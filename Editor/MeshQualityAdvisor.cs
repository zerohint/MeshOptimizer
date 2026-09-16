using System.Collections.Generic;
using UnityEngine;

namespace MeshOptimizer
{
    /// <summary>
    /// Backs the "Smart" button: picks the quality slider value that gives the most
    /// compression while keeping visual distortion under a small, fixed budget.
    /// Everything else in the mesh's settings is left untouched - only quality is tuned.
    /// </summary>
    public static class MeshQualityAdvisor
    {
        // Distortion is the mean distance from sampled source-mesh points to the closest
        // triangle of the candidate, as a fraction of the mesh's bounding-sphere radius.
        // 0.35% is small enough to be imperceptible at normal viewing distance on most props.
        const float TargetDistortion = 0.0035f;
        const int MaxSourceSamples = 600;
        const int MaxCandidateTriangles = 4000;
        const int MaxIterations = 7;
        const float MinQuality = 0.02f;
        const float MaxQuality = 1f;

        /// <summary>
        /// Binary-searches for the lowest quality (most compression) whose simplified result
        /// stays within the distortion budget. Blocking - only call from a user-triggered action.
        /// </summary>
        public static float FindBestQuality(Mesh source, OptimizeSettings baseSettings)
        {
            if (source == null || source.vertexCount == 0)
                return baseSettings.quality;

            var radius = Mathf.Max(0.0001f, source.bounds.extents.magnitude);
            var samples = SamplePoints(source, MaxSourceSamples);

            // If even the most aggressive simplification is already within budget, that is best.
            if (Distortion(source, baseSettings, MinQuality, samples, radius) <= TargetDistortion)
                return MinQuality;

            float lo = MinQuality, hi = MaxQuality, best = MaxQuality;
            for (int i = 0; i < MaxIterations; i++)
            {
                var mid = (lo + hi) * 0.5f;
                var d = Distortion(source, baseSettings, mid, samples, radius);

                if (d <= TargetDistortion)
                {
                    best = mid;
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }

            return best;
        }

        static float Distortion(Mesh source, OptimizeSettings baseSettings, float quality, Vector3[] samples, float radius)
        {
            var settings = baseSettings.Clone();
            settings.quality = quality;

            var generated = MeshOptimizeCore.Simplify(source, settings, out var error);
            if (generated == null || error != null)
            {
                Object.DestroyImmediate(generated);
                return float.MaxValue; // treat failures as "too distorted" - search backs off toward higher quality
            }

            var result = MeanNearestTriangleDistance(samples, generated) / radius;
            Object.DestroyImmediate(generated);
            return result;
        }

        /// <summary>Evenly-strided subset of the source mesh's vertices, capped for performance.</summary>
        static Vector3[] SamplePoints(Mesh source, int maxSamples)
        {
            var verts = source.vertices;
            if (verts.Length <= maxSamples)
                return verts;

            var step = verts.Length / (float)maxSamples;
            var result = new Vector3[maxSamples];
            for (int i = 0; i < maxSamples; i++)
                result[i] = verts[Mathf.Min(verts.Length - 1, Mathf.FloorToInt(i * step))];
            return result;
        }

        /// <summary>Mean distance from each sample to the closest triangle of <paramref name="mesh"/>.</summary>
        static float MeanNearestTriangleDistance(Vector3[] samples, Mesh mesh)
        {
            var verts = mesh.vertices;
            var tris = SampleTriangles(AllTriangles(mesh), MaxCandidateTriangles);
            if (tris.Length == 0)
                return float.MaxValue;

            double sum = 0;
            foreach (var p in samples)
            {
                var best = float.MaxValue;
                for (int t = 0; t < tris.Length; t += 3)
                {
                    var d = PointTriangleDistanceSqr(p, verts[tris[t]], verts[tris[t + 1]], verts[tris[t + 2]]);
                    if (d < best)
                        best = d;
                }
                sum += Mathf.Sqrt(best);
            }
            return (float)(sum / samples.Length);
        }

        /// <summary>Concatenated triangle indices across every submesh (Mesh.triangles only covers submesh 0).</summary>
        static int[] AllTriangles(Mesh mesh)
        {
            if (mesh.subMeshCount <= 1)
                return mesh.triangles;

            var all = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
                all.AddRange(mesh.GetTriangles(s));
            return all.ToArray();
        }

        /// <summary>Evenly-strided subset of triangles (whole triangles, i.e. multiples of 3).</summary>
        static int[] SampleTriangles(int[] tris, int maxTriangles)
        {
            var triCount = tris.Length / 3;
            if (triCount <= maxTriangles)
                return tris;

            var step = triCount / (float)maxTriangles;
            var result = new int[maxTriangles * 3];
            for (int i = 0; i < maxTriangles; i++)
            {
                var src = Mathf.Min(triCount - 1, Mathf.FloorToInt(i * step)) * 3;
                result[i * 3] = tris[src];
                result[i * 3 + 1] = tris[src + 1];
                result[i * 3 + 2] = tris[src + 2];
            }
            return result;
        }

        /// <summary>Squared distance from point p to triangle (a, b, c). Ericson, RTCD 5.1.5.</summary>
        static float PointTriangleDistanceSqr(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;

            var d1 = Vector3.Dot(ab, ap);
            var d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
                return (p - a).sqrMagnitude;

            var bp = p - b;
            var d3 = Vector3.Dot(ab, bp);
            var d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
                return (p - b).sqrMagnitude;

            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                var v = d1 / (d1 - d3);
                return (p - (a + v * ab)).sqrMagnitude;
            }

            var cp = p - c;
            var d5 = Vector3.Dot(ab, cp);
            var d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
                return (p - c).sqrMagnitude;

            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                var w = d2 / (d2 - d6);
                return (p - (a + w * ac)).sqrMagnitude;
            }

            var va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                var w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return (p - (b + w * (c - b))).sqrMagnitude;
            }

            var denom = 1f / (va + vb + vc);
            var v2 = vb * denom;
            var w2 = vc * denom;
            return (p - (a + ab * v2 + ac * w2)).sqrMagnitude;
        }
    }
}
