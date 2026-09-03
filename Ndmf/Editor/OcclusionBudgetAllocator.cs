#nullable enable

#if ENABLE_MODULAR_AVATAR

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    /// <summary>
    /// Holds pre-baked GL draw arrays (world-space vertex positions + RGBA colours) produced by
    /// <see cref="OcclusionBudgetAllocator.ComputeVisibilityScores"/> for the Scene View heat-map.
    /// Arrays are built once at compute time; the gizmo drawer reads them each repaint without any
    /// per-frame mesh traversal.
    /// </summary>
    internal sealed class OcclusionDebugData : IDisposable
    {
        /// <summary>
        /// Flat interleaved GL triangle arrays per renderer.
        /// <c>positions[i]</c> is in world space; <c>colors[i]</c> is the heat-map RGBA.
        /// Triangle count is capped at <see cref="OcclusionBudgetAllocator.MaxDrawTrisPerMesh"/>
        /// so the scene view stays responsive.
        /// </summary>
        public readonly Dictionary<Renderer, (Vector3[] positions, Color[] colors)> DrawArrays = new();

        public void Dispose() => DrawArrays.Clear();
    }

    internal static class OcclusionBudgetAllocator
    {
        /// <summary>
        /// Maximum vertices stride-sampled per renderer for occlusion scoring.
        /// Higher values produce smoother per-vertex heat-maps but increase compute time linearly.
        /// The new 6-direction AABB test is O(numRenderers) per vertex (pure float comparisons),
        /// so 2000 samples runs quickly in practice; reduce if compute is too slow on your avatar.
        /// </summary>
        private const int MaxSampleVertsPerMesh = 2000;

        /// <summary>
        /// Maximum triangles drawn per renderer in the Scene View heat-map gizmo.
        /// Capping the drawn triangle count is the primary guard against scene-view stutter.
        /// </summary>
        public const int MaxDrawTrisPerMesh = 3000;

        /// <summary>
        /// Debug data produced by the last <see cref="ComputeVisibilityScores"/> call.
        /// Null until first compute or after a domain reload.
        /// </summary>
        public static OcclusionDebugData? LastDebugData;

        /// <summary>
        /// Computes a visibility score [0,1] for each renderer owned by <paramref name="simplifier"/>.
        /// Score 1.0 = most visible; 0.0 = most occluded.
        /// <para>
        /// Algorithm — per-vertex 6-direction AABB occlusion:
        /// Each renderer's skinned mesh is baked into world space. Up to
        /// <see cref="MaxSampleVertsPerMesh"/> vertices are stride-sampled.  For each sampled vertex V
        /// and each of the 6 cardinal viewing directions, V is "occluded" from that direction if some
        /// OTHER active renderer B satisfies two conditions simultaneously:
        /// (1) B's AABB CENTER is in front of V along the direction (center-depth comparison), and
        /// (2) V's position projected perpendicular to that direction falls inside B's 2D AABB
        ///     footprint (two axis-aligned range checks).
        /// Using center-depth (rather than front-face depth) prevents a large body-mesh AABB from
        /// incorrectly blocking outer clothing surfaces whose vertices happen to lie inside the body
        /// AABB but are physically in front of the body center.
        /// </para>
        /// <para>
        /// Disabled GameObjects and disabled Renderer components are excluded from the occluder set
        /// so that inactive clothing layers do not affect the score.
        /// </para>
        /// <para>
        /// Also populates <see cref="LastDebugData"/> with pre-baked GL draw arrays for the gizmo.
        /// </para>
        /// </summary>
        public static Dictionary<string, float> ComputeVisibilityScores(
            MeshiaCascadingAvatarMeshSimplifier simplifier)
        {
            // Release memory from the previous run.
            LastDebugData?.Dispose();
            var debugData = new OcclusionDebugData();

            var result = new Dictionary<string, float>();
            var entries = simplifier.Entries;

            if (entries.Count == 0)
            {
                LastDebugData = debugData;
                return result;
            }

            // Gather renderers + reference paths.  We keep disabled renderers in this list so they
            // still appear in the score output, but they are EXCLUDED from the occluder set below.
            var rendererPaths = new List<(Renderer renderer, string path)>();
            foreach (var entry in entries)
            {
                var renderer = entry.GetTargetRenderer(simplifier);
                if (renderer == null) continue;
                rendererPaths.Add((renderer, entry.RendererObjectReference.referencePath));
            }

            if (rendererPaths.Count == 0)
            {
                LastDebugData = debugData;
                return result;
            }

            var allRenderers = rendererPaths.Select(rp => rp.renderer).ToArray();
            int numRenderers = allRenderers.Length;

            // Per-renderer raw mean visibility (before normalisation).
            var rawScores = new Dictionary<string, float>();

            for (int aIdx = 0; aIdx < numRenderers; aIdx++)
            {
                var (rendererA, path) = rendererPaths[aIdx];

                // Build the occluder bounds array: every OTHER renderer that is currently active.
                // Disabled clothing must NOT occlude the body (or any other mesh).
                var occluderBounds = new List<Bounds>(numRenderers - 1);
                for (int bi = 0; bi < numRenderers; bi++)
                {
                    if (bi == aIdx) continue;
                    var rB = allRenderers[bi];
                    if (!rB.gameObject.activeInHierarchy || !rB.enabled) continue;
                    occluderBounds.Add(rB.bounds);
                }
                var boundsArray = occluderBounds.ToArray();

                // Bake to a world-space mesh so we can test actual surface vertex positions.
                var bakedMesh = BakeRendererWorldSpace(rendererA);
                if (bakedMesh == null || bakedMesh.vertexCount == 0)
                {
                    if (bakedMesh != null) UnityEngine.Object.DestroyImmediate(bakedMesh);
                    rawScores[path] = 1f;
                    continue;
                }

                var worldVerts = bakedMesh.vertices; // already in world space
                int numVerts = worldVerts.Length;

                // Stride-sample vertices for scoring (keeps compute time bounded).
                int stride     = Mathf.Max(1, numVerts / MaxSampleVertsPerMesh);
                int numSamples = (numVerts + stride - 1) / stride;
                var sampledScores = new float[numSamples];

                for (int si = 0; si < numSamples; si++)
                    sampledScores[si] = ComputeVertexOcclusionScore(worldVerts[si * stride], boundsArray);

                // Per-renderer score = mean visibility across sampled vertices.
                float sum = 0f;
                foreach (var s in sampledScores) sum += s;
                rawScores[path] = numSamples > 0 ? sum / numSamples : 1f;

                // Propagate stride-sampled scores to every vertex for the heat-map gizmo.
                var fullVertexScores = new float[numVerts];
                for (int i = 0; i < numVerts; i++)
                    fullVertexScores[i] = sampledScores[Mathf.Clamp(
                        Mathf.RoundToInt((float)i / stride), 0, numSamples - 1)];

                // Build capped flat GL draw arrays (computed once; read every repaint frame).
                debugData.DrawArrays[rendererA] = BuildDrawArrays(bakedMesh, worldVerts, fullVertexScores);

                // The baked mesh is no longer needed — the draw arrays hold all required data.
                UnityEngine.Object.DestroyImmediate(bakedMesh);
            }

            // Normalise scores relative to the most-visible renderer, then blend with 1.0
            // via the aggressiveness slider (0 = even, 1 = full differentiation).
            float maxRaw       = rawScores.Values.DefaultIfEmpty(0f).Max();
            float aggressiveness = simplifier.OcclusionAggressiveness;

            foreach (var (_, path) in rendererPaths)
            {
                float raw        = rawScores.TryGetValue(path, out var r) ? r : 1f;
                float normalized = maxRaw > 0f ? raw / maxRaw : 1f;
                result[path]     = Mathf.Clamp01(Mathf.Lerp(1f, normalized, aggressiveness));
            }

            LastDebugData = debugData;
            return result;
        }

        /// <summary>
        /// Returns a visibility score in [0,1] for a single world-space vertex position
        /// <paramref name="V"/> given a set of occluder AABBs <paramref name="occluderBounds"/>.
        /// <para>
        /// For each of the 6 cardinal viewing directions (+X, −X, +Y, −Y, +Z, −Z), V is considered
        /// "blocked" from that direction if ANY occluder B satisfies:
        /// <list type="bullet">
        ///   <item>B's AABB center is in front of V along the direction (center-depth test), AND</item>
        ///   <item>V's perpendicular 2D projection falls inside B's axis-aligned 2D footprint.</item>
        /// </list>
        /// Returns 1 − (blocked directions / 6).  An isolated vertex with no AABB covering it
        /// scores 1.0; a vertex fully surrounded by clothing AABBs scores 0.0.
        /// </para>
        /// </summary>
        private static float ComputeVertexOcclusionScore(Vector3 V, Bounds[] occluderBounds)
        {
            if (occluderBounds.Length == 0) return 1f;

            // blockedMask bits: +X=1, −X=2, +Y=4, −Y=8, +Z=16, −Z=32
            int blockedMask = 0;

            foreach (var B in occluderBounds)
            {
                if (blockedMask == 0b111111) break; // all 6 directions already blocked

                float minX = B.min.x, maxX = B.max.x;
                float minY = B.min.y, maxY = B.max.y;
                float minZ = B.min.z, maxZ = B.max.z;
                float cx   = B.center.x, cy = B.center.y, cz = B.center.z;

                // Pre-compute 2D footprint containment for each perpendicular plane.
                bool inYZ = V.y >= minY && V.y <= maxY && V.z >= minZ && V.z <= maxZ;
                bool inXZ = V.x >= minX && V.x <= maxX && V.z >= minZ && V.z <= maxZ;
                bool inXY = V.x >= minX && V.x <= maxX && V.y >= minY && V.y <= maxY;

                if ((blockedMask & 1)  == 0 && cx > V.x && inYZ) blockedMask |= 1;  // +X viewer
                if ((blockedMask & 2)  == 0 && cx < V.x && inYZ) blockedMask |= 2;  // −X viewer
                if ((blockedMask & 4)  == 0 && cy > V.y && inXZ) blockedMask |= 4;  // +Y viewer
                if ((blockedMask & 8)  == 0 && cy < V.y && inXZ) blockedMask |= 8;  // −Y viewer
                if ((blockedMask & 16) == 0 && cz > V.z && inXY) blockedMask |= 16; // +Z viewer
                if ((blockedMask & 32) == 0 && cz < V.z && inXY) blockedMask |= 32; // −Z viewer
            }

            // Popcount the blocked-direction bits.
            int blocked = 0;
            for (int m = blockedMask; m != 0; m >>= 1) blocked += m & 1;
            return 1f - blocked / 6f;
        }

        /// <summary>
        /// Builds flat GL-triangle draw arrays (world-space positions + RGBA heat-map colors)
        /// for fast single-pass scene-view rendering.  Triangle count is capped at
        /// <see cref="MaxDrawTrisPerMesh"/> by striding the index buffer.
        /// </summary>
        private static (Vector3[] positions, Color[] colors) BuildDrawArrays(
            Mesh bakedMesh, Vector3[] worldVerts, float[] vertexScores)
        {
            // Count total triangles across all sub-meshes.
            int totalTris = 0;
            for (int sub = 0; sub < bakedMesh.subMeshCount; sub++)
                totalTris += bakedMesh.GetTriangles(sub).Length / 3;

            // Every Nth triangle is drawn; remaining are skipped.
            int drawStride = Mathf.Max(1, totalTris / MaxDrawTrisPerMesh);
            int maxOutVerts = ((totalTris + drawStride - 1) / drawStride) * 3;

            var positions = new Vector3[maxOutVerts];
            var colors    = new Color[maxOutVerts];
            int outIdx    = 0;
            int triIdx    = 0;

            for (int sub = 0; sub < bakedMesh.subMeshCount; sub++)
            {
                var tris = bakedMesh.GetTriangles(sub);
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    // Keep only every drawStride-th triangle.
                    if (triIdx++ % drawStride != 0) continue;
                    if (outIdx + 3 > positions.Length) break;

                    int v0 = tris[i], v1 = tris[i + 1], v2 = tris[i + 2];

                    // Cast to uint: negative indices become large values > Length, giving a single
                    // comparison that catches both negative and out-of-range index in one branch.
                    if ((uint)v0 >= (uint)worldVerts.Length
                     || (uint)v1 >= (uint)worldVerts.Length
                     || (uint)v2 >= (uint)worldVerts.Length) continue;

                    positions[outIdx] = worldVerts[v0];
                    colors[outIdx++]  = ScoreToColor(v0 < vertexScores.Length ? vertexScores[v0] : 0.5f);
                    positions[outIdx] = worldVerts[v1];
                    colors[outIdx++]  = ScoreToColor(v1 < vertexScores.Length ? vertexScores[v1] : 0.5f);
                    positions[outIdx] = worldVerts[v2];
                    colors[outIdx++]  = ScoreToColor(v2 < vertexScores.Length ? vertexScores[v2] : 0.5f);
                }
            }

            // Trim to actual output count.
            if (outIdx < positions.Length)
            {
                Array.Resize(ref positions, outIdx);
                Array.Resize(ref colors,    outIdx);
            }

            return (positions, colors);
        }

        /// <summary>Maps a visibility score [0,1] to a heat-map colour (red→yellow→green, α=0.55).</summary>
        private static Color ScoreToColor(float t)
        {
            Color c = t <= 0.5f
                ? Color.Lerp(Color.red,    Color.yellow, t * 2f)
                : Color.Lerp(Color.yellow, Color.green,  (t - 0.5f) * 2f);
            c.a = 0.55f;
            return c;
        }

        /// <summary>
        /// Bakes <paramref name="renderer"/> to a <see cref="Mesh"/> whose vertices are in world
        /// space.  For <see cref="SkinnedMeshRenderer"/> this captures the current skinned pose.
        /// Returns <c>null</c> if no mesh could be obtained.  The caller must destroy the mesh.
        /// </summary>
        private static Mesh? BakeRendererWorldSpace(Renderer renderer)
        {
            Mesh? baked = null;

            if (renderer is SkinnedMeshRenderer smr)
            {
                baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                smr.BakeMesh(baked);
            }
            else if (renderer is MeshRenderer mr)
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf?.sharedMesh == null) return null;
                baked = UnityEngine.Object.Instantiate(mf.sharedMesh);
                baked.hideFlags = HideFlags.HideAndDontSave;
            }

            if (baked == null || baked.vertexCount == 0)
            {
                if (baked != null) UnityEngine.Object.DestroyImmediate(baked);
                return null;
            }

            // Transform vertices from renderer local space to world space.
            var verts = baked.vertices;
            var m     = renderer.transform.localToWorldMatrix;
            for (int i = 0; i < verts.Length; i++)
                verts[i] = m.MultiplyPoint3x4(verts[i]);
            baked.vertices = verts;
            baked.RecalculateBounds();
            return baked;
        }
    }
}

#endif

