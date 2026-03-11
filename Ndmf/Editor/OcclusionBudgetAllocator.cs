#nullable enable

#if ENABLE_MODULAR_AVATAR

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    internal static class OcclusionBudgetAllocator
    {
        /// <summary>
        /// The Unity layer used for temporary MeshColliders during occlusion computation.
        /// Layer 31 is used as it is typically unused by default. If your project uses layer 31,
        /// this value can be changed here to an unused layer.
        /// </summary>
        private const int TempColliderLayer = 31;

        /// <summary>Weight of the ray-cast visibility score in the combined result.</summary>
        private const float RayScoreWeight = 0.7f;

        /// <summary>Weight of the bounds-containment heuristic score in the combined result.</summary>
        private const float BoundsScoreWeight = 0.3f;
        /// <summary>
        /// Computes a visibility score [0,1] for each renderer owned by the simplifier.
        /// 1.0 = fully visible, 0.0 = fully occluded.
        /// </summary>
        /// <param name="simplifier">The cascading simplifier component.</param>
        /// <param name="raySampleCount">Number of ray-cast directions to sample. Default 256.</param>
        /// <returns>Dictionary keyed by renderer's AvatarObjectReference.referencePath → visibility score.</returns>
        public static Dictionary<string, float> ComputeVisibilityScores(
            MeshiaCascadingAvatarMeshSimplifier simplifier,
            int raySampleCount = 256)
        {
            var result = new Dictionary<string, float>();
            var entries = simplifier.Entries;

            if (entries.Count == 0) return result;

            // Gather renderers and their reference paths
            var rendererPaths = new List<(Renderer renderer, string path)>();
            foreach (var entry in entries)
            {
                var renderer = entry.GetTargetRenderer(simplifier);
                if (renderer == null) continue;
                rendererPaths.Add((renderer, entry.RendererObjectReference.referencePath));
            }

            if (rendererPaths.Count == 0) return result;

            // Step 1: Multi-view ray sampling using temporary GameObjects with MeshColliders
            var tempObjects = new List<GameObject>();
            var colliderToRenderer = new Dictionary<MeshCollider, Renderer>();
            var hitCounts = new Dictionary<Renderer, int>();
            int layerMask = 1 << TempColliderLayer;

            try
            {
                // Create temporary GameObjects with MeshColliders for raycasting
                foreach (var (renderer, _) in rendererPaths)
                {
                    var mesh = RendererUtility.GetMesh(renderer);
                    if (mesh == null) continue;

                    var tempGo = new GameObject("__MeshiaOcclusionTemp__")
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                        layer = TempColliderLayer,
                    };

                    // Copy world-space transform for root-level temp object (lossyScale == localScale for root)
                    tempGo.transform.position = renderer.transform.position;
                    tempGo.transform.rotation = renderer.transform.rotation;
                    tempGo.transform.localScale = renderer.transform.lossyScale;

                    var col = tempGo.AddComponent<MeshCollider>();
                    col.sharedMesh = mesh;

                    tempObjects.Add(tempGo);
                    colliderToRenderer[col] = renderer;
                    hitCounts[renderer] = 0;
                }

                // Ensure physics transforms are up to date
                Physics.SyncTransforms();

                // Compute avatar bounds
                var allBounds = rendererPaths.Select(rp => rp.renderer.bounds).ToArray();
                var avatarBounds = allBounds[0];
                foreach (var b in allBounds.Skip(1)) avatarBounds.Encapsulate(b);

                float farDistance = avatarBounds.size.magnitude * 2f + 1f;
                var center = avatarBounds.center;

                // Generate sample directions using Fibonacci sphere for even distribution
                var directions = GenerateFibonacciSphere(raySampleCount);

                foreach (var dir in directions)
                {
                    var origin = center + dir * farDistance;
                    var ray = new Ray(origin, -dir);

                    if (Physics.Raycast(ray, out var hit, farDistance * 2.2f, layerMask))
                    {
                        if (hit.collider is MeshCollider hitCol && colliderToRenderer.TryGetValue(hitCol, out var hitRenderer))
                        {
                            hitCounts[hitRenderer]++;
                        }
                    }
                }

                // Step 2: Bounds-containment heuristic for renderers with low/zero ray hits
                var boundsScores = new Dictionary<Renderer, float>();
                foreach (var (rendererA, _) in rendererPaths)
                {
                    var boundsA = rendererA.bounds;
                    float totalOverlapFraction = 0f;

                    foreach (var (rendererB, _) in rendererPaths)
                    {
                        if (rendererB == rendererA) continue;
                        var boundsB = rendererB.bounds;

                        // Only consider larger renderers as potential occluders
                        if (boundsB.size.sqrMagnitude <= boundsA.size.sqrMagnitude) continue;

                        float overlapFraction = ComputeAABBOverlapFraction(boundsA, boundsB);
                        if (overlapFraction > 0f)
                            totalOverlapFraction += overlapFraction;
                    }

                    // High overlap from larger outer meshes → lower visibility
                    boundsScores[rendererA] = Mathf.Clamp01(1f - Mathf.Clamp01(totalOverlapFraction));
                }

                // Step 3: Normalize ray scores and combine with bounds scores
                int maxHits = hitCounts.Values.DefaultIfEmpty(0).Max();
                float aggressiveness = simplifier.OcclusionAggressiveness;

                foreach (var (renderer, path) in rendererPaths)
                {
                    float rayScore;
                    if (maxHits > 0 && hitCounts.TryGetValue(renderer, out var hits))
                        rayScore = Mathf.Clamp01((float)hits / maxHits);
                    else
                        rayScore = 1f;

                    float boundsScore = boundsScores.TryGetValue(renderer, out var bs) ? bs : 1f;

                    float combinedScore = RayScoreWeight * rayScore + BoundsScoreWeight * boundsScore;
                    float effectiveScore = Mathf.Lerp(1f, combinedScore, aggressiveness);
                    result[path] = Mathf.Clamp01(effectiveScore);
                }
            }
            finally
            {
                // Always clean up temporary GameObjects
                foreach (var go in tempObjects)
                {
                    if (go != null)
                        UnityEngine.Object.DestroyImmediate(go);
                }
            }

            return result;
        }

        /// <summary>Generates evenly-distributed directions on a sphere using the Fibonacci / golden-angle method.</summary>
        private static Vector3[] GenerateFibonacciSphere(int count)
        {
            if (count <= 0) return System.Array.Empty<Vector3>();
            if (count == 1) return new[] { Vector3.up };

            var points = new Vector3[count];
            // Golden angle in radians
            float angleIncrement = Mathf.PI * (3f - Mathf.Sqrt(5f));

            for (int i = 0; i < count; i++)
            {
                float y = 1f - (i / (float)(count - 1)) * 2f; // range [-1, 1]
                float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float theta = angleIncrement * i;

                points[i] = new Vector3(
                    Mathf.Cos(theta) * radius,
                    y,
                    Mathf.Sin(theta) * radius
                );
            }

            return points;
        }

        /// <summary>
        /// Computes the fraction of bounds A's volume that overlaps with bounds B.
        /// Returns a value in [0, 1].
        /// </summary>
        private static float ComputeAABBOverlapFraction(Bounds a, Bounds b)
        {
            float overlapX = Mathf.Max(0f, Mathf.Min(a.max.x, b.max.x) - Mathf.Max(a.min.x, b.min.x));
            float overlapY = Mathf.Max(0f, Mathf.Min(a.max.y, b.max.y) - Mathf.Max(a.min.y, b.min.y));
            float overlapZ = Mathf.Max(0f, Mathf.Min(a.max.z, b.max.z) - Mathf.Max(a.min.z, b.min.z));

            float overlapVolume = overlapX * overlapY * overlapZ;
            float aVolume = a.size.x * a.size.y * a.size.z;

            if (aVolume <= 0f) return 0f;
            return overlapVolume / aVolume;
        }
    }
}

#endif
