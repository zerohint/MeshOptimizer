using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MeshOptimizer
{
    // Small stateless/reusable helpers: renderer collection, transform paths, preview lifecycle.
    public partial class PrefabMeshOptimizerWindow
    {
        static string DefaultOutputFor(string prefabFolder)
        {
            return prefabFolder.TrimEnd('/') + "/OptimizedMeshes";
        }

        static List<Renderer> CollectMeshRenderers(GameObject root)
        {
            var list = new List<Renderer>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if ((r is MeshRenderer || r is SkinnedMeshRenderer) && MeshOf(r) != null)
                    list.Add(r);
            }
            return list;
        }

        static Mesh MeshOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr)
                return smr.sharedMesh;

            var mf = r != null ? r.GetComponent<MeshFilter>() : null;
            return mf != null ? mf.sharedMesh : null;
        }

        static string TransformPath(Transform t, Transform root)
        {
            if (t == root)
                return "";

            var parts = new List<string>();
            while (t != null && t != root)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        void EnsurePreview()
        {
            if (preview == null)
                preview = new PreviewRenderUtility();
        }

        void DisposePreview()
        {
            if (preview != null)
            {
                preview.Cleanup();
                preview = null;
            }
        }

        static void DestroyIfTemporary(Mesh m)
        {
            if (m != null && !AssetDatabase.Contains(m))
                DestroyImmediate(m);
        }

        /// <summary>Looks for a cached result matching the entry's current settings and, if found,
        /// restores it onto the entry so no simplification job is needed.</summary>
        static bool TryUseCachedResult(MeshEntry e)
        {
            foreach (var c in e.resultCache)
            {
                if (!c.settings.SameAs(e.settings))
                    continue;

                e.result = c.result;
                e.resultStats = c.stats;
                e.error = null;
                e.needsRebuild = false;
                return true;
            }

            return false;
        }

        /// <summary>Remembers a freshly generated result under the settings that produced it, so
        /// re-selecting that exact combo later reuses it instead of re-simplifying.</summary>
        static void CacheResult(MeshEntry e, OptimizeSettings settings, Mesh result, MeshStats stats)
        {
            while (e.resultCache.Count >= MaxResultCachePerEntry)
            {
                DestroyIfTemporary(e.resultCache[0].result);
                e.resultCache.RemoveAt(0);
            }

            e.resultCache.Add(new ResultCacheItem { settings = settings, result = result, stats = stats });
        }

        void ClearEntries()
        {
            foreach (var e in entries)
            {
                e.job = null; // abandon in-flight work; its result is simply never collected
                foreach (var c in e.resultCache)
                    DestroyIfTemporary(c.result);
                e.resultCache.Clear();
            }

            entries.Clear();
            lods.Clear();
            selected = -1;
            dirtyStamp = -1;
        }
    }
}
