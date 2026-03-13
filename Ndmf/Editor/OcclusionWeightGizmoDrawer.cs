#nullable enable
#if ENABLE_MODULAR_AVATAR

using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    /// <summary>
    /// Draws a per-vertex occlusion weight heatmap in the Scene View.
    /// Green = weight 1.0 (visible, preserved), Red = weight 10.0 (occluded, simplified aggressively).
    /// Uses pre-baked GL draw arrays for responsiveness; no per-frame mesh traversal.
    /// </summary>
    [InitializeOnLoad]
    internal static class OcclusionWeightGizmoDrawer
    {
        // Editor-pref keys and defaults
        private const string PrefKeyMaxTrianglesPerMesh = "Meshia.Occlusion.MaxTrianglesPerMesh";
        private const string PrefKeyMaxTrianglesTotal = "Meshia.Occlusion.MaxTrianglesTotal";
        private const int PrefDefaultMaxTrianglesPerMesh = 1000; // Cap to maintain editor performance (90k vertices)
        private const int PrefDefaultMaxTrianglesTotal = 1000; // Cap total triangles across all appended preview meshes

        private static int MaxTrianglesPerMesh => UnityEditor.EditorPrefs.GetInt(PrefKeyMaxTrianglesPerMesh, PrefDefaultMaxTrianglesPerMesh);
        private static int MaxTrianglesTotal => UnityEditor.EditorPrefs.GetInt(PrefKeyMaxTrianglesTotal, PrefDefaultMaxTrianglesTotal);

        // Pre-baked draw data (built once when preview is triggered)
        [NonSerialized] private static Vector3[]? _positions;
        [NonSerialized] private static Color[]? _colors;

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
            // Replace existing preview with the provided single mesh
            ClearPreviewData();
            AppendPreviewData(worldSpaceMesh, simplificationWeights);
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Appends preview data for an additional mesh. Useful for showing occlusion
        /// heatmaps across multiple meshes at once.
        /// </summary>
        internal static void AppendPreviewData(Mesh worldSpaceMesh, float[] simplificationWeights)
        {
            // Build draw arrays for this mesh only
            BuildDrawArraysForMesh(worldSpaceMesh, simplificationWeights, out var positions, out var colors);

            if (positions == null || positions.Length == 0) return;

            if (_positions == null || _positions.Length == 0)
            {
                _positions = positions;
                _colors = colors;
            }
            else
            {
                // Concatenate, but respect MaxTrianglesTotal cap
                int existingTriangles = _positions.Length / 3;
                int incomingTriangles = positions.Length / 3;
                int allowedTriangles = Mathf.Max(0, MaxTrianglesTotal - existingTriangles);
                if (allowedTriangles <= 0) return;

                int trianglesToTake = Mathf.Min(allowedTriangles, incomingTriangles);
                int vertsToTake = trianglesToTake * 3;

                var newPositions = new Vector3[existingTriangles * 3 + vertsToTake];
                var newColors = new Color[existingTriangles * 3 + vertsToTake];
                Array.Copy(_positions, newPositions, _positions.Length);
                Array.Copy(_colors, newColors, _colors.Length);
                Array.Copy(positions, 0, newPositions, _positions.Length, vertsToTake);
                Array.Copy(colors, 0, newColors, _colors.Length, vertsToTake);

                _positions = newPositions;
                _colors = newColors;
            }

            SceneView.RepaintAll();
        }

        /// <summary>Clears the preview and stops drawing.</summary>
        internal static void ClearPreviewData()
        {
            _positions = null;
            _colors = null;
            SceneView.RepaintAll();
        }

        private static void BuildDrawArraysForMesh(Mesh mesh, float[] weights, out Vector3[]? outPositions, out Color[]? outColors)
        {
            var meshVertices = mesh.vertices;
            int subMeshCount = mesh.subMeshCount;

            // Count total triangles across all sub-meshes, capped per-mesh
            int totalTriangles = 0;
            for (int s = 0; s < subMeshCount; s++)
            {
                var desc = mesh.GetSubMesh(s);
                if (desc.topology == MeshTopology.Triangles)
                    totalTriangles += desc.indexCount / 3;
            }
            int triangleLimit = Mathf.Min(totalTriangles, MaxTrianglesPerMesh);

            var positions = new Vector3[triangleLimit * 3];
            var colors = new Color[triangleLimit * 3];

            // Integer stride: sample every Nth triangle (N = totalTriangles / triangleLimit, rounded up)
            int stride = totalTriangles > triangleLimit ? (totalTriangles + triangleLimit - 1) / triangleLimit : 1;

            int outputTriangle = 0;
            int globalTriangleIndex = 0;

            for (int s = 0; s < subMeshCount && outputTriangle < triangleLimit; s++)
            {
                var desc = mesh.GetSubMesh(s);
                if (desc.topology != MeshTopology.Triangles) continue;
                var indices = mesh.GetTriangles(s);

                for (int t = 0; t < indices.Length / 3 && outputTriangle < triangleLimit; t++, globalTriangleIndex++)
                {
                    // Sample only every Nth triangle
                    if (globalTriangleIndex % stride != 0) continue;

                    int i0 = indices[t * 3];
                    int i1 = indices[t * 3 + 1];
                    int i2 = indices[t * 3 + 2];

                    int baseOut = outputTriangle * 3;
                    positions[baseOut + 0] = meshVertices[i0];
                    positions[baseOut + 1] = meshVertices[i1];
                    positions[baseOut + 2] = meshVertices[i2];

                    colors[baseOut + 0] = WeightToColor(i0 < weights.Length ? weights[i0] : 1f);
                    colors[baseOut + 1] = WeightToColor(i1 < weights.Length ? weights[i1] : 1f);
                    colors[baseOut + 2] = WeightToColor(i2 < weights.Length ? weights[i2] : 1f);

                    outputTriangle++;
                }
            }

            // Trim to actual size if fewer triangles were sampled
            if (outputTriangle < triangleLimit)
            {
                Array.Resize(ref positions, outputTriangle * 3);
                Array.Resize(ref colors, outputTriangle * 3);
            }

            outPositions = positions;
            outColors = colors;
        }

        /// <summary>Maps a simplification weight [1, 10] to a green→red heatmap color.</summary>
        private static Color WeightToColor(float weight)
        {
            float t = Mathf.InverseLerp(1f, 10f, weight);
            return Color.Lerp(Color.green, Color.red, t);
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint) return;
            var positions = _positions;
            var colors = _colors;

            if (positions == null || colors == null || positions.Length == 0) return;

            Material? mat = GetGLMaterial();
            if (mat == null) return;

            mat.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);
            GL.Begin(GL.TRIANGLES);

            for (int i = 0; i < positions.Length; i++)
            {
                GL.Color(colors[i]);
                GL.Vertex(positions[i]);
            }

            GL.End();
            GL.PopMatrix();
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
            _glMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
            return _glMaterial;
        }
    }
}

#endif
