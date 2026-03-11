#nullable enable

#if ENABLE_MODULAR_AVATAR

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    internal static class OcclusionBudgetAllocator
    {
        /// <summary>Weight of the directional visibility score in the combined result.</summary>
        private const float RayScoreWeight = 0.7f;

        /// <summary>Weight of the bounds-containment heuristic score in the combined result.</summary>
        private const float BoundsScoreWeight = 0.3f;

        /// <summary>
        /// Computes a visibility score [0,1] for each renderer owned by the simplifier.
        /// 1.0 = fully visible, 0.0 = fully occluded.
        /// Uses AABB-based directional sampling (no physics required, works in edit mode).
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

            // Initialize hit counters for all renderers
            var hitCounts = new Dictionary<Renderer, int>();
            foreach (var (renderer, _) in rendererPaths)
                hitCounts[renderer] = 0;

            // Compute combined avatar bounds
            var avatarBounds = rendererPaths[0].renderer.bounds;
            foreach (var (renderer, _) in rendererPaths.Skip(1))
                avatarBounds.Encapsulate(renderer.bounds);

            float farDistance = avatarBounds.size.magnitude * 2f + 1f;
            var center = avatarBounds.center;

            // Step 1: Multi-directional sampling using Bounds.IntersectRay
            // No physics required — works reliably in editor mode.
            //
            // For each sample direction, we fire a ray toward the avatar center and find
            // the "front-most" renderer: among all renderers whose AABB the ray intersects,
            // the one whose BOUNDS CENTER has the HIGHEST projection along the ray direction
            // is considered the outermost/visible renderer. Using center-depth rather than
            // AABB front-face distance avoids the problem where the body mesh's large AABB
            // (which extends to the nose/head) would always appear closest even when clothing
            // is visually in front of the torso.
            var directions = GenerateFibonacciSphere(raySampleCount);

            foreach (var dir in directions)
            {
                var origin = center + dir * farDistance;
                var ray = new Ray(origin, -dir);

                float maxCenterDepth = float.MinValue;
                Renderer? frontRenderer = null;

                foreach (var (renderer, _) in rendererPaths)
                {
                    // Only consider renderers whose AABB the ray actually intersects
                    if (!renderer.bounds.IntersectRay(ray, out float dist) || dist < 0f)
                        continue;

                    // Among intersected renderers, pick the one whose CENTER is
                    // furthest along the ray direction (= most "in front" of the avatar
                    // from the viewer's perspective in direction `dir`).
                    float centerDepth = Vector3.Dot(renderer.bounds.center, dir);
                    if (centerDepth > maxCenterDepth)
                    {
                        maxCenterDepth = centerDepth;
                        frontRenderer = renderer;
                    }
                }

                if (frontRenderer != null)
                    hitCounts[frontRenderer]++;
            }

            // Step 2: Directional-projection bounds heuristic (complement)
            // For each renderer A, compute what fraction of its 2D projected surface
            // (in each principal direction) is covered by other renderers that are
            // "in front of" A in that direction. No size constraint — even smaller
            // clothing meshes can occlude a larger body mesh.
            var boundsScores = new Dictionary<Renderer, float>();
            var principalDirs = new[]
            {
                Vector3.right, Vector3.left,
                Vector3.up,    Vector3.down,
                Vector3.forward, Vector3.back,
            };

            foreach (var (rendererA, _) in rendererPaths)
            {
                float totalOccludedFraction = 0f;
                int validDirs = 0;

                foreach (var d in principalDirs)
                {
                    float areaA = GetProjectedArea(rendererA.bounds, d);
                    if (areaA <= 0f) continue;

                    float aDepth = Vector3.Dot(rendererA.bounds.center, d);
                    float occludedArea = 0f;

                    foreach (var (rendererB, _) in rendererPaths)
                    {
                        if (rendererB == rendererA) continue;

                        // Only consider renderers whose center is IN FRONT of A along d
                        float bDepth = Vector3.Dot(rendererB.bounds.center, d);
                        if (bDepth <= aDepth) continue;

                        occludedArea += GetProjectedOverlapArea(rendererA.bounds, rendererB.bounds, d);
                    }

                    totalOccludedFraction += Mathf.Clamp01(occludedArea / areaA);
                    validDirs++;
                }

                float avgOccluded = validDirs > 0 ? totalOccludedFraction / validDirs : 0f;
                boundsScores[rendererA] = Mathf.Clamp01(1f - avgOccluded);
            }

            // Step 3: Normalize ray scores and combine with bounds scores
            int maxHits = hitCounts.Values.DefaultIfEmpty(0).Max();
            float aggressiveness = simplifier.OcclusionAggressiveness;

            foreach (var (renderer, path) in rendererPaths)
            {
                float rayScore = maxHits > 0 ? Mathf.Clamp01((float)hitCounts[renderer] / maxHits) : 1f;
                float boundsScore = boundsScores.TryGetValue(renderer, out var bs) ? bs : 1f;

                float combinedScore = RayScoreWeight * rayScore + BoundsScoreWeight * boundsScore;
                float effectiveScore = Mathf.Lerp(1f, combinedScore, aggressiveness);
                result[path] = Mathf.Clamp01(effectiveScore);
            }

            return result;
        }

        /// <summary>
        /// Returns the area of the bounds face perpendicular to <paramref name="dir"/>
        /// (one of the 6 axis-aligned unit vectors).
        /// </summary>
        private static float GetProjectedArea(Bounds b, Vector3 dir)
        {
            var absDir = new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z));
            // The two axes perpendicular to dir
            float w, h;
            if (absDir.x > 0.5f)      { w = b.size.y; h = b.size.z; }
            else if (absDir.y > 0.5f) { w = b.size.x; h = b.size.z; }
            else                       { w = b.size.x; h = b.size.y; }
            return w * h;
        }

        /// <summary>
        /// Returns the 2D projected overlap area of bounds <paramref name="a"/> and
        /// <paramref name="b"/> projected onto the plane perpendicular to <paramref name="dir"/>.
        /// </summary>
        private static float GetProjectedOverlapArea(Bounds a, Bounds b, Vector3 dir)
        {
            var absDir = new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z));

            float ow, oh;
            if (absDir.x > 0.5f)
            {
                ow = Mathf.Max(0f, Mathf.Min(a.max.y, b.max.y) - Mathf.Max(a.min.y, b.min.y));
                oh = Mathf.Max(0f, Mathf.Min(a.max.z, b.max.z) - Mathf.Max(a.min.z, b.min.z));
            }
            else if (absDir.y > 0.5f)
            {
                ow = Mathf.Max(0f, Mathf.Min(a.max.x, b.max.x) - Mathf.Max(a.min.x, b.min.x));
                oh = Mathf.Max(0f, Mathf.Min(a.max.z, b.max.z) - Mathf.Max(a.min.z, b.min.z));
            }
            else
            {
                ow = Mathf.Max(0f, Mathf.Min(a.max.x, b.max.x) - Mathf.Max(a.min.x, b.min.x));
                oh = Mathf.Max(0f, Mathf.Min(a.max.y, b.max.y) - Mathf.Max(a.min.y, b.min.y));
            }

            return ow * oh;
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
    }
}

#endif

