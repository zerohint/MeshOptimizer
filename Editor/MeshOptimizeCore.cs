using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityMeshSimplifier;

namespace MeshOptimizer
{
    /// <summary>
    /// Per-mesh simplification settings. Mirrors UnityMeshSimplifier's SimplificationOptions
    /// plus the extras the window needs (target quality, post-processing).
    /// </summary>
    [Serializable]
    public class OptimizeSettings
    {
        [Range(0.01f, 1f)] public float quality = 0.5f;

        public bool preserveBorderEdges = true;
        // Locking UV seams stops almost all collapses on hard-surface meshes whose UVs are
        // split per face (measured: 99.8% of triangles kept even at quality 0.05), so it is off
        // by default. Turn it on for meshes that visibly smear their textures.
        public bool preserveUVSeamEdges = false;
        public bool preserveUVFoldoverEdges = false;
        public bool preserveSurfaceCurvature = true;

        public bool enableSmartLink = true;
        public float vertexLinkDistance = 0f;   // 0 -> double.Epsilon
        public int maxIterationCount = 100;
        public float agressiveness = 7f;

        public bool manualUVComponentCount = false;
        [Range(0, 4)] public int uvComponentCount = 2;

        // Normals are left alone by default: recalculating them averages across hard edges and
        // flattens the shading of hard-surface props. Tangents are cheap to rebuild and keep
        // normal maps correct after vertices move, so they are on.
        public bool recalculateNormals = false;
        public bool recalculateTangents = true;
        public bool optimizeForRendering = true; // MeshUtility.Optimize (vertex cache order)

        public OptimizeSettings Clone()
        {
            return (OptimizeSettings)MemberwiseClone();
        }

        public SimplificationOptions ToSimplificationOptions()
        {
            return new SimplificationOptions
            {
                PreserveBorderEdges = preserveBorderEdges,
                PreserveUVSeamEdges = preserveUVSeamEdges,
                PreserveUVFoldoverEdges = preserveUVFoldoverEdges,
                PreserveSurfaceCurvature = preserveSurfaceCurvature,
                EnableSmartLink = enableSmartLink,
                VertexLinkDistance = vertexLinkDistance <= 0f ? double.Epsilon : vertexLinkDistance,
                MaxIterationCount = Mathf.Max(1, maxIterationCount),
                Agressiveness = Mathf.Max(0.01f, agressiveness),
                ManualUVComponentCount = manualUVComponentCount,
                UVComponentCount = uvComponentCount
            };
        }

        public bool SameAs(OptimizeSettings o)
        {
            return o != null
                && Mathf.Approximately(quality, o.quality)
                && preserveBorderEdges == o.preserveBorderEdges
                && preserveUVSeamEdges == o.preserveUVSeamEdges
                && preserveUVFoldoverEdges == o.preserveUVFoldoverEdges
                && preserveSurfaceCurvature == o.preserveSurfaceCurvature
                && enableSmartLink == o.enableSmartLink
                && Mathf.Approximately(vertexLinkDistance, o.vertexLinkDistance)
                && maxIterationCount == o.maxIterationCount
                && Mathf.Approximately(agressiveness, o.agressiveness)
                && manualUVComponentCount == o.manualUVComponentCount
                && uvComponentCount == o.uvComponentCount
                && recalculateNormals == o.recalculateNormals
                && recalculateTangents == o.recalculateTangents
                && optimizeForRendering == o.optimizeForRendering;
        }
    }

    /// <summary>Read-only snapshot of a mesh, used for the before/after stat columns.</summary>
    public struct MeshStats
    {
        public int triangles;
        public int vertices;
        public int subMeshes;
        public int uvChannels;
        public bool normals, tangents, colors, boneWeights, blendShapes;
        public Vector3 size;

        public static MeshStats From(Mesh m)
        {
            var s = new MeshStats();
            if (m == null)
                return s;

            for (int i = 0; i < m.subMeshCount; i++)
                s.triangles += (int)(m.GetIndexCount(i) / 3);

            s.vertices = m.vertexCount;
            s.subMeshes = m.subMeshCount;
            s.normals = m.HasVertexAttribute(VertexAttribute.Normal);
            s.tangents = m.HasVertexAttribute(VertexAttribute.Tangent);
            s.colors = m.HasVertexAttribute(VertexAttribute.Color);
            s.boneWeights = m.HasVertexAttribute(VertexAttribute.BlendWeight);
            s.blendShapes = m.blendShapeCount > 0;
            s.size = m.bounds.size;

            for (int c = 0; c < 8; c++)
            {
                if (m.HasVertexAttribute(VertexAttribute.TexCoord0 + c))
                    s.uvChannels++;
            }

            return s;
        }

        public string Attributes
        {
            get
            {
                var parts = new List<string>(5);
                if (normals) parts.Add("N");
                if (tangents) parts.Add("T");
                if (colors) parts.Add("C");
                if (boneWeights) parts.Add("Skin");
                if (blendShapes) parts.Add("Blend");
                parts.Add(uvChannels + "xUV");
                return string.Join(" ", parts);
            }
        }
    }

    /// <summary>Simplification + asset writing. No UI, so it can be reused from batch scripts.</summary>
    public static class MeshOptimizeCore
    {
        /// <summary>
        /// Builds a simplified copy of <paramref name="src"/>. Returns null and fills
        /// <paramref name="error"/> when the mesh cannot be processed.
        /// The result is an in-memory mesh; call <see cref="WriteMeshAsset"/> to persist it.
        /// </summary>
        public static Mesh Simplify(Mesh src, OptimizeSettings settings, out string error)
        {
            var job = SimplifyJob.Start(src, settings);
            var mesh = job.Complete();
            error = job.Error;
            return mesh;
        }

        /// <summary>
        /// Runs the collapse loop on a worker thread so the editor stays responsive.
        /// Mesh reads (Initialize) and mesh creation (Complete) happen on the main thread;
        /// only the pure array math is threaded.
        /// </summary>
        public class SimplifyJob
        {
            readonly MeshSimplifier simplifier;
            readonly OptimizeSettings settings;
            readonly float quality;
            readonly System.Threading.Tasks.Task task;

            public Mesh Source { get; }
            public string Error { get; private set; }

            /// <summary>Incremented by the caller to detect stale jobs; see the window.</summary>
            public int Generation { get; set; }

            public bool IsDone => Error != null || task == null || task.IsCompleted;

            SimplifyJob(Mesh source, MeshSimplifier simplifier, OptimizeSettings settings, string error)
            {
                Source = source;
                this.simplifier = simplifier;
                this.settings = settings;
                Error = error;

                if (simplifier == null)
                    return;

                quality = Mathf.Clamp01(settings.quality);
                task = System.Threading.Tasks.Task.Run(() =>
                {
                    simplifier.SimplifyMesh(quality);
                });
            }

            /// <summary>Kicks off simplification. Always returns a job; check Error/IsDone.</summary>
            public static SimplifyJob Start(Mesh source, OptimizeSettings settings)
            {
                if (source == null)
                    return new SimplifyJob(null, null, settings, "Source mesh is null.");

                try
                {
                    var simplifier = new MeshSimplifier();
                    simplifier.SimplificationOptions = settings.ToSimplificationOptions();
                    simplifier.Initialize(source); // main thread: reads mesh data
                    return new SimplifyJob(source, simplifier, settings, null);
                }
                catch (Exception e)
                {
                    return new SimplifyJob(source, null, settings, DescribeError(source, e));
                }
            }

            /// <summary>Main thread. Builds the resulting mesh once the worker has finished.</summary>
            public Mesh Complete()
            {
                if (Error != null)
                    return null;

                try
                {
                    task.Wait();

                    var dst = simplifier.ToMesh();
                    dst.name = source_name();

                    if (settings.recalculateNormals)
                        dst.RecalculateNormals();
                    if (settings.recalculateTangents)
                        dst.RecalculateTangents();

                    dst.RecalculateBounds();

                    if (settings.optimizeForRendering)
                        MeshUtility.Optimize(dst);

                    return dst;
                }
                catch (Exception e)
                {
                    Error = DescribeError(Source, e);
                    return null;
                }
            }

            string source_name() => Source.name.EndsWith("_opt") ? Source.name : Source.name + "_opt";
        }

        static string DescribeError(Mesh src, Exception e)
        {
            // Asset meshes are readable from the Editor even with Read/Write disabled,
            // so a failure here is usually something else - but mention it just in case.
            var inner = e is AggregateException agg && agg.InnerException != null ? agg.InnerException : e;
            return src != null && !src.isReadable
                ? inner.Message + " (mesh has Read/Write disabled on its importer)"
                : inner.Message;
        }

        /// <summary>Writes <paramref name="mesh"/> under <paramref name="folder"/>, returns the asset path.</summary>
        public static string WriteMeshAsset(Mesh mesh, string folder, string preferredName)
        {
            EnsureFolder(folder);

            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SanitizeFileName(preferredName)}.asset");
            mesh.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(mesh, path);
            return path;
        }

        /// <summary>Overwrites an existing mesh asset in place so prefab references survive.</summary>
        public static void OverwriteMeshAsset(string assetPath, Mesh newMesh)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existing == null)
                return;

            var keptName = existing.name;
            existing.Clear(false);
            CopyMeshInto(newMesh, existing);
            existing.name = keptName;
            EditorUtility.SetDirty(existing);
        }

        static void CopyMeshInto(Mesh src, Mesh dst)
        {
            dst.indexFormat = src.indexFormat;
            dst.vertices = src.vertices;
            if (src.HasVertexAttribute(VertexAttribute.Normal)) dst.normals = src.normals;
            if (src.HasVertexAttribute(VertexAttribute.Tangent)) dst.tangents = src.tangents;
            if (src.HasVertexAttribute(VertexAttribute.Color)) dst.colors = src.colors;
            if (src.HasVertexAttribute(VertexAttribute.BlendWeight)) dst.boneWeights = src.boneWeights;

            for (int c = 0; c < 8; c++)
            {
                if (!src.HasVertexAttribute(VertexAttribute.TexCoord0 + c))
                    continue;

                var uvs = new List<Vector4>();
                src.GetUVs(c, uvs);
                dst.SetUVs(c, uvs);
            }

            dst.bindposes = src.bindposes;
            dst.subMeshCount = src.subMeshCount;
            for (int i = 0; i < src.subMeshCount; i++)
                dst.SetTriangles(src.GetTriangles(i), i, false);

            dst.RecalculateBounds();
        }

        public static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder))
                return;

            var parts = folder.Split('/');
            var current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        public static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        /// <summary>What a mesh currently referenced by a prefab actually is.</summary>
        public struct ReferencedMeshInfo
        {
            public Mesh source;            // mesh to simplify from (the original)
            public string overwritePath;    // set when `referenced` is one of our own outputs
            public bool orphanOutput;       // one of our outputs, but the original is unknown
            public MeshOptimizerDatabase.MeshRecord produced;
        }

        /// <summary>
        /// Resolves a prefab's mesh reference back to the original it came from. When the prefab
        /// already points at a mesh we generated, this recovers the source from the cache so a
        /// re-tune regenerates from the original and overwrites the same asset (no *_opt_opt).
        /// </summary>
        public static ReferencedMeshInfo InspectReferencedMesh(Mesh referenced, string outputFolder, MeshOptimizerDatabase db)
        {
            var info = new ReferencedMeshInfo { source = referenced };
            if (referenced == null)
                return info;

            var assetPath = AssetDatabase.GetAssetPath(referenced) ?? "";
            if (!assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return info; // imported model mesh - always an original

            // The database is project-wide, so provenance is found no matter which output folder
            // the mesh was written to in an earlier session.
            var produced = (db ?? MeshOptimizerDatabase.Instance).FindByOutput(assetPath);
            if (produced == null)
            {
                var outPrefix = (outputFolder ?? "").TrimEnd('/') + "/";
                var underOutput = !string.IsNullOrEmpty(outputFolder) &&
                                  assetPath.StartsWith(outPrefix, StringComparison.OrdinalIgnoreCase);

                // Inside the output folder but unknown to the database: we cannot tell what it
                // came from, so refuse to compound the decimation.
                info.orphanOutput = underOutput;
                return info;
            }

            var original = LoadMeshFromKey(produced.key);
            if (original == null)
            {
                info.orphanOutput = true;
                return info;
            }

            info.source = original;
            info.overwritePath = assetPath;
            info.produced = produced;
            return info;
        }

        /// <summary>Call when starting a run so provenance is re-read from disk.</summary>
        public static void ClearProvenanceCache()
        {
            MeshOptimizerDatabase.Reload();
        }

        /// <summary>Loads the original mesh a cache key points at, or null if it is gone.</summary>
        public static Mesh LoadMeshFromKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;

            var split = key.Split('|');
            if (split.Length != 2)
                return null;

            var path = AssetDatabase.GUIDToAssetPath(split[0]);
            if (string.IsNullOrEmpty(path))
                return null;

            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().FirstOrDefault(m => m.name == split[1]);
        }

        /// <summary>Stable key for a mesh sub-asset: "{guid}|{meshName}".</summary>
        public static string MeshKey(Mesh mesh)
        {
            if (mesh == null)
                return null;

            var path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path))
                return null;

            return AssetDatabase.AssetPathToGUID(path) + "|" + mesh.name;
        }

        /// <summary>One mesh to persist: either a freshly generated mesh or a reuse of an existing asset.</summary>
        public class ApplyRequest
        {
            public Mesh source;            // ORIGINAL mesh the result is generated from
            public Mesh referenced;        // mesh the prefab currently points at (defaults to source)
            public Mesh generated;         // in-memory simplified mesh (null when reusing)
            public string reuseAssetPath;  // set to reuse an already written optimized asset as-is
            public string overwritePath;   // set to overwrite this asset instead of creating a new one
            public string cacheKey;
            public OptimizeSettings settings;
        }

        /// <summary>An edited LOD group, addressed by its transform path inside the prefab.</summary>
        public class LodEdit
        {
            public string path;
            public float[] heights;
            public LODFadeMode fadeMode;
            public bool animateCrossFading;
        }

        /// <summary>
        /// Writes the generated meshes into <paramref name="outputFolder"/> and returns the
        /// source mesh -> optimized mesh mapping to apply to prefabs. Records everything in
        /// <paramref name="db"/> so meshes shared between prefabs are generated only once.
        /// </summary>
        public static Dictionary<Mesh, Mesh> PersistMeshes(IEnumerable<ApplyRequest> requests, string outputFolder,
            MeshOptimizerDatabase db, bool updateSharedMeshInPlace)
        {
            var mapping = new Dictionary<Mesh, Mesh>();
            EnsureFolder(outputFolder);

            foreach (var req in requests)
            {
                if (req?.source == null)
                    continue;

                var referenced = req.referenced ?? req.source;

                if (!string.IsNullOrEmpty(req.reuseAssetPath))
                {
                    var existing = AssetDatabase.LoadAssetAtPath<Mesh>(req.reuseAssetPath);
                    if (existing != null)
                        mapping[referenced] = existing;
                    continue;
                }

                if (req.generated == null)
                    continue;

                var cached = db?.Find(req.cacheKey);
                var overwrite = req.overwritePath;

                // Prefer an explicit overwrite target, then the cached asset for this source mesh.
                if (string.IsNullOrEmpty(overwrite) && updateSharedMeshInPlace && cached != null)
                    overwrite = cached.outputPath;

                if (!string.IsNullOrEmpty(overwrite) && AssetDatabase.LoadAssetAtPath<Mesh>(overwrite) != null)
                {
                    // Overwrite in place so every prefab already pointing at it picks up the change,
                    // and no *_opt_opt duplicate is produced when re-tuning.
                    OverwriteMeshAsset(overwrite, req.generated);
                    mapping[referenced] = AssetDatabase.LoadAssetAtPath<Mesh>(overwrite);
                    db?.RecordMesh(req.cacheKey, req.source.name, overwrite, req.settings,
                        MeshStats.From(req.source).triangles, MeshStats.From(req.generated).triangles);
                }
                else
                {
                    req.generated.hideFlags = HideFlags.None;
                    var baseName = req.source.name.EndsWith("_opt") ? req.source.name : req.source.name + "_opt";
                    var path = WriteMeshAsset(req.generated, outputFolder, baseName);
                    mapping[referenced] = req.generated;
                    db?.RecordMesh(req.cacheKey, req.source.name, path, req.settings,
                        MeshStats.From(req.source).triangles, MeshStats.From(req.generated).triangles);
                }
            }

            AssetDatabase.SaveAssets();
            db?.Save();
            return mapping;
        }

        /// <summary>
        /// Re-points every renderer (and optionally MeshCollider) in the prefab at the optimized
        /// meshes and writes the LOD edits. Returns false when the prefab could not be saved.
        /// </summary>
        public static bool ApplyToPrefab(string prefabPath, IDictionary<Mesh, Mesh> mapping, bool includeColliders,
            IList<LodEdit> lodEdits, out int replacedReferences, out string error)
        {
            replacedReferences = 0;
            error = null;

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null)
            {
                error = "Could not load prefab contents.";
                return false;
            }

            try
            {
                if (mapping != null && mapping.Count > 0)
                {
                    foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.sharedMesh != null && mapping.TryGetValue(mf.sharedMesh, out var repl) && repl != null)
                        {
                            mf.sharedMesh = repl;
                            replacedReferences++;
                        }
                    }

                    foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (smr.sharedMesh != null && mapping.TryGetValue(smr.sharedMesh, out var repl) && repl != null)
                        {
                            smr.sharedMesh = repl;
                            replacedReferences++;
                        }
                    }

                    if (includeColliders)
                    {
                        foreach (var mc in root.GetComponentsInChildren<MeshCollider>(true))
                        {
                            if (mc.sharedMesh != null && mapping.TryGetValue(mc.sharedMesh, out var repl) && repl != null)
                            {
                                mc.sharedMesh = repl;
                                replacedReferences++;
                            }
                        }
                    }
                }

                if (lodEdits != null)
                {
                    foreach (var edit in lodEdits)
                    {
                        var t = string.IsNullOrEmpty(edit.path) ? root.transform : root.transform.Find(edit.path);
                        var group = t != null ? t.GetComponent<LODGroup>() : null;
                        if (group == null)
                        {
                            Debug.LogWarning($"[MeshOptimizer] LODGroup at '{edit.path}' not found in {prefabPath}; LOD edits skipped.");
                            continue;
                        }

                        var levels = group.GetLODs();
                        for (int i = 0; i < levels.Length && i < edit.heights.Length; i++)
                            levels[i].screenRelativeTransitionHeight = edit.heights[i];

                        group.SetLODs(levels);
                        group.fadeMode = edit.fadeMode;
                        group.animateCrossFading = edit.animateCrossFading;
                    }
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out var success);
                if (!success)
                    error = "SaveAsPrefabAsset reported failure.";
                return success;
            }
            catch (Exception e)
            {
                error = e.ToString();
                return false;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static string FormatDelta(int before, int after)
        {
            if (before <= 0)
                return "-";

            var pct = 100f * after / before;
            return $"{after:N0} ({pct:0.#}% of {before:N0})";
        }
    }

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
