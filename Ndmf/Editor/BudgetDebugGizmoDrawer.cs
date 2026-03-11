#nullable enable

#if ENABLE_MODULAR_AVATAR

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    [InitializeOnLoad]
    internal static class BudgetDebugGizmoDrawer
    {
        /// <summary>
        /// When true, draws a per-vertex occlusion heat-map overlay on each renderer's mesh
        /// and AABB wire-box labels in the Scene View when the avatar is selected.
        /// </summary>
        public static bool DebugGizmosEnabled = false;

        private static Material? _heatmapMat;

        static BudgetDebugGizmoDrawer()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!DebugGizmosEnabled) return;

            var selected = Selection.activeGameObject;
            if (selected == null) return;

            var simplifier = selected.GetComponent<MeshiaCascadingAvatarMeshSimplifier>()
                          ?? selected.GetComponentInChildren<MeshiaCascadingAvatarMeshSimplifier>();
            if (simplifier == null && selected.transform.parent != null)
                simplifier = selected.transform.parent.GetComponentInChildren<MeshiaCascadingAvatarMeshSimplifier>();

            if (simplifier == null) return;

            bool useOcclusion = simplifier.AllocationStrategy == BudgetAllocationStrategy.OcclusionBased;
            var debugData = useOcclusion ? OcclusionBudgetAllocator.LastDebugData : null;

            // ── Per-vertex mesh heat-map overlay (occlusion mode only, repaint pass) ──
            // Drawn as semi-transparent GL triangles coloured by per-vertex visibility score.
            // Red = occluded (low score), yellow = partial, green = fully visible.
            if (useOcclusion && debugData != null && Event.current.type == EventType.Repaint)
            {
                var mat = GetOrCreateHeatmapMaterial();
                if (mat != null)
                {
                    mat.SetPass(0);
                    GL.PushMatrix();
                    GL.MultMatrix(Matrix4x4.identity); // vertices are already in world space

                    foreach (var entry in simplifier.Entries)
                    {
                        var renderer = entry.GetTargetRenderer(simplifier);
                        if (renderer == null) continue;
                        if (!debugData.BakedMeshes.TryGetValue(renderer, out var mesh)) continue;
                        if (!debugData.VertexScores.TryGetValue(renderer, out var scores)) continue;

                        DrawMeshHeatmap(mesh, scores);
                    }

                    GL.PopMatrix();
                }
            }

            // ── AABB wire-box + label overlay (all modes) ──
            foreach (var entry in simplifier.Entries)
            {
                var renderer = entry.GetTargetRenderer(simplifier);
                if (renderer == null) continue;

                var bounds = renderer.bounds;
                int originalCount = RendererUtility.GetMesh(renderer)?.GetTriangleCount() ?? 0;
                int targetCount = entry.TargetTriangleCount;

                float ratio = originalCount > 0 ? Mathf.Clamp01((float)targetCount / originalCount) : 1f;

                // Wire box colour matches the simplification ratio heat-map.
                Handles.color = HeatMapColor(ratio);
                Handles.DrawWireCube(bounds.center, bounds.size);

                int pct = Mathf.RoundToInt(ratio * 100f);
                string label = $"{renderer.gameObject.name}\n{targetCount}/{originalCount} ({pct}%)";

                if (useOcclusion
                    && simplifier.CachedVisibilityScores?.TryGetValue(
                            entry.RendererObjectReference.referencePath, out var visScore) == true)
                {
                    label += $"\nvis: {visScore:F2}";
                }

                Handles.Label(bounds.center + Vector3.up * (bounds.extents.y + 0.05f), label);
            }
        }

        /// <summary>
        /// Draws <paramref name="mesh"/> (world-space vertices) as GL_TRIANGLES with each vertex
        /// coloured by its entry in <paramref name="scores"/>, using the red→yellow→green heat-map.
        /// </summary>
        private static void DrawMeshHeatmap(Mesh mesh, float[] scores)
        {
            var vertices = mesh.vertices;
            if (vertices.Length == 0) return;

            GL.Begin(GL.TRIANGLES);

            for (int subIdx = 0; subIdx < mesh.subMeshCount; subIdx++)
            {
                var triangles = mesh.GetTriangles(subIdx);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int v0 = triangles[i];
                    int v1 = triangles[i + 1];
                    int v2 = triangles[i + 2];

                    // Guard against malformed index buffers.
                    if ((uint)v0 >= (uint)vertices.Length
                     || (uint)v1 >= (uint)vertices.Length
                     || (uint)v2 >= (uint)vertices.Length) continue;

                    Color c0 = HeatMapColor(v0 < scores.Length ? scores[v0] : 0.5f); c0.a = 0.55f;
                    Color c1 = HeatMapColor(v1 < scores.Length ? scores[v1] : 0.5f); c1.a = 0.55f;
                    Color c2 = HeatMapColor(v2 < scores.Length ? scores[v2] : 0.5f); c2.a = 0.55f;

                    GL.Color(c0); GL.Vertex(vertices[v0]);
                    GL.Color(c1); GL.Vertex(vertices[v1]);
                    GL.Color(c2); GL.Vertex(vertices[v2]);
                }
            }

            GL.End();
        }

        private static Material? GetOrCreateHeatmapMaterial()
        {
            if (_heatmapMat != null) return _heatmapMat;

            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return null; // shader not available in this Unity version

            _heatmapMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _heatmapMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _heatmapMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _heatmapMat.SetInt("_ZWrite", 0);         // don't occlude scene handles
            _heatmapMat.SetInt("_Cull", (int)CullMode.Off);  // show both sides
            _heatmapMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            return _heatmapMat;
        }

        private static Color HeatMapColor(float t)
        {
            // t=0 → red, t=0.5 → yellow, t=1 → green
            if (t <= 0.5f)
                return Color.Lerp(Color.red, Color.yellow, t * 2f);
            else
                return Color.Lerp(Color.yellow, Color.green, (t - 0.5f) * 2f);
        }
    }
}

#endif

