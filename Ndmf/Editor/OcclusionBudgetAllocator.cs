#nullable enable

#if ENABLE_MODULAR_AVATAR

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    /// <summary>
    /// Holds per-vertex visibility scores and world-space baked meshes produced by
    /// <see cref="OcclusionBudgetAllocator.ComputeVisibilityScores"/>.
    /// Used by <see cref="BudgetDebugGizmoDrawer"/> to draw the mesh heat-map overlay.
    /// </summary>
    internal sealed class OcclusionDebugData : IDisposable
    {
        /// <summary>World-space baked mesh per renderer (vertices are in world space).</summary>
        public readonly Dictionary<Renderer, Mesh> BakedMeshes = new();

        /// <summary>
        /// Per-vertex visibility score [0,1] for each renderer.
        /// Array length equals <c>BakedMeshes[renderer].vertexCount</c>.
        /// </summary>
        public readonly Dictionary<Renderer, float[]> VertexScores = new();

        public void Dispose()
        {
            foreach (var mesh in BakedMeshes.Values)
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            BakedMeshes.Clear();
            VertexScores.Clear();
        }
    }

    internal static class OcclusionBudgetAllocator
    {
        /// <summary>Maximum number of vertices sampled per renderer for visibility scoring.</summary>
        private const int MaxSampleVertsPerMesh = 500;

        /// <summary>Number of Fibonacci-sphere view directions used for per-vertex visibility.</summary>
        private const int RaySampleCount = 64;

        /// <summary>
        /// Debug data produced by the last <see cref="ComputeVisibilityScores"/> call.
        /// Used by <see cref="BudgetDebugGizmoDrawer"/> for the mesh heat-map gizmo.
        /// Null until the first compute or after a domain reload.
        /// </summary>
        public static OcclusionDebugData? LastDebugData;

        /// <summary>
        /// Computes a visibility score [0,1] for each renderer owned by <paramref name="simplifier"/>.
        /// Score 1.0 = most visible renderer; 0.0 = most occluded.
        /// <para>
        /// Uses <b>per-vertex occlusion sampling</b>: each renderer's skinned mesh is baked into
        /// world space, then for up to <see cref="MaxSampleVertsPerMesh"/> surface vertices
        /// <see cref="RaySampleCount"/> Fibonacci-sphere view directions are tested. A vertex is
        /// considered visible from a direction when no other renderer's AABB blocks the ray from
        /// that vertex toward the viewer. The per-renderer score is the mean vertex visibility,
        /// normalised by the most-visible renderer and then blended with 1.0 via aggressiveness.
        /// </para>
        /// <para>
        /// Also populates <see cref="LastDebugData"/> with the full baked meshes and propagated
        /// per-vertex scores for the Scene View heat-map gizmo.
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

            // Gather renderers and their reference paths.
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
            var directions = GenerateFibonacciSphere(RaySampleCount);
            int numDirs = directions.Length;

            // Precompute per-(direction, renderer) front-face depth to avoid redundant work
            // inside the inner vertex loop.
            // frontDepths[di][ri] = maximum extent of renderer[ri]'s AABB along directions[di],
            // i.e. the depth of the face closest to a viewer looking from directions[di].
            var frontDepths = new float[numDirs][];
            for (int di = 0; di < numDirs; di++)
            {
                var dir = directions[di];
                var absDir = new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z));
                frontDepths[di] = new float[numRenderers];
                for (int ri = 0; ri < numRenderers; ri++)
                {
                    var b = allRenderers[ri].bounds;
                    frontDepths[di][ri] = Vector3.Dot(b.center, dir) + Vector3.Dot(b.extents, absDir);
                }
            }

            // Per-renderer raw mean visibility (before normalisation).
            var rawScores = new Dictionary<string, float>();

            for (int aIdx = 0; aIdx < numRenderers; aIdx++)
            {
                var (rendererA, path) = rendererPaths[aIdx];

                // Bake to a world-space mesh so we can test actual surface positions.
                var bakedMesh = BakeRendererWorldSpace(rendererA);
                if (bakedMesh == null || bakedMesh.vertexCount == 0)
                {
                    if (bakedMesh != null) UnityEngine.Object.DestroyImmediate(bakedMesh);
                    rawScores[path] = 1f;
                    continue;
                }

                // Cache the full baked mesh for the heat-map gizmo.
                debugData.BakedMeshes[rendererA] = bakedMesh;

                var worldVerts = bakedMesh.vertices; // world-space
                int numVerts = worldVerts.Length;

                // Stride-sample vertices for scoring to bound compute time.
                int stride = Mathf.Max(1, numVerts / MaxSampleVertsPerMesh);
                int numSamples = (numVerts + stride - 1) / stride;
                var sampledScores = new float[numSamples];

                for (int si = 0; si < numSamples; si++)
                {
                    var P = worldVerts[si * stride];
                    int visCount = 0;

                    for (int di = 0; di < numDirs; di++)
                    {
                        var dir = directions[di];
                        // Depth of this vertex along the current view direction.
                        float vertexDepth = Vector3.Dot(P, dir);
                        // Ray from the vertex outward toward the viewer in direction `dir`.
                        var ray = new Ray(P, dir);
                        bool blocked = false;

                        for (int bi = 0; bi < numRenderers; bi++)
                        {
                            if (bi == aIdx) continue;
                            // Early-out: skip renderers whose AABB front face is entirely
                            // behind (or level with) the vertex — they cannot occlude it.
                            if (frontDepths[di][bi] <= vertexDepth) continue;
                            // The AABB front face is in front of the vertex; check if the
                            // view ray actually passes through the AABB.
                            if (allRenderers[bi].bounds.IntersectRay(ray))
                            {
                                blocked = true;
                                break;
                            }
                        }

                        if (!blocked) visCount++;
                    }

                    sampledScores[si] = (float)visCount / numDirs;
                }

                // Per-renderer score = mean visibility across sampled vertices.
                float sum = 0f;
                foreach (var s in sampledScores) sum += s;
                rawScores[path] = numSamples > 0 ? sum / numSamples : 1f;

                // Propagate sampled scores to every vertex in the full mesh for the heat-map.
                // Each full-mesh vertex at index i receives the score of the stride-aligned
                // sample at index i/stride. For meshes where consecutive vertex indices are
                // spatially close (common in well-authored character meshes) this produces a
                // smooth per-region colour gradient without additional nearest-neighbour work.
                var fullVertexScores = new float[numVerts];
                for (int i = 0; i < numVerts; i++)
                    fullVertexScores[i] = sampledScores[Mathf.Clamp(Mathf.RoundToInt((float)i / stride), 0, numSamples - 1)];

                debugData.VertexScores[rendererA] = fullVertexScores;
            }

            // Normalise scores relative to the most-visible renderer, then blend with 1.0
            // via the aggressiveness slider so that 0 = even distribution, 1 = full differentiation.
            float maxRaw = rawScores.Values.DefaultIfEmpty(0f).Max();
            float aggressiveness = simplifier.OcclusionAggressiveness;

            foreach (var (_, path) in rendererPaths)
            {
                float raw = rawScores.TryGetValue(path, out var r) ? r : 1f;
                float normalized = maxRaw > 0f ? raw / maxRaw : 1f;
                float effectiveScore = Mathf.Lerp(1f, normalized, aggressiveness);
                result[path] = Mathf.Clamp01(effectiveScore);
            }

            LastDebugData = debugData;
            return result;
        }

        /// <summary>
        /// Bakes <paramref name="renderer"/> to a new <see cref="Mesh"/> whose vertices are in
        /// world space. For <see cref="SkinnedMeshRenderer"/> this captures the current skinned pose.
        /// Returns <c>null</c> if no mesh could be obtained. Caller must destroy the returned mesh.
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

            // Transform vertices from the renderer's local space to world space.
            var verts = baked.vertices;
            var m = renderer.transform.localToWorldMatrix;
            for (int i = 0; i < verts.Length; i++)
                verts[i] = m.MultiplyPoint3x4(verts[i]);
            baked.vertices = verts;
            baked.RecalculateBounds();
            return baked;
        }

        /// <summary>Generates evenly-distributed unit directions on a sphere using the Fibonacci / golden-angle method.</summary>
        private static Vector3[] GenerateFibonacciSphere(int count)
        {
            if (count <= 0) return Array.Empty<Vector3>();
            if (count == 1) return new[] { Vector3.up };

            var points = new Vector3[count];
            float angleIncrement = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < count; i++)
            {
                float y = 1f - (i / (float)(count - 1)) * 2f;
                float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float theta = angleIncrement * i;
                points[i] = new Vector3(Mathf.Cos(theta) * radius, y, Mathf.Sin(theta) * radius);
            }
            return points;
        }
    }
}

#endif

