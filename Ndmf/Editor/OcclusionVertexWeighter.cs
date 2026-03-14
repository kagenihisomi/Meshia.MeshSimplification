#nullable enable
#if ENABLE_MODULAR_AVATAR

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
    /// Occluded vertices receive a higher simplification weight so the mesh simplifier collapses
    /// them more aggressively than visible vertices.
    /// </summary>
    internal static class OcclusionVertexWeighter
    {
        // Ray origin offset to step slightly above the surface and avoid self-intersection.
        private const float RayOriginBias = 0.001f;

        // A self-collider hit only counts as occlusion when it is further away than this
        // distance, preventing the vertex's own adjacent faces from registering as blockers.
        private const float SelfMinHitDist = 0.002f;

        // Default number of Fibonacci sphere sample directions.
        private const int DefaultRayCount = 32;

        // Default maximum ray distance for occlusion tests (0.15 m – avatar scale).
        private const float DefaultMaxDist = 0.15f;

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
            float[] weights = new float[vertexCount];

            float maxWeight = Mathf.Lerp(1f, 10f, occlusionWeightStrength);
            int clampedCount = Mathf.Clamp(externalOccluderCount, 0, externalOccluderColliders.Length);

            var directions = GetFibonacciDirections(rayCount);

            using var selfOccluder = SelfMeshRayOccluder.Create(worldSpaceMesh);

            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 normal = i < normals.Length ? normals[i] : Vector3.zero;
                float score = ComputeVertexOcclusionScore(
                    vertices[i], normal,
                    directions,
                    externalOccluderColliders, clampedCount,
                    selfOccluder.Collider,
                    maxRayDistance);
                weights[i] = Mathf.Lerp(1f, maxWeight, score);
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
