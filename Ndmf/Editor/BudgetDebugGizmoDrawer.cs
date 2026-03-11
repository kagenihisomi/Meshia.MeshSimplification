#nullable enable

#if ENABLE_MODULAR_AVATAR

using UnityEditor;
using UnityEngine;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    [InitializeOnLoad]
    internal static class BudgetDebugGizmoDrawer
    {
        /// <summary>
        /// When true, draws colored wire bounding boxes and labels for each renderer entry
        /// in the Scene View when a GameObject with MeshiaCascadingAvatarMeshSimplifier is selected.
        /// </summary>
        public static bool DebugGizmosEnabled = false;

        static BudgetDebugGizmoDrawer()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!DebugGizmosEnabled) return;

            var selected = Selection.activeGameObject;
            if (selected == null) return;

            // Look for the simplifier on the selected object or its children/parent
            var simplifier = selected.GetComponent<MeshiaCascadingAvatarMeshSimplifier>()
                          ?? selected.GetComponentInChildren<MeshiaCascadingAvatarMeshSimplifier>();
            if (simplifier == null && selected.transform.parent != null)
                simplifier = selected.transform.parent.GetComponentInChildren<MeshiaCascadingAvatarMeshSimplifier>();

            if (simplifier == null) return;

            bool useOcclusion = simplifier.AllocationStrategy == BudgetAllocationStrategy.OcclusionBased;

            foreach (var entry in simplifier.Entries)
            {
                var renderer = entry.GetTargetRenderer(simplifier);
                if (renderer == null) continue;

                var bounds = renderer.bounds;
                int originalCount = RendererUtility.GetMesh(renderer)?.GetTriangleCount() ?? 0;
                int targetCount = entry.TargetTriangleCount;

                float ratio = originalCount > 0 ? Mathf.Clamp01((float)targetCount / originalCount) : 1f;

                // Heat map color: green (1.0) → yellow (0.5) → red (0.0)
                Color color = HeatMapColor(ratio);

                Handles.color = color;
                Handles.DrawWireCube(bounds.center, bounds.size);

                // Build label text
                int pct = Mathf.RoundToInt(ratio * 100f);
                string label = $"{renderer.gameObject.name}\n{targetCount}/{originalCount} ({pct}%)";

                if (useOcclusion
                    && simplifier.CachedVisibilityScores?.TryGetValue(entry.RendererObjectReference.referencePath, out var visScore) == true)
                {
                    label += $"\nvis: {visScore:F2}";
                }

                // Draw label above the bounding box
                Handles.Label(bounds.center + Vector3.up * (bounds.extents.y + 0.05f), label);
            }
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
