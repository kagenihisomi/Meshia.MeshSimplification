#nullable enable
#if ENABLE_MODULAR_AVATAR

using System.Collections.Generic;
using UnityEngine;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    /// <summary>
    /// Computes per-vertex occlusion weights using outward-hemisphere visibility sampling against
    /// the avatar's external clothing/body geometry.
    ///
    /// For each vertex, rays are cast only in directions within the outward-facing hemisphere
    /// (those where the ray direction aligns with the vertex normal).  Only external clothing
    /// and body meshes act as occluders — there is no self-collider test, which previously caused
    /// thin outer-surface meshes (e.g. stockings) to be falsely scored as occluded.
    ///
    /// This correctly answers: "From what fraction of external viewpoints is this vertex visible?"
    /// Vertices on the outside of clothing (stockings, sleeves) have outward normals; their
    /// outward rays escape freely → low score → preserved.  Body vertices buried under clothing
    /// have their outward rays immediately blocked by the clothing collider → high score →
    /// simplified aggressively.
    ///
    /// After scoring, a triangle-connectivity-based smoothing pass is applied to produce
    /// smooth weight gradients across the mesh surface.
    ///
    /// Occluded vertices receive a higher simplification weight so the mesh simplifier collapses
    /// them more aggressively than visible vertices.
    /// </summary>
    internal static class OcclusionVertexWeighter
    {
        // Ray origin offset to step slightly above the surface and avoid self-intersection.
        private const float RayOriginBias = 0.001f;

        // Default number of Fibonacci sphere candidate directions.
        // At runtime only the outward-hemisphere subset (~half) is actually tested.
        // 64 candidates gives adequate hemisphere coverage while keeping computation reasonable.
        private const int DefaultRayCount = 64;

        // Default maximum ray distance for occlusion tests (0.5 m – avatar scale).
        // Covers loose clothing, capes, skirts, and most accessories.
        private const float DefaultMaxDist = 0.5f;

        // Number of Laplacian smoothing iterations applied to raw scores.
        private const int SmoothIterations = 3;

        // Minimum squared magnitude for a vertex normal to be considered valid.
        // Below this threshold the normal is treated as zero-length and the hemisphere
        // gate is skipped (full sphere used instead).
        private const float MinNormalMagnitudeSq = 1e-6f;

        // Cached Fibonacci directions for the default ray count (immutable, safe for sharing).
        private static Vector3[]? s_cachedDirs;

        // ──────────────────────────────────────────────────────────────────────────────
        //  Public API
        // ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes per-vertex simplification weights using outward-hemisphere visibility sampling.
        /// </summary>
        /// <param name="worldSpaceMesh">
        ///   Mesh with vertices already in world space (baked or statically-transformed).
        /// </param>
        /// <param name="externalOccluderColliders">
        ///   World-space <see cref="MeshCollider"/> objects for all OTHER meshes on the avatar
        ///   (external clothing and body geometry).  The target renderer itself must be excluded.
        /// </param>
        /// <param name="externalOccluderCount">
        ///   Number of valid entries in <paramref name="externalOccluderColliders"/>.
        /// </param>
        /// <param name="occlusionWeightStrength">
        ///   How strongly occlusion raises the simplification weight [0 = off, 1 = maximum].
        /// </param>
        /// <returns>
        ///   Per-vertex float array (same indexing as <c>Mesh.vertices</c>).
        ///   1.0 = fully visible (preserve), up to 10.0 = fully occluded (simplify aggressively).
        /// </returns>
        public static float[] ComputeWeights(
            Mesh worldSpaceMesh,
            MeshCollider[] externalOccluderColliders,
            int externalOccluderCount,
            float occlusionWeightStrength)
        {
            return ComputeWeights(
                worldSpaceMesh,
                externalOccluderColliders,
                externalOccluderCount,
                DefaultRayCount,
                DefaultMaxDist,
                occlusionWeightStrength);
        }

        /// <summary>
        /// Full-control overload with explicit <paramref name="rayCount"/> and
        /// <paramref name="maxRayDistance"/>.
        /// </summary>
        public static float[] ComputeWeights(
            Mesh worldSpaceMesh,
            MeshCollider[] externalOccluderColliders,
            int externalOccluderCount,
            int rayCount,
            float maxRayDistance,
            float occlusionWeightStrength)
        {
            var vertices = worldSpaceMesh.vertices;
            var normals = worldSpaceMesh.normals;
            int vertexCount = vertices.Length;
            float[] rawScores = new float[vertexCount];

            float maxWeight = Mathf.Lerp(1f, 10f, occlusionWeightStrength);
            int clampedCount = Mathf.Clamp(externalOccluderCount, 0, externalOccluderColliders.Length);

            var directions = GetFibonacciDirections(rayCount);

            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 normal = i < normals.Length ? normals[i] : Vector3.zero;
                rawScores[i] = ComputeVertexOcclusionScore(
                    vertices[i], normal,
                    directions,
                    externalOccluderColliders, clampedCount,
                    maxRayDistance);
            }

            // Smooth the raw scores using triangle connectivity to produce gradual
            // gradients instead of noisy per-vertex scores.
            var triangles = worldSpaceMesh.triangles;
            float[] smoothed = SmoothScores(rawScores, triangles, vertexCount, SmoothIterations);

            // Convert smoothed scores to simplification weights.
            float[] weights = new float[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                weights[i] = Mathf.Lerp(1f, maxWeight, smoothed[i]);
            }

            return weights;
        }

        // ──────────────────────────────────────────────────────────────────────────────
        //  Core per-vertex outward-hemisphere visibility scoring
        // ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Returns [0, 1]: 0 = fully visible, 1 = fully occluded.</summary>
        private static float ComputeVertexOcclusionScore(
            Vector3 vertex,
            Vector3 normal,
            Vector3[] fibDirs,
            MeshCollider[] externalColliders,
            int externalCount,
            float maxDist)
        {
            if (externalCount == 0)
                return 0f;

            // Determine whether we have a valid surface normal.
            // If the normal is zero-length (degenerate vertex), fall back to the full sphere
            // so degenerate vertices are treated conservatively.
            bool hasNormal = normal.sqrMagnitude > MinNormalMagnitudeSq;
            Vector3 n = hasNormal ? normal.normalized : Vector3.zero;

            // Bias the ray origin slightly above the surface along the vertex normal
            // to prevent rays from immediately intersecting the vertex's own face.
            Vector3 origin = hasNormal
                ? vertex + n * RayOriginBias
                : vertex;

            int blocked = 0;
            int tested = 0;

            for (int d = 0; d < fibDirs.Length; d++)
            {
                // Only test directions in the outward hemisphere (facing away from the surface).
                // Directions pointing into the body are irrelevant for external visibility and
                // cause false-positive occlusion on thin outer-surface meshes (e.g. stockings):
                // those inward rays immediately hit the body mesh underneath.
                // When no valid normal exists, skip the hemisphere gate and test all directions.
                if (hasNormal && Vector3.Dot(fibDirs[d], n) <= 0f)
                    continue;

                tested++;
                var ray = new Ray(origin, fibDirs[d]);
                bool hit = false;

                // Test external (clothing/other-renderer) occluder colliders only.
                // No self-collider test: the self-collider caused false positives on thin
                // clothing surfaces by counting hits on the far side of the same mesh as occlusion.
                for (int c = 0; c < externalCount; c++)
                {
                    if (externalColliders[c] != null &&
                        externalColliders[c].Raycast(ray, out _, maxDist))
                    {
                        hit = true;
                        break;
                    }
                }

                if (hit) blocked++;
            }

            // Denominator is only the outward-hemisphere directions actually tested,
            // making the score independent of the normal orientation.
            return tested > 0 ? blocked / (float)tested : 0f;
        }

        // ──────────────────────────────────────────────────────────────────────────────
        //  Laplacian smoothing on triangle connectivity
        // ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Applies iterative Laplacian smoothing: each vertex's score is replaced by the
        /// average of its 1-ring neighbours.  This produces smooth gradients from fully-
        /// occluded to fully-visible regions instead of noisy per-vertex scores.
        /// </summary>
        private static float[] SmoothScores(float[] scores, int[] triangles, int vertexCount, int iterations)
        {
            if (triangles == null || triangles.Length < 3 || vertexCount == 0)
                return scores;

            // Build adjacency: for each vertex, collect the set of neighbour vertices.
            var neighbours = new HashSet<int>[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                neighbours[i] = new HashSet<int>();

            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if (a < vertexCount && b < vertexCount && c < vertexCount)
                {
                    AddNeighbour(neighbours, a, b);
                    AddNeighbour(neighbours, a, c);
                    AddNeighbour(neighbours, b, c);
                }
            }

            float[] src = (float[])scores.Clone();
            float[] dst = new float[vertexCount];

            for (int iter = 0; iter < iterations; iter++)
            {
                for (int v = 0; v < vertexCount; v++)
                {
                    var nbs = neighbours[v];
                    if (nbs.Count == 0)
                    {
                        dst[v] = src[v];
                        continue;
                    }

                    // Average of self + all neighbours (umbrella operator)
                    float sum = src[v];
                    foreach (int n in nbs)
                        sum += src[n];
                    dst[v] = sum / (nbs.Count + 1);
                }

                // Swap buffers
                (src, dst) = (dst, src);
            }

            return src;
        }

        private static void AddNeighbour(HashSet<int>[] neighbours, int a, int b)
        {
            neighbours[a].Add(b);
            neighbours[b].Add(a);
        }

        // ──────────────────────────────────────────────────────────────────────────────
        //  Fibonacci sphere direction generator
        // ──────────────────────────────────────────────────────────────────────────────

        private static Vector3[] GetFibonacciDirections(int count)
        {
            if (count == DefaultRayCount && s_cachedDirs != null)
                return s_cachedDirs;

            var dirs = GenerateFibonacciSphere(count);
            if (count == DefaultRayCount)
                s_cachedDirs = dirs;
            return dirs;
        }

        /// <summary>
        /// Generates <paramref name="n"/> evenly-distributed unit directions on the unit sphere
        /// using the golden-angle Fibonacci lattice.
        /// </summary>
        private static Vector3[] GenerateFibonacciSphere(int n)
        {
            var dirs = new Vector3[n];
            // Golden angle ≈ 2π × (2 − φ) ≈ 2.39996 rad
            const float GoldenAngle = 2.39996323f;
            for (int i = 0; i < n; i++)
            {
                float t = (i + 0.5f) / n;
                float inclination = Mathf.Acos(1f - 2f * t);   // 0 → π
                float azimuth = GoldenAngle * i;
                dirs[i] = new Vector3(
                    Mathf.Sin(inclination) * Mathf.Cos(azimuth),
                    Mathf.Sin(inclination) * Mathf.Sin(azimuth),
                    Mathf.Cos(inclination));
            }
            return dirs;
        }
    }
}

#endif
