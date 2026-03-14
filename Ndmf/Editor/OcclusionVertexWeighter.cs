#nullable enable
#if ENABLE_MODULAR_AVATAR

using UnityEngine;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    /// <summary>
    /// Computes per-vertex occlusion weights for a baked world-space mesh.
    /// Occluded vertices receive a higher simplification weight so that the mesh simplifier
    /// collapses them more aggressively than visible vertices.
    /// </summary>
    internal static class OcclusionVertexWeighter
    {
        /// <summary>
        /// Computes per-vertex simplification weights using a 6-direction AABB center-depth + footprint test.
        /// </summary>
        /// <param name="bakedWorldSpaceMesh">
        /// The mesh whose vertices are to be scored, with vertex positions already in world space.
        /// </param>
        /// <param name="occluderBounds">
        /// World-space AABBs of all other active renderers on the avatar that act as potential occluders.
        /// </param>
        /// <param name="occlusionWeightStrength">
        /// Controls how strongly occlusion affects the weight. Range [0, 1].
        /// </param>
        /// <returns>
        /// A float[] with one entry per vertex (same indexing as <c>Mesh.vertices</c>).
        /// Value 1.0 = fully visible (preserve), up to 10.0 = fully occluded (simplify aggressively).
        /// </returns>
        public static float[] ComputeWeights(Mesh bakedWorldSpaceMesh, Bounds[] occluderBounds, float occlusionWeightStrength)
        {
            return ComputeWeights(bakedWorldSpaceMesh, occluderBounds, occluderBounds.Length, occlusionWeightStrength);
        }

        /// <summary>
        /// Computes per-vertex simplification weights using only the first <paramref name="occluderCount"/>
        /// entries in <paramref name="occluderBounds"/>. This avoids temporary array allocations.
        /// </summary>
        public static float[] ComputeWeights(Mesh bakedWorldSpaceMesh, Bounds[] occluderBounds, int occluderCount, float occlusionWeightStrength)
        {
            var vertices = bakedWorldSpaceMesh.vertices;
            int vertexCount = vertices.Length;
            float[] weights = new float[vertexCount];

            float maxWeight = Mathf.Lerp(1f, 10f, occlusionWeightStrength);
            int clampedOccluderCount = Mathf.Clamp(occluderCount, 0, occluderBounds.Length);

            for (int i = 0; i < vertexCount; i++)
            {
                float occlusionScore = ComputeVertexOcclusionScore(vertices[i], occluderBounds, clampedOccluderCount);
                // simplificationWeight: 1.0 = fully visible (unchanged), maxWeight = fully occluded (aggressive)
                weights[i] = Mathf.Lerp(1f, maxWeight, occlusionScore);
            }

            return weights;
        }

        /// <summary>
        /// Returns a score in [0, 1] indicating how occluded a vertex is.
        /// 0 = fully visible (no directions blocked), 1 = fully occluded (all 6 directions blocked).
        /// </summary>
        private static float ComputeVertexOcclusionScore(Vector3 vertex, Bounds[] occluderBounds, int occluderCount)
        {
            if (occluderCount == 0) return 0f;

            // If a vertex is physically inside any occluder volume, treat it as fully occluded.
            for (int i = 0; i < occluderCount; i++)
            {
                if (occluderBounds[i].Contains(vertex))
                    return 1f;
            }

            int blockedDirections = 0;
            // Test 6 cardinal directions: +X, -X, +Y, -Y, +Z, -Z
            for (int axis = 0; axis < 3; axis++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    if (IsDirectionBlocked(vertex, axis, sign, occluderBounds, occluderCount))
                        blockedDirections++;
                }
            }

            return blockedDirections / 6f;
        }

        /// <summary>
        /// Tests whether any occluder bounds blocks the given vertex from a cardinal direction.
        /// A direction is "blocked" if:
        /// (a) the occluder's center is in front of the vertex along that axis, and
        /// (b) the vertex's perpendicular 2D position falls inside the occluder's AABB footprint.
        /// </summary>
        private static bool IsDirectionBlocked(Vector3 vertex, int axis, int sign, Bounds[] occluderBounds, int occluderCount)
        {
            float vertexDepth = GetComponent(vertex, axis);
            int axis1 = (axis + 1) % 3;
            int axis2 = (axis + 2) % 3;
            float v1 = GetComponent(vertex, axis1);
            float v2 = GetComponent(vertex, axis2);

            for (int i = 0; i < occluderCount; i++)
            {
                var bounds = occluderBounds[i];
                // (a) Center-depth test: occluder center must be "in front of" vertex along this axis/sign
                float occluderCenterDepth = GetComponent(bounds.center, axis);
                if (sign > 0 && occluderCenterDepth <= vertexDepth) continue;
                if (sign < 0 && occluderCenterDepth >= vertexDepth) continue;

                // (b) Footprint test: vertex falls inside the occluder's AABB in the two perpendicular axes
                float min1 = GetComponent(bounds.min, axis1);
                float max1 = GetComponent(bounds.max, axis1);
                float min2 = GetComponent(bounds.min, axis2);
                float max2 = GetComponent(bounds.max, axis2);

                if (v1 >= min1 && v1 <= max1 && v2 >= min2 && v2 <= max2)
                    return true;
            }
            return false;
        }

        private static float GetComponent(Vector3 v, int axis) => axis switch
        {
            0 => v.x,
            1 => v.y,
            _ => v.z
        };
    }
}

#endif
