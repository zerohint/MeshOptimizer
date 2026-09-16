using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MeshOptimizer
{
    // Scanning the folder, moving through the queue, building mesh entries, generating and
    // applying results.
    public partial class PrefabMeshOptimizerWindow
    {
        void Scan()
        {
            ClearEntries();
            state.queue.Clear();
            state.statuses.Clear();

            var paths = AssetDatabase.FindAssets("t:Prefab", new[] { state.sourceFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                .Where(p => state.includeSubfolders || Path.GetDirectoryName(p).Replace('\\', '/') == state.sourceFolder)
                .Where(p => !p.StartsWith(state.outputFolder.TrimEnd('/') + "/"))
                .OrderBy(p => p)
                .ToList();

            if (state.skipPrefabsWithoutMeshes)
            {
                paths = paths.Where(p =>
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                    return go != null && CollectMeshRenderers(go)
                        .Any(r => MeshStats.From(MeshOf(r)).triangles >= state.minTriangles);
                }).ToList();
            }

            db = MeshOptimizerDatabase.Reload();

            state.queue.AddRange(paths);
            state.statuses.AddRange(paths.Select(StatusFromDatabase));

            MeshOptimizeCore.EnsureFolder(state.outputFolder);
            MeshOptimizeCore.ClearProvenanceCache();
            db = MeshOptimizerDatabase.Instance;

            state.index = -1;
            if (state.queue.Count == 0)
                Debug.LogWarning($"[MeshOptimizer] No prefabs with meshes found in {state.sourceFolder}");
            else
                Goto(0);

            SaveState();
        }

        int StatusFromDatabase(string prefabPath)
        {
            var record = (db ?? MeshOptimizerDatabase.Instance).FindPrefab(prefabPath);
            if (record == null)
                return (int)PrefabStatus.Pending;

            return record.status == "applied" ? (int)PrefabStatus.Applied
                 : record.status == "skipped" ? (int)PrefabStatus.Skipped
                 : (int)PrefabStatus.Pending;
        }

        void Goto(int i)
        {
            if (state.queue.Count == 0)
                return;

            ClearEntries();
            state.index = Mathf.Clamp(i, 0, state.queue.Count - 1);
            LoadCurrent();
            SaveState();
            Repaint();
        }

        void LoadCurrent()
        {
            currentPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(state.queue[state.index]);
            entries.Clear();
            lods.Clear();
            selected = -1;
            hiddenByThreshold = 0;

            if (currentPrefab == null)
                return;

            db = db ?? MeshOptimizerDatabase.Instance;

            // Built alongside the renderer -> LOD level map below so each mesh entry can jump
            // straight to its LOD's switch height without needing the LOD Groups foldout open.
            var lodLevelOf = new Dictionary<Renderer, (int level, LodInfo lod)>();

            foreach (var group in currentPrefab.GetComponentsInChildren<LODGroup>(true))
            {
                var levels = group.GetLODs();
                var info = new LodInfo
                {
                    path = TransformPath(group.transform, currentPrefab.transform),
                    name = group.gameObject.name + $" (LODGroup, {levels.Length} levels)",
                    heights = levels.Select(l => l.screenRelativeTransitionHeight).ToArray(),
                    fadeMode = group.fadeMode,
                    animateCrossFading = group.animateCrossFading,
                    rendererCounts = levels.Select(l => l.renderers?.Count(r => r != null) ?? 0).ToArray(),
                    triangleCounts = levels.Select(l => l.renderers == null
                        ? 0
                        : l.renderers.Where(r => r != null).Sum(r => MeshStats.From(MeshOf(r)).triangles)).ToArray()
                };
                lods.Add(info);

                for (int i = 0; i < levels.Length; i++)
                {
                    if (levels[i].renderers == null)
                        continue;

                    foreach (var r in levels[i].renderers)
                    {
                        if (r != null && !lodLevelOf.ContainsKey(r))
                            lodLevelOf.Add(r, (i, info));
                    }
                }
            }

            var colliderUse = new Dictionary<Mesh, int>();
            foreach (var mc in currentPrefab.GetComponentsInChildren<MeshCollider>(true))
            {
                if (mc.sharedMesh == null)
                    continue;

                colliderUse.TryGetValue(mc.sharedMesh, out var n);
                colliderUse[mc.sharedMesh] = n + 1;
            }

            var byMesh = new Dictionary<Mesh, MeshEntry>();

            foreach (var r in CollectMeshRenderers(currentPrefab))
            {
                var mesh = MeshOf(r);
                if (mesh == null)
                    continue;

                if (MeshStats.From(mesh).triangles < state.minTriangles)
                {
                    if (!byMesh.ContainsKey(mesh))
                    {
                        byMesh.Add(mesh, null);
                        hiddenByThreshold++;
                    }
                    continue;
                }

                if (!byMesh.TryGetValue(mesh, out var entry) || entry == null)
                {
                    entry = BuildEntry(mesh, r);
                    entry.colliderUseCount = colliderUse.TryGetValue(mesh, out var cu) ? cu : 0;
                    byMesh[mesh] = entry;
                    entries.Add(entry);
                }

                entry.useCount++;
                if (lodLevelOf.TryGetValue(r, out var lvl) && entry.lodLevel < 0)
                {
                    entry.lodLevel = lvl.level;
                    entry.lodLabel = "LOD" + lvl.level;
                    entry.lod = lvl.lod;
                }
            }

            entries = entries.OrderByDescending(e => e.sourceStats.triangles).ToList();
            if (entries.Count > 0)
                Select(0);
        }

        MeshEntry BuildEntry(Mesh referenced, Renderer renderer)
        {
            var entry = new MeshEntry
            {
                referenced = referenced,
                source = referenced,
                referencedStats = MeshStats.From(referenced),
                label = TransformPath(renderer.transform, currentPrefab.transform),
                materials = renderer.sharedMaterials,
                skinned = renderer is SkinnedMeshRenderer
            };

            var info = MeshOptimizeCore.InspectReferencedMesh(referenced, state.outputFolder, db);

            if (info.orphanOutput)
            {
                entry.isOrphanOutput = true;
                entry.key = MeshOptimizeCore.MeshKey(referenced);
                entry.settings = sticky.Clone();
                entry.include = false;
            }
            else if (!string.IsNullOrEmpty(info.overwritePath))
            {
                // The prefab already points at one of our optimized assets: regenerate from the
                // recovered original and overwrite that same asset.
                entry.source = info.source;
                entry.key = info.produced.key;
                entry.overwritePath = info.overwritePath;
                entry.cacheEntry = info.produced;

                var recorded = MeshOptimizerDatabase.SettingsOfEntry(info.produced);
                entry.settings = (recorded ?? sticky).Clone();
                entry.touched = recorded != null;    // keep the settings this asset was made with
                entry.reuseCache = false;
                entry.include = true;
            }
            else
            {
                entry.key = MeshOptimizeCore.MeshKey(referenced);
                entry.cacheEntry = db.Find(entry.key);

                var recorded = db.SettingsOf(entry.cacheEntry);
                entry.settings = (recorded ?? sticky).Clone();
                entry.touched = recorded != null;
                entry.reuseCache = entry.cacheEntry != null;
            }

            entry.sourceStats = MeshStats.From(entry.source);
            return entry;
        }

        void Select(int i)
        {
            selected = i;
            var e = Current;
            if (e == null)
                return;

            // Carry the settings the user last tuned over to meshes they have not touched yet.
            if (!e.touched && !e.settings.SameAs(sticky))
            {
                e.settings = sticky.Clone();
                e.needsRebuild = true;
            }

            if (e.needsRebuild && !e.reuseCache && state.autoRebuild && e.sourceStats.triangles <= AutoRebuildTriangleLimit)
                dirtyStamp = EditorApplication.timeSinceStartup;
        }

        void RequestRebuild(MeshEntry e)
        {
            if (e == null)
                return;

            e.needsRebuild = true;
            dirtyStamp = EditorApplication.timeSinceStartup;
        }

        void StartJob(MeshEntry e)
        {
            if (e?.source == null)
                return;

            if (TryUseCachedResult(e))
                return;

            e.error = null;
            e.needsRebuild = false;
            e.jobSettings = e.settings.Clone();
            e.job = MeshOptimizeCore.SimplifyJob.Start(e.source, e.jobSettings);
        }

        /// <summary>Blocking variant used by Generate All, where a progress bar is shown anyway.</summary>
        void RebuildNow(MeshEntry e)
        {
            if (e?.source == null)
                return;

            if (TryUseCachedResult(e))
                return;

            StartJob(e);
            var job = e.job;
            e.job = null;

            var result = job.Complete();
            e.error = job.Error;
            var stats = MeshStats.From(result);
            if (result != null)
            {
                result.hideFlags = HideFlags.DontSave;
                CacheResult(e, e.jobSettings, result, stats);
            }
            e.result = result;
            e.resultStats = stats;
        }

        void GenerateAll()
        {
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (!e.include || e.reuseCache || !e.needsRebuild)
                        continue;

                    EditorUtility.DisplayProgressBar("Mesh Optimizer", $"Simplifying {e.source.name}", (float)i / entries.Count);
                    RebuildNow(e);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void CopySettingsToAll(MeshEntry src)
        {
            foreach (var e in entries)
            {
                if (e == src || e.reuseCache)
                    continue;

                e.settings = src.settings.Clone();
                e.touched = true;
                e.smart = false;
                e.preSmartSettings = null;
                e.needsRebuild = true;
            }

            RequestRebuild(Current);
        }

        void ApplyCurrent(bool advance)
        {
            var prefabPath = state.queue[state.index];
            db = db ?? MeshOptimizerDatabase.Instance;

            var requests = new List<MeshOptimizeCore.ApplyRequest>();
            foreach (var e in entries)
            {
                if (!e.include || e.source == null)
                    continue;

                if (e.reuseCache && e.cacheEntry != null)
                {
                    requests.Add(new MeshOptimizeCore.ApplyRequest
                    {
                        source = e.source,
                        referenced = e.referenced,
                        reuseAssetPath = e.cacheEntry.outputPath,
                        cacheKey = e.key,
                        settings = e.settings
                    });
                    continue;
                }

                if (e.result == null)
                    continue;

                requests.Add(new MeshOptimizeCore.ApplyRequest
                {
                    source = e.source,
                    referenced = e.referenced,
                    generated = e.result,
                    overwritePath = e.overwritePath,
                    cacheKey = e.key,
                    settings = e.settings
                });
            }

            var mapping = MeshOptimizeCore.PersistMeshes(requests, state.outputFolder, db, state.updateSharedMeshInPlace);

            foreach (var e in entries)
                e.cacheEntry = db.Find(e.key) ?? e.cacheEntry;

            var lodEdits = lods.Where(l => l.changed)
                               .Select(l => new MeshOptimizeCore.LodEdit
                               {
                                   path = l.path,
                                   heights = l.heights,
                                   fadeMode = l.fadeMode,
                                   animateCrossFading = l.animateCrossFading
                               })
                               .ToList();

            var ok = MeshOptimizeCore.ApplyToPrefab(prefabPath, mapping, state.alsoReplaceColliderMeshes, lodEdits,
                out var replaced, out var error);

            state.statuses[state.index] = (int)(ok ? PrefabStatus.Applied : PrefabStatus.Failed);
            if (!ok)
                Debug.LogError($"[MeshOptimizer] Failed on {prefabPath}: {error}");

            var applied = entries.Where(e => e.referenced != null && mapping.ContainsKey(e.referenced)).ToList();
            var before = applied.Sum(e => e.sourceStats.triangles);
            var after = applied.Sum(e => MeshStats.From(mapping[e.referenced]).triangles);
            Debug.Log($"[MeshOptimizer] {Path.GetFileNameWithoutExtension(prefabPath)}: {mapping.Count} mesh(es), " +
                      $"{replaced} reference(s) re-pointed, {before:N0} -> {after:N0} tris -> {state.outputFolder}");

            if (ok)
            {
                db.RecordPrefab(prefabPath, "applied", mapping.Count, before, after);
                db.Save();
            }

            if (advance)
            {
                if (state.index < state.queue.Count - 1)
                {
                    Goto(state.index + 1);
                }
                else
                {
                    Goto(state.index);
                    Debug.Log("[MeshOptimizer] Reached the end of the queue.");
                }
            }

            SaveState();
        }
    }
}
