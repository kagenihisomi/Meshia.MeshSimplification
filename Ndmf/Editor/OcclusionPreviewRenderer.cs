#nullable enable
#if ENABLE_MODULAR_AVATAR

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    /// <summary>
    /// Creates temporary GameObjects with MeshRenderer + MeshFilter in the scene to display
    /// per-vertex occlusion weight heatmaps.  Unlike GL-based gizmo rendering, this approach
    /// produces real scene objects that correctly depth-test against the avatar, can be selected
    /// in the Hierarchy, and show the exact world-space mesh that occlusion is computed on.
    ///
    /// Per-vertex weights are baked into the mesh's vertex color channel.  A vertex-color
    /// material renders the heatmap directly.
    ///
    /// Color scale:
    ///   Purple/Blue (weight 1.0) = Visible / Preserved — low simplification priority.
    ///   Yellow      (weight 10.0) = Occluded / Simplified — high simplification priority.
    /// </summary>
    [InitializeOnLoad]
    internal static class OcclusionPreviewRenderer
    {
        private const float ContrastGamma = 0.55f;
        private const string PreviewRootName = "MeshiaOcclusionPreview";
        private const string OccluderRootName = "MeshiaOcclusionOccluders";

        private sealed class PreviewEntry
        {
            public readonly string Id;
            public readonly GameObject Go;
            public readonly MeshFilter Filter;
            public readonly MeshRenderer Renderer;
            public readonly Mesh Mesh;

            public PreviewEntry(string id, GameObject go, MeshFilter filter, MeshRenderer renderer, Mesh mesh)
            {
                Id = id;
                Go = go;
                Filter = filter;
                Renderer = renderer;
                Mesh = mesh;
            }
        }

        private static readonly Dictionary<string, PreviewEntry> _entries = new();
        private static GameObject? _previewRoot;
        private static GameObject? _occluderRoot;
        private static readonly List<GameObject> _occluderDebugObjects = new();
        private static Material? _vertexColorMaterial;
        private static Material? _occluderDebugMaterial;

        static OcclusionPreviewRenderer()
        {
            // Clean up when entering/exiting play mode or reloading scripts.
            EditorApplication.playModeStateChanged += _ => ClearPreviewData();
            AssemblyReloadEvents.beforeAssemblyReload += ClearPreviewData;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a preview GameObject for the given world-space mesh with per-vertex
        /// occlusion weights baked as vertex colors.
        /// </summary>
        internal static void SetPreviewData(string previewId, Mesh worldSpaceMesh, float[] simplificationWeights, bool enabled)
        {
            // Remove old entry for this ID if it exists.
            RemovePreviewData(previewId);

            if (worldSpaceMesh == null || worldSpaceMesh.vertexCount == 0)
                return;

            // Build vertex-colored mesh from the world-space mesh + weights.
            var previewMesh = BuildVertexColoredMesh(worldSpaceMesh, simplificationWeights);
            if (previewMesh == null)
                return;

            // Ensure preview root exists.
            var root = GetOrCreatePreviewRoot();

            // Create the preview GameObject.
            var go = new GameObject($"OcclusionPreview_{previewId}")
            {
                hideFlags = HideFlags.DontSave,
            };
            go.transform.SetParent(root.transform, false);
            // Mesh is already in world space, so identity transform.
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = Vector3.one;

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = previewMesh;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = GetVertexColorMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            go.SetActive(enabled);

            var entry = new PreviewEntry(previewId, go, filter, renderer, previewMesh);
            _entries[previewId] = entry;

            Debug.Log($"[Meshia] Created occlusion preview '{previewId}': {worldSpaceMesh.vertexCount} verts, {worldSpaceMesh.triangles.Length / 3} tris, weight range [{GetMinWeight(simplificationWeights):F2}, {GetMaxWeight(simplificationWeights):F2}]");
        }

        internal static void SetPreviewData(Mesh worldSpaceMesh, float[] simplificationWeights)
        {
            SetPreviewData("default", worldSpaceMesh, simplificationWeights, true);
        }

        internal static void AppendPreviewData(Mesh worldSpaceMesh, float[] simplificationWeights)
        {
            string id = $"legacy-{_entries.Count}";
            SetPreviewData(id, worldSpaceMesh, simplificationWeights, true);
        }

        /// <summary>
        /// Creates debug GameObjects for each occluder mesh, rendered as semi-transparent
        /// wireframe-like objects so the user can verify which clothing meshes participate
        /// in occlusion testing.
        /// </summary>
        internal static void ShowOccluderDebugMeshes(Mesh[] occluderWorldMeshes, string[] labels)
        {
            ClearOccluderDebugMeshes();

            if (occluderWorldMeshes == null || occluderWorldMeshes.Length == 0)
            {
                Debug.LogWarning("[Meshia] No occluder meshes to show.");
                return;
            }

            var root = GetOrCreateOccluderRoot();
            for (int i = 0; i < occluderWorldMeshes.Length; i++)
            {
                var srcMesh = occluderWorldMeshes[i];
                if (srcMesh == null || srcMesh.vertexCount == 0) continue;

                // Clone mesh so we can safely manage its lifetime.
                var debugMesh = new Mesh
                {
                    hideFlags = HideFlags.DontSave,
                    vertices = srcMesh.vertices,
                    triangles = srcMesh.triangles,
                };
                debugMesh.RecalculateNormals();
                debugMesh.RecalculateBounds();

                string label = i < labels.Length ? labels[i] : $"Occluder_{i}";
                var go = new GameObject($"Occluder_{label}")
                {
                    hideFlags = HideFlags.DontSave,
                };
                go.transform.SetParent(root.transform, false);
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                go.transform.localScale = Vector3.one;

                var filter = go.AddComponent<MeshFilter>();
                filter.sharedMesh = debugMesh;

                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = GetOccluderDebugMaterial();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                _occluderDebugObjects.Add(go);
            }

            int count = _occluderDebugObjects.Count;
            Debug.Log($"[Meshia] Showing {count} occluder debug meshes under '{OccluderRootName}'.");
        }

        internal static void ClearOccluderDebugMeshes()
        {
            foreach (var go in _occluderDebugObjects)
            {
                if (go != null)
                {
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                        UnityEngine.Object.DestroyImmediate(mf.sharedMesh);
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            _occluderDebugObjects.Clear();

            if (_occluderRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(_occluderRoot);
                _occluderRoot = null;
            }
        }

        internal static void ClearPreviewData()
        {
            foreach (var pair in _entries)
                DestroyEntry(pair.Value);
            _entries.Clear();

            if (_previewRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(_previewRoot);
                _previewRoot = null;
            }

            ClearOccluderDebugMeshes();
        }

        internal static void RemovePreviewData(string previewId)
        {
            if (_entries.TryGetValue(previewId, out var entry))
            {
                DestroyEntry(entry);
                _entries.Remove(previewId);
            }
            CleanupEmptyRoot();
        }

        internal static void RemovePreviewDataForPrefix(string previewPrefix)
        {
            var keysToRemove = new List<string>();
            foreach (var pair in _entries)
            {
                if (pair.Key.StartsWith(previewPrefix, StringComparison.Ordinal))
                    keysToRemove.Add(pair.Key);
            }

            foreach (var key in keysToRemove)
            {
                if (_entries.TryGetValue(key, out var entry))
                    DestroyEntry(entry);
                _entries.Remove(key);
            }
            CleanupEmptyRoot();
        }

        internal static bool HasPreviewData(string previewId)
        {
            return _entries.ContainsKey(previewId);
        }

        internal static bool HasPreviewDataForPrefix(string previewPrefix)
        {
            foreach (var key in _entries.Keys)
            {
                if (key.StartsWith(previewPrefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        internal static bool IsPreviewEnabled(string previewId)
        {
            return _entries.TryGetValue(previewId, out var entry) && entry.Go != null && entry.Go.activeSelf;
        }

        internal static void SetPreviewEnabled(string previewId, bool enabled)
        {
            if (_entries.TryGetValue(previewId, out var entry) && entry.Go != null)
                entry.Go.SetActive(enabled);
        }

        internal static void SetPreviewEnabledForPrefix(string previewPrefix, bool enabled)
        {
            foreach (var pair in _entries)
            {
                if (pair.Key.StartsWith(previewPrefix, StringComparison.Ordinal) && pair.Value.Go != null)
                    pair.Value.Go.SetActive(enabled);
            }
        }

        internal static void GetPreviewCountsForPrefix(string previewPrefix, out int total, out int enabled)
        {
            total = 0;
            enabled = 0;
            foreach (var pair in _entries)
            {
                if (!pair.Key.StartsWith(previewPrefix, StringComparison.Ordinal))
                    continue;
                total++;
                if (pair.Value.Go != null && pair.Value.Go.activeSelf)
                    enabled++;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Internal helpers
        // ─────────────────────────────────────────────────────────────────────

        private static float GetMinWeight(float[] weights)
        {
            if (weights == null || weights.Length == 0) return 0f;
            float min = float.MaxValue;
            foreach (var w in weights) if (w < min) min = w;
            return min;
        }

        private static float GetMaxWeight(float[] weights)
        {
            if (weights == null || weights.Length == 0) return 0f;
            float max = float.MinValue;
            foreach (var w in weights) if (w > max) max = w;
            return max;
        }

        private static GameObject GetOrCreatePreviewRoot()
        {
            if (_previewRoot != null)
                return _previewRoot;

            _previewRoot = new GameObject(PreviewRootName)
            {
                hideFlags = HideFlags.DontSave,
            };
            _previewRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            return _previewRoot;
        }

        private static GameObject GetOrCreateOccluderRoot()
        {
            if (_occluderRoot != null)
                return _occluderRoot;

            _occluderRoot = new GameObject(OccluderRootName)
            {
                hideFlags = HideFlags.DontSave,
            };
            _occluderRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            return _occluderRoot;
        }

        private static void CleanupEmptyRoot()
        {
            if (_entries.Count == 0 && _previewRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(_previewRoot);
                _previewRoot = null;
            }
        }

        private static void DestroyEntry(PreviewEntry entry)
        {
            if (entry.Go != null) UnityEngine.Object.DestroyImmediate(entry.Go);
            if (entry.Mesh != null) UnityEngine.Object.DestroyImmediate(entry.Mesh);
        }

        /// <summary>
        /// Creates a clone of the world-space mesh with per-vertex colors baked from
        /// the simplification weights.  The mesh is an independent copy; the caller
        /// is free to destroy the original <paramref name="worldMesh"/> after this call.
        /// </summary>
        private static Mesh? BuildVertexColoredMesh(Mesh worldMesh, float[] weights)
        {
            var vertices = worldMesh.vertices;
            var triangles = worldMesh.triangles;
            var normals = worldMesh.normals;
            int vertexCount = vertices.Length;

            if (vertexCount == 0 || triangles.Length == 0)
                return null;

            var colors = new Color[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                float w = i < weights.Length ? weights[i] : 1f;
                colors[i] = WeightToColor(w);
            }

            var mesh = new Mesh
            {
                hideFlags = HideFlags.DontSave,
                vertices = vertices,
                triangles = triangles,
                colors = colors,
            };

            if (normals != null && normals.Length == vertexCount)
                mesh.normals = normals;
            else
                mesh.RecalculateNormals();

            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Maps a simplification weight [1, 10] to a Viridis heatmap color.</summary>
        private static Color WeightToColor(float weight)
        {
            float t = Mathf.InverseLerp(1f, 10f, weight);
            t = Mathf.Pow(t, ContrastGamma);
            var palette = EditorPrefs.GetString("Meshia.Occlusion.ColorPalette", "Viridis");
            return palette switch
            {
                "BlueRed" => Color.Lerp(Color.blue, Color.red, t),
                "GreenRed" => Color.Lerp(Color.green, Color.red, t),
                _ => Viridis(t),
            };
        }

        /// <summary>Viridis approximation (colorblind-friendly)</summary>
        private static Color Viridis(float t)
        {
            t = Mathf.Clamp01(t);
            if (t < 0.25f)
            {
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

        /// <summary>
        /// Returns a shared vertex-color unlit material.
        /// Uses "Hidden/Internal-Colored" which renders vertex colors directly without
        /// lighting, ensuring the heatmap is clearly visible regardless of scene lighting.
        /// </summary>
        private static Material GetVertexColorMaterial()
        {
            if (_vertexColorMaterial != null)
                return _vertexColorMaterial;

            // Use the Internal-Colored shader which renders vertex colors directly.
            // This is more reliable than Particles/Standard Unlit for vertex color display.
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                // Fallback to Particles/Standard Unlit.
                shader = Shader.Find("Particles/Standard Unlit");
            }

            if (shader == null)
            {
                Debug.LogError("[Meshia] Could not find a vertex color shader for occlusion preview.");
                var fallbackShader = Shader.Find("Standard");
                if (fallbackShader == null)
                    fallbackShader = Shader.Find("UI/Default");
                if (fallbackShader == null)
                {
                    Debug.LogError("[Meshia] No usable shader found at all. Occlusion preview will not render.");
                    _vertexColorMaterial = new Material(Shader.Find("Sprites/Default")!) { hideFlags = HideFlags.DontSave };
                    return _vertexColorMaterial;
                }
                _vertexColorMaterial = new Material(fallbackShader) { hideFlags = HideFlags.DontSave };
                return _vertexColorMaterial;
            }

            _vertexColorMaterial = new Material(shader)
            {
                hideFlags = HideFlags.DontSave,
            };

            if (shader.name == "Hidden/Internal-Colored")
            {
                // Internal-Colored renders vertex colors directly.
                _vertexColorMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                _vertexColorMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                _vertexColorMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Back);
                _vertexColorMaterial.SetInt("_ZWrite", 1);
                _vertexColorMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            }
            else if (shader.name == "Particles/Standard Unlit")
            {
                // Enable vertex color stream.
                _vertexColorMaterial.SetFloat("_ColorMode", 1f); // Multiply
                _vertexColorMaterial.SetColor("_Color", Color.white);
                _vertexColorMaterial.SetFloat("_Mode", 0f); // Opaque
                _vertexColorMaterial.SetFloat("_SoftParticlesEnabled", 0f);
                _vertexColorMaterial.SetFloat("_CameraFadingEnabled", 0f);
            }

            return _vertexColorMaterial;
        }

        /// <summary>
        /// Returns a semi-transparent material for occluder debug visualization.
        /// </summary>
        private static Material GetOccluderDebugMaterial()
        {
            if (_occluderDebugMaterial != null)
                return _occluderDebugMaterial;

            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                shader = Shader.Find("Particles/Standard Unlit");
            if (shader == null)
                shader = Shader.Find("Standard");

            if (shader == null)
            {
                Debug.LogError("[Meshia] No shader found for occluder debug material.");
                _occluderDebugMaterial = new Material(Shader.Find("Sprites/Default")!) { hideFlags = HideFlags.DontSave };
                return _occluderDebugMaterial;
            }

            _occluderDebugMaterial = new Material(shader)
            {
                hideFlags = HideFlags.DontSave,
                color = new Color(0.3f, 0.6f, 1.0f, 0.25f), // semi-transparent blue
            };

            if (shader.name == "Hidden/Internal-Colored")
            {
                _occluderDebugMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _occluderDebugMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _occluderDebugMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Back);
                _occluderDebugMaterial.SetInt("_ZWrite", 0);
                _occluderDebugMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            }

            return _occluderDebugMaterial;
        }
    }
}

#endif
