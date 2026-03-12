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
            // Uses pre-baked flat arrays built once at compute time; no per-frame mesh traversal.
            // Red = occluded (low score), yellow = partial, green = fully visible (high score).
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
                        if (!debugData.DrawArrays.TryGetValue(renderer, out var arrays)) continue;

                        DrawCachedArrays(arrays.positions, arrays.colors);
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
                int targetCount   = entry.TargetTriangleCount;

                float ratio = originalCount > 0 ? Mathf.Clamp01((float)targetCount / originalCount) : 1f;

                Handles.color = HeatMapColor(ratio);
                Handles.DrawWireCube(bounds.center, bounds.size);

                int    pct   = Mathf.RoundToInt(ratio * 100f);
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
        /// Draws pre-baked GL triangle arrays (world-space positions, RGBA colors).
        /// Arrays were built once at compute time; calling this each repaint is fast —
        /// it is a tight loop over contiguous managed arrays with no dictionary lookups
        /// or index-buffer traversal.
        /// </summary>
        private static void DrawCachedArrays(Vector3[] positions, Color[] colors)
        {
            if (positions.Length == 0) return;

            GL.Begin(GL.TRIANGLES);
            for (int i = 0; i < positions.Length; i++)
            {
                GL.Color(colors[i]);
                GL.Vertex(positions[i]);
            }
            GL.End();
        }

        private static Material? GetOrCreateHeatmapMaterial()
        {
            if (_heatmapMat != null) return _heatmapMat;

            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return null;

            _heatmapMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _heatmapMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _heatmapMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _heatmapMat.SetInt("_ZWrite",   0);                              // don't write depth
            _heatmapMat.SetInt("_Cull",     (int)CullMode.Off);             // both sides visible
            _heatmapMat.SetInt("_ZTest",    (int)CompareFunction.LessEqual);
            return _heatmapMat;
        }

        private static Color HeatMapColor(float t)
        {
            if (t <= 0.5f)
                return Color.Lerp(Color.red,    Color.yellow, t * 2f);
            else
                return Color.Lerp(Color.yellow, Color.green,  (t - 0.5f) * 2f);
        }
    }
}

#endif

