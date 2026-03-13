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
        private const int MaxTrianglesPerMesh = 3000;

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
            BuildDrawArrays(worldSpaceMesh, simplificationWeights);
            SceneView.RepaintAll();
        }

        /// <summary>Clears the preview and stops drawing.</summary>
        internal static void ClearPreviewData()
        {
            _positions = null;
            _colors = null;
            SceneView.RepaintAll();
        }

        private static void BuildDrawArrays(Mesh mesh, float[] weights)
        {
            var meshVertices = mesh.vertices;
            int subMeshCount = mesh.subMeshCount;

            // Count total triangles across all sub-meshes, capped at MaxTrianglesPerMesh
            int totalTriangles = 0;
            for (int s = 0; s < subMeshCount; s++)
            {
                var desc = mesh.GetSubMesh(s);
                if (desc.topology == MeshTopology.Triangles)
                    totalTriangles += desc.indexCount / 3;
            }
            int triangleLimit = Mathf.Min(totalTriangles, MaxTrianglesPerMesh);

            _positions = new Vector3[triangleLimit * 3];
            _colors = new Color[triangleLimit * 3];

            int outputTriangle = 0;
            float strideRatio = triangleLimit < totalTriangles ? (float)totalTriangles / triangleLimit : 1f;

            int inputTriangle = 0;
            int nextInput = 0;

            for (int s = 0; s < subMeshCount && outputTriangle < triangleLimit; s++)
            {
                var desc = mesh.GetSubMesh(s);
                if (desc.topology != MeshTopology.Triangles) continue;
                var indices = mesh.GetTriangles(s);

                for (int t = 0; t < indices.Length / 3 && outputTriangle < triangleLimit; t++, inputTriangle++)
                {
                    // Stride: skip triangles if we're over budget
                    if (inputTriangle < nextInput) continue;
                    nextInput = Mathf.RoundToInt((outputTriangle + 1) * strideRatio);

                    int i0 = indices[t * 3];
                    int i1 = indices[t * 3 + 1];
                    int i2 = indices[t * 3 + 2];

                    int baseOut = outputTriangle * 3;
                    _positions[baseOut + 0] = meshVertices[i0];
                    _positions[baseOut + 1] = meshVertices[i1];
                    _positions[baseOut + 2] = meshVertices[i2];

                    _colors[baseOut + 0] = WeightToColor(i0 < weights.Length ? weights[i0] : 1f);
                    _colors[baseOut + 1] = WeightToColor(i1 < weights.Length ? weights[i1] : 1f);
                    _colors[baseOut + 2] = WeightToColor(i2 < weights.Length ? weights[i2] : 1f);

                    outputTriangle++;
                }
            }

            // Trim to actual size
            if (outputTriangle < triangleLimit)
            {
                Array.Resize(ref _positions, outputTriangle * 3);
                Array.Resize(ref _colors, outputTriangle * 3);
            }
        }

        /// <summary>Maps a simplification weight [1, 10] to a green→red heatmap color.</summary>
        private static Color WeightToColor(float weight)
        {
            float t = Mathf.InverseLerp(1f, 10f, weight);
            return Color.Lerp(Color.green, Color.red, t);
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
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
            return _glMaterial;
        }
    }
}

#endif
