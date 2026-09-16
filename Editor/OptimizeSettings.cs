using System;
using UnityEngine;
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
}
