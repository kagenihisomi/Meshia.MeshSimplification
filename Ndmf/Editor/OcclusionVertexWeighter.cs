#nullable enable
#if ENABLE_MODULAR_AVATAR

using System.Collections.Generic;
using UnityEngine;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    /// <summary>
    /// Computes per-vertex occlusion weights using Fibonacci sphere raycasting against the
    /// avatar's combined world-space geometry.
    ///
    /// For each vertex, rays are cast outward in evenly-distributed Fibonacci-lattice directions.
    /// Directions that are blocked by occluder mesh colliders (geometry from other renderers on
    /// the same avatar) count toward the occlusion score.  A self-collider with a minimum distance
    /// guard also contributes, enabling per-mesh self-occlusion (e.g. body geometry hidden inside
    /// clothing on a single SkinnedMeshRenderer).
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

        // A self-collider hit only counts as occlusion when it is further away than this
        // distance, preventing the vertex's own adjacent faces from registering as blockers.
        // 1cm avoids noise from neighbouring triangles on the same mesh.
        private const float SelfMinHitDist = 0.01f;

        // Default number of Fibonacci sphere sample directions.
        // 64 directions gives ~1.6% resolution per step which is smooth enough for
        // visible heatmaps while keeping computation time reasonable.
        private const int DefaultRayCount = 64;

        // Default maximum ray distance for occlusion tests (0.5 m – avatar scale).
        // Covers loose clothing, capes, skirts, and most accessories.
        private const float DefaultMaxDist = 0.5f;

        // Number of Laplacian smoothing iterations applied to raw scores.
        private const int SmoothIterations = 3;

        // Cached Fibonacci directions for the default ray count (immutable, safe for sharing).
        private static Vector3[]? s_cachedDirs;

        // ──────────────────────────────────────────────────────────────────────────────
        //  Public API
        // ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes per-vertex simplification weights using Fibonacci sphere raycasting.
        /// </summary>
        /// <param name="worldSpaceMesh">
        ///   Mesh with vertices already in world space (baked or statically-transformed).
        ///   Also used internally to build a self-occlusion collider for this mesh.
        /// </param>
        /// <param name="externalOccluderColliders">
        ///   World-space <see cref="MeshCollider"/> objects for all OTHER meshes on the avatar
        ///   (the target renderer itself must be excluded so the self-collider path handles it).
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

            using var selfOccluder = SelfMeshRayOccluder.Create(worldSpaceMesh);

            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 normal = i < normals.Length ? normals[i] : Vector3.zero;
                rawScores[i] = ComputeVertexOcclusionScore(
                    vertices[i], normal,
                    directions,
                    externalOccluderColliders, clampedCount,
                    selfOccluder.Collider,
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
        //  Temporary self-collider helper
        // ──────────────────────────────────────────────────────────────────────────────

        private sealed class SelfMeshRayOccluder : System.IDisposable
        {
            private readonly GameObject _go;
            public MeshCollider Collider { get; }

            private SelfMeshRayOccluder(GameObject go, MeshCollider col) { _go = go; Collider = col; }

            public static SelfMeshRayOccluder Create(Mesh worldSpaceMesh)
            {
                var go = new GameObject("MeshiaOcclusionSelfRay") { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                go.transform.localScale = Vector3.one;
                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = worldSpaceMesh;
                return new SelfMeshRayOccluder(go, col);
            }

            public void Dispose()
            {
                if (_go != null) Object.DestroyImmediate(_go);
            }
        }

        // ──────────────────────────────────────────────────────────────────────────────
        //  Core per-vertex Fibonacci sphere scoring
        // ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Returns [0, 1]: 0 = fully visible, 1 = fully occluded.</summary>
        private static float ComputeVertexOcclusionScore(
            Vector3 vertex,
            Vector3 normal,
            Vector3[] fibDirs,
            MeshCollider[] externalColliders,
            int externalCount,
            MeshCollider selfCollider,
            float maxDist)
        {
            if (externalCount == 0)
                return 0f;

            // Bias the ray origin slightly above the surface along the vertex normal
            // to prevent rays from immediately intersecting the vertex's own face.
            Vector3 origin = normal.sqrMagnitude > 1e-6f
                ? vertex + normal.normalized * RayOriginBias
                : vertex;

            int blocked = 0;
            int total = fibDirs.Length;

            for (int d = 0; d < total; d++)
            {
                var ray = new Ray(origin, fibDirs[d]);
                bool hit = false;

                // 1. Test all external (other-renderer) occluder colliders.
                //    Any hit within maxDist counts – no minimum distance required here
                //    because these are entirely separate meshes.
                for (int c = 0; c < externalCount; c++)
                {
                    if (externalColliders[c] != null &&
                        externalColliders[c].Raycast(ray, out _, maxDist))
                    {
                        hit = true;
                        break;
                    }
                }

                // 2. Test against the self-collider (same mesh as the vertex).
                //    Only count as an occluder when the hit is at least SelfMinHitDist away,
                //    which filters out the vertex's own adjacent triangles.
                if (!hit && selfCollider != null)
                {
                    if (selfCollider.Raycast(ray, out var selfHit, maxDist) &&
                        selfHit.distance >= SelfMinHitDist)
                    {
                        hit = true;
                    }
                }

                if (hit) blocked++;
            }

            return blocked / (float)total;
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
