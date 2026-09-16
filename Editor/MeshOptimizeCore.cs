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
}
