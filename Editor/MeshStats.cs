using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeshOptimizer
{
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
}
