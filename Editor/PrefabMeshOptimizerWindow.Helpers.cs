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

        static Dictionary<Renderer, int> BuildLodLevelMap(GameObject root)
        {
            var map = new Dictionary<Renderer, int>();
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
            {
                var levels = group.GetLODs();
                for (int i = 0; i < levels.Length; i++)
                {
                    if (levels[i].renderers == null)
                        continue;

                    foreach (var r in levels[i].renderers)
                    {
                        if (r != null && !map.ContainsKey(r))
                            map.Add(r, i);
                    }
                }
            }
            return map;
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

        void ClearEntries()
        {
            foreach (var e in entries)
            {
                e.job = null; // abandon in-flight work; its result is simply never collected
                DestroyIfTemporary(e.result);
            }

            entries.Clear();
            lods.Clear();
            selected = -1;
            dirtyStamp = -1;
        }
    }
}
