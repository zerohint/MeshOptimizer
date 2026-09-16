using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MeshOptimizer
{
    /// <summary>
    /// Project-wide record of everything the optimizer has done: which source mesh produced which
    /// optimized asset (with the settings used), and which prefabs are already handled.
    ///
    /// Lives in ProjectSettings/MeshOptimizer.json - one file for the whole project, versioned by
    /// git like the rest of ProjectSettings, so provenance and progress are shared with the team
    /// instead of hiding next to the meshes.
    /// </summary>
    [Serializable]
    public class MeshOptimizerDatabase
    {
        public const string FilePath = "ProjectSettings/MeshOptimizer.json";
        const string LegacyFileName = "_optimizer_cache.json";

        [Serializable]
        public class MeshRecord
        {
            public string key;              // MeshOptimizeCore.MeshKey of the SOURCE mesh
            public string sourceName;
            public string outputPath;       // the optimized asset produced from it
            public string settingsJson;
            public int originalTriangles;
            public int optimizedTriangles;
            public string updatedUtc;
        }

        [Serializable]
        public class PrefabRecord
        {
            public string guid;
            public string path;             // kept for readable diffs; guid is the identity
            public string status;           // "applied" or "skipped"
            public int meshCount;
            public int trianglesBefore;
            public int trianglesAfter;
            public string updatedUtc;
        }

        public int version = 1;
        public List<MeshRecord> meshes = new List<MeshRecord>();
        public List<PrefabRecord> prefabs = new List<PrefabRecord>();

        static MeshOptimizerDatabase instance;

        public static MeshOptimizerDatabase Instance
        {
            get
            {
                if (instance == null)
                    instance = LoadFromDisk();
                return instance;
            }
        }

        /// <summary>Re-reads the file, e.g. after a git pull or at the start of a run.</summary>
        public static MeshOptimizerDatabase Reload()
        {
            instance = LoadFromDisk();
            return instance;
        }

        static MeshOptimizerDatabase LoadFromDisk()
        {
            MeshOptimizerDatabase db = null;

            if (File.Exists(FilePath))
            {
                try { db = JsonUtility.FromJson<MeshOptimizerDatabase>(File.ReadAllText(FilePath)); }
                catch (Exception e) { Debug.LogWarning($"[MeshOptimizer] Could not read {FilePath}: {e.Message}"); }
            }

            db = db ?? new MeshOptimizerDatabase();
            var migrated = db.MigrateLegacyCaches();
            var dropped = db.meshes.RemoveAll(m => AssetDatabase.LoadAssetAtPath<Mesh>(m.outputPath) == null);

            if (migrated > 0 || dropped > 0)
                db.Save();

            return db;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonUtility.ToJson(this, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MeshOptimizer] Could not write {FilePath}: {e.Message}");
            }
        }

        /// <summary>Folds any old per-folder cache files into this database, once.</summary>
        int MigrateLegacyCaches()
        {
            var imported = 0;
            string[] files;

            try { files = Directory.GetFiles(Application.dataPath, LegacyFileName, SearchOption.AllDirectories); }
            catch { return 0; }

            foreach (var file in files)
            {
                LegacyCache legacy = null;
                try { legacy = JsonUtility.FromJson<LegacyCache>(File.ReadAllText(file)); }
                catch { continue; }

                if (legacy?.entries == null)
                    continue;

                foreach (var e in legacy.entries)
                {
                    if (string.IsNullOrEmpty(e.key) || Find(e.key) != null)
                        continue;

                    meshes.Add(new MeshRecord
                    {
                        key = e.key,
                        sourceName = e.sourceName,
                        outputPath = e.outputPath,
                        settingsJson = e.settingsJson,
                        updatedUtc = DateTime.UtcNow.ToString("o")
                    });
                    imported++;
                }
            }

            if (imported > 0)
            {
                Debug.Log($"[MeshOptimizer] Imported {imported} record(s) from {files.Length} legacy " +
                          $"{LegacyFileName} file(s) into {FilePath}. The old files are no longer read and can be deleted.");
            }

            return imported;
        }

        [Serializable]
        class LegacyCache
        {
            [Serializable]
            public class Entry
            {
                public string key, sourceName, outputPath, settingsJson;
            }

            public List<Entry> entries;
        }

        // ---- meshes

        public MeshRecord Find(string key) =>
            string.IsNullOrEmpty(key) ? null : meshes.FirstOrDefault(m => m.key == key);

        /// <summary>Finds the record that produced a given optimized asset.</summary>
        public MeshRecord FindByOutput(string outputPath) => string.IsNullOrEmpty(outputPath)
            ? null
            : meshes.FirstOrDefault(m => string.Equals(m.outputPath, outputPath, StringComparison.OrdinalIgnoreCase));

        public void RecordMesh(string key, string sourceName, string outputPath, OptimizeSettings settings,
            int originalTriangles, int optimizedTriangles)
        {
            if (string.IsNullOrEmpty(key))
                return;

            var record = Find(key);
            if (record == null)
            {
                record = new MeshRecord { key = key };
                meshes.Add(record);
            }

            record.sourceName = sourceName;
            record.outputPath = outputPath;
            record.settingsJson = JsonUtility.ToJson(settings);
            record.originalTriangles = originalTriangles;
            record.optimizedTriangles = optimizedTriangles;
            record.updatedUtc = DateTime.UtcNow.ToString("o");
        }

        public OptimizeSettings SettingsOf(MeshRecord record) => SettingsOfEntry(record);

        public static OptimizeSettings SettingsOfEntry(MeshRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.settingsJson))
                return null;

            try { return JsonUtility.FromJson<OptimizeSettings>(record.settingsJson); }
            catch { return null; }
        }

        // ---- prefabs

        public PrefabRecord FindPrefab(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath))
                return null;

            var guid = AssetDatabase.AssetPathToGUID(prefabPath);
            return string.IsNullOrEmpty(guid) ? null : prefabs.FirstOrDefault(p => p.guid == guid);
        }

        public void RecordPrefab(string prefabPath, string status, int meshCount, int trianglesBefore, int trianglesAfter)
        {
            var guid = AssetDatabase.AssetPathToGUID(prefabPath);
            if (string.IsNullOrEmpty(guid))
                return;

            var record = prefabs.FirstOrDefault(p => p.guid == guid);
            if (record == null)
            {
                record = new PrefabRecord { guid = guid };
                prefabs.Add(record);
            }

            record.path = prefabPath;
            record.status = status;
            record.meshCount = meshCount;
            record.trianglesBefore = trianglesBefore;
            record.trianglesAfter = trianglesAfter;
            record.updatedUtc = DateTime.UtcNow.ToString("o");
        }

        public void ForgetPrefab(string prefabPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(prefabPath);
            if (!string.IsNullOrEmpty(guid))
                prefabs.RemoveAll(p => p.guid == guid);
        }
    }
}
