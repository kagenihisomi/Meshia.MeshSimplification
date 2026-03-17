#nullable enable
#if ENABLE_MODULAR_AVATAR

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    /// <summary>
    /// Draws a per-vertex occlusion weight heatmap over the mesh surface in the Scene View.
    /// Renders actual mesh triangles (GL.TRIANGLES) with per-vertex interpolated colors.
    /// Green = weight 1.0 (visible, preserved), Red = weight 10.0 (occluded, simplified aggressively).
    /// Uses pre-baked triangle arrays for responsiveness; no per-frame mesh traversal.
    /// </summary>
    [InitializeOnLoad]
    internal static class OcclusionWeightGizmoDrawer
    {
        // Editor-pref keys and defaults
        private const string PrefKeyMaxVerticesPerMesh = "Meshia.Occlusion.MaxVerticesPerMesh";
        private const string PrefKeyMaxVerticesTotal = "Meshia.Occlusion.MaxVerticesTotal";
        private const string LegacyPrefKeyMaxTrianglesPerMesh = "Meshia.Occlusion.MaxTrianglesPerMesh";
        private const string LegacyPrefKeyMaxTrianglesTotal = "Meshia.Occlusion.MaxTrianglesTotal";
        private const int PrefDefaultMaxVerticesPerMesh = 20000;
        private const int PrefDefaultMaxVerticesTotal = 80000;
        private const float ContrastGamma = 0.55f;
        private const string DefaultPreviewId = "default";

        // Small offset along face normal to prevent z-fighting with the actual mesh surface.
        private const float NormalOffset = 0.0005f;

        private static int MaxVerticesPerMesh =>
            GetIntWithLegacyFallback(PrefKeyMaxVerticesPerMesh, LegacyPrefKeyMaxTrianglesPerMesh, PrefDefaultMaxVerticesPerMesh);

        private static int MaxVerticesTotal =>
            GetIntWithLegacyFallback(PrefKeyMaxVerticesTotal, LegacyPrefKeyMaxTrianglesTotal, PrefDefaultMaxVerticesTotal);

        private sealed class PreviewBatch
        {
            public Vector3[] Positions;
            public Color[] Colors;
            public bool Enabled;

            public PreviewBatch(Vector3[] positions, Color[] colors, bool enabled)
            {
                Positions = positions;
                Colors = colors;
                Enabled = enabled;
            }
        }

        [NonSerialized] private static readonly Dictionary<string, PreviewBatch> _previewBatches = new();
        [NonSerialized] private static Texture2D? _legendTexture;
        [NonSerialized] private static string? _legendPaletteKey;
        [NonSerialized] private static int _legacyAppendCounter;

        static OcclusionWeightGizmoDrawer()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        /// <summary>
        /// Sets the preview data from baked world-space mesh + per-vertex weights.
        /// Call this when "Preview Occlusion Weights" is clicked.
        /// </summary>
        /// <param name="worldSpaceMesh">The mesh with vertices already in world space.</param>
        /// <param name="simplificationWeights">Per-vertex weights (1.0 = preserve, 10.0 = aggressive).</param>
        internal static void SetPreviewData(Mesh worldSpaceMesh, float[] simplificationWeights)
        {
            SetPreviewData(DefaultPreviewId, worldSpaceMesh, simplificationWeights, true);
        }

        internal static void SetPreviewData(string previewId, Mesh worldSpaceMesh, float[] simplificationWeights, bool enabled)
        {
            BuildDrawArraysForMesh(worldSpaceMesh, simplificationWeights, out var positions, out var colors);

            if (positions == null || colors == null || positions.Length == 0)
            {
                _previewBatches.Remove(previewId);
                SceneView.RepaintAll();
                return;
            }

            _previewBatches[previewId] = new PreviewBatch(positions, colors, enabled);
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Appends preview data for an additional mesh. Useful for showing occlusion
        /// heatmaps across multiple meshes at once.
        /// </summary>
        internal static void AppendPreviewData(Mesh worldSpaceMesh, float[] simplificationWeights)
        {
            SetPreviewData($"legacy-{_legacyAppendCounter++}", worldSpaceMesh, simplificationWeights, true);
        }

        /// <summary>Clears the preview and stops drawing.</summary>
        internal static void ClearPreviewData()
        {
            _previewBatches.Clear();
            _legacyAppendCounter = 0;
            SceneView.RepaintAll();
        }

        internal static void RemovePreviewData(string previewId)
        {
            if (_previewBatches.Remove(previewId))
                SceneView.RepaintAll();
        }

        internal static void RemovePreviewDataForPrefix(string previewPrefix)
        {
            bool changed = false;
            var keysToRemove = new List<string>();
            foreach (var pair in _previewBatches)
            {
                if (pair.Key.StartsWith(previewPrefix, StringComparison.Ordinal))
                    keysToRemove.Add(pair.Key);
            }

            foreach (var key in keysToRemove)
            {
                changed |= _previewBatches.Remove(key);
            }

            if (changed)
                SceneView.RepaintAll();
        }

        internal static bool HasPreviewData(string previewId)
        {
            return _previewBatches.TryGetValue(previewId, out var batch) && batch.Positions.Length > 0;
        }

        internal static bool HasPreviewDataForPrefix(string previewPrefix)
        {
            foreach (var pair in _previewBatches)
            {
                if (pair.Key.StartsWith(previewPrefix, StringComparison.Ordinal) && pair.Value.Positions.Length > 0)
                    return true;
            }

            return false;
        }

        internal static bool IsPreviewEnabled(string previewId)
        {
            return _previewBatches.TryGetValue(previewId, out var batch) && batch.Enabled;
        }

        internal static void SetPreviewEnabled(string previewId, bool enabled)
        {
            if (!_previewBatches.TryGetValue(previewId, out var batch))
                return;

            if (batch.Enabled == enabled)
                return;

            batch.Enabled = enabled;
            SceneView.RepaintAll();
        }

        internal static void SetPreviewEnabledForPrefix(string previewPrefix, bool enabled)
        {
            bool changed = false;
            foreach (var pair in _previewBatches)
            {
                if (!pair.Key.StartsWith(previewPrefix, StringComparison.Ordinal))
                    continue;

                if (pair.Value.Enabled == enabled)
                    continue;

                pair.Value.Enabled = enabled;
                changed = true;
            }

            if (changed)
                SceneView.RepaintAll();
        }

        internal static void GetPreviewCountsForPrefix(string previewPrefix, out int total, out int enabled)
        {
            total = 0;
            enabled = 0;
            foreach (var pair in _previewBatches)
            {
                if (!pair.Key.StartsWith(previewPrefix, StringComparison.Ordinal))
                    continue;

                total++;
                if (pair.Value.Enabled)
                    enabled++;
            }
        }

        private static void BuildDrawArraysForMesh(Mesh mesh, float[] weights, out Vector3[]? outPositions, out Color[]? outColors)
        {
            var meshVertices = mesh.vertices;
            var meshTriangles = mesh.triangles;
            int vertexCount = meshVertices.Length;
            int triangleCount = meshTriangles.Length / 3;

            if (vertexCount == 0 || triangleCount == 0)
            {
                outPositions = Array.Empty<Vector3>();
                outColors = Array.Empty<Color>();
                return;
            }

            // Budget: MaxVerticesPerMesh is the triangle-vertex budget (each triangle = 3 vertices).
            int maxTriVerts = Mathf.Max(3, MaxVerticesPerMesh);
            int maxTris = maxTriVerts / 3;

            // Sample every Nth triangle if over budget.
            int stride = triangleCount > maxTris ? (triangleCount + maxTris - 1) / maxTris : 1;
            int sampledTriCount = Mathf.Min((triangleCount + stride - 1) / stride, maxTris);

            var positions = new Vector3[sampledTriCount * 3];
            var colors = new Color[sampledTriCount * 3];

            int outIdx = 0;
            for (int t = 0; t < triangleCount && outIdx + 2 < positions.Length; t += stride)
            {
                int baseIdx = t * 3;
                int i0 = meshTriangles[baseIdx];
                int i1 = meshTriangles[baseIdx + 1];
                int i2 = meshTriangles[baseIdx + 2];

                if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount)
                    continue;

                Vector3 v0 = meshVertices[i0];
                Vector3 v1 = meshVertices[i1];
                Vector3 v2 = meshVertices[i2];

                // Slight offset along face normal to prevent z-fighting with the actual mesh.
                Vector3 faceNormal = Vector3.Cross(v1 - v0, v2 - v0);
                float areaSq = faceNormal.sqrMagnitude;
                if (areaSq > 1e-12f)
                    faceNormal = faceNormal * (NormalOffset / Mathf.Sqrt(areaSq));
                else
                    faceNormal = Vector3.zero;

                positions[outIdx] = v0 + faceNormal;
                colors[outIdx] = WeightToColor(i0 < weights.Length ? weights[i0] : 1f);
                outIdx++;

                positions[outIdx] = v1 + faceNormal;
                colors[outIdx] = WeightToColor(i1 < weights.Length ? weights[i1] : 1f);
                outIdx++;

                positions[outIdx] = v2 + faceNormal;
                colors[outIdx] = WeightToColor(i2 < weights.Length ? weights[i2] : 1f);
                outIdx++;
            }

            // Trim to actual size if fewer triangles were emitted (due to degenerate/skipped tris).
            if (outIdx < positions.Length)
            {
                Array.Resize(ref positions, outIdx);
                Array.Resize(ref colors, outIdx);
            }

            outPositions = positions;
            outColors = colors;
        }

        /// <summary>Maps a simplification weight [1, 10] to a green→red heatmap color.</summary>
        private static Color WeightToColor(float weight)
        {
            float t = Mathf.InverseLerp(1f, 10f, weight);
            t = Mathf.Pow(t, ContrastGamma);
            var palette = EditorPrefs.GetString("Meshia.Occlusion.ColorPalette", "Viridis");
            Color c = palette switch
            {
                "BlueRed" => Color.Lerp(Color.blue, Color.red, t),
                "GreenRed" => Color.Lerp(Color.green, Color.red, t),
                _ => Viridis(t),
            };
            c.a = 0.85f;
            return c;
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (_previewBatches.Count == 0) return;

            Material? mat = GetGLMaterial();
            if (mat == null) return;

            mat.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);
            GL.Begin(GL.TRIANGLES);

            int drawnVertices = 0;
            int maxVertices = MaxVerticesTotal;
            foreach (var pair in _previewBatches)
            {
                var batch = pair.Value;
                if (!batch.Enabled) continue;

                var positions = batch.Positions;
                var colors = batch.Colors;
                if (positions.Length == 0) continue;

                int remaining = maxVertices - drawnVertices;
                if (remaining <= 0) break;

                // Ensure we draw complete triangles (multiple of 3).
                int drawCount = Mathf.Min(remaining, positions.Length);
                drawCount = (drawCount / 3) * 3;

                for (int i = 0; i < drawCount; i++)
                {
                    GL.Color(colors[i]);
                    GL.Vertex3(positions[i].x, positions[i].y, positions[i].z);
                }

                drawnVertices += drawCount;
            }

            GL.End();
            GL.PopMatrix();

            if (drawnVertices > 0)
                DrawLegendBar();
        }

        private static Material? _glMaterial;

        private static Material? GetGLMaterial()
        {
            if (_glMaterial != null) return _glMaterial;
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return null;
            _glMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _glMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _glMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _glMaterial.SetInt("_Cull", (int)CullMode.Off);
            _glMaterial.SetInt("_ZWrite", 0);
            _glMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            return _glMaterial;
        }

        private static void DrawLegendBar()
        {
            Handles.BeginGUI();

            var panelRect = new Rect(16f, 16f, 220f, 74f);
            GUI.Box(panelRect, "Occlusion Weight");

            var gradientRect = new Rect(panelRect.x + 10f, panelRect.y + 30f, panelRect.width - 20f, 16f);
            GUI.DrawTexture(gradientRect, GetLegendTexture(), ScaleMode.StretchToFill, false);

            var leftLabelRect = new Rect(gradientRect.x, gradientRect.yMax + 2f, 80f, 16f);
            var rightLabelRect = new Rect(gradientRect.xMax - 100f, gradientRect.yMax + 2f, 100f, 16f);
            GUI.Label(leftLabelRect, "Visible 1.0", EditorStyles.miniLabel);
            GUI.Label(rightLabelRect, "Occluded 10.0", EditorStyles.miniLabel);

            Handles.EndGUI();
        }

        private static Texture2D GetLegendTexture()
        {
            var palette = EditorPrefs.GetString("Meshia.Occlusion.ColorPalette", "Viridis");
            if (_legendTexture != null && string.Equals(_legendPaletteKey, palette, StringComparison.Ordinal))
                return _legendTexture;

            if (_legendTexture != null)
            {
                UnityEngine.Object.DestroyImmediate(_legendTexture);
                _legendTexture = null;
            }

            _legendTexture = new Texture2D(128, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            for (int x = 0; x < _legendTexture.width; x++)
            {
                float t = x / (_legendTexture.width - 1f);
                t = Mathf.Pow(t, ContrastGamma);
                Color c = palette switch
                {
                    "BlueRed" => Color.Lerp(Color.blue, Color.red, t),
                    "GreenRed" => Color.Lerp(Color.green, Color.red, t),
                    _ => Viridis(t),
                };
                _legendTexture.SetPixel(x, 0, c);
            }
            _legendTexture.Apply(false, true);
            _legendPaletteKey = palette;

            return _legendTexture;
        }

        // Viridis approximation (colorblind-friendly) — returns Color at t in [0,1]
        private static Color Viridis(float t)
        {
            t = Mathf.Clamp01(t);
            // coefficients from matplotlib viridis sampled stops
            // simple polynomial interpolation between a few control points to avoid extra dependency
            if (t < 0.25f)
            {
                // from deep blue to teal
                float u = t / 0.25f;
                return Color.Lerp(new Color(0.267f, 0.004f, 0.329f), new Color(0.229f, 0.322f, 0.545f), u);
            }
            else if (t < 0.5f)
            {
                float u = (t - 0.25f) / 0.25f;
                return Color.Lerp(new Color(0.229f, 0.322f, 0.545f), new Color(0.127f, 0.566f, 0.550f), u);
            }
            else if (t < 0.75f)
            {
                float u = (t - 0.5f) / 0.25f;
                return Color.Lerp(new Color(0.127f, 0.566f, 0.550f), new Color(0.713f, 0.862f, 0.343f), u);
            }
            else
            {
                float u = (t - 0.75f) / 0.25f;
                return Color.Lerp(new Color(0.713f, 0.862f, 0.343f), new Color(0.993f, 0.906f, 0.143f), u);
            }
        }

        private static int GetIntWithLegacyFallback(string key, string legacyKey, int fallback)
        {
            if (EditorPrefs.HasKey(key))
                return EditorPrefs.GetInt(key, fallback);

            if (EditorPrefs.HasKey(legacyKey))
                return EditorPrefs.GetInt(legacyKey, fallback);

            return fallback;
        }
    }
}

#endif
