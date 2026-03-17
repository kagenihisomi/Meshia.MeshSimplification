#nullable enable
#if ENABLE_MODULAR_AVATAR

using UnityEditor;
using UnityEngine;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    internal class OcclusionPreviewSettingsWindow : EditorWindow
    {
        private const string PrefKeyMaxVerticesPerMesh = "Meshia.Occlusion.MaxVerticesPerMesh";
        private const string PrefKeyMaxVerticesTotal = "Meshia.Occlusion.MaxVerticesTotal";
        private const string LegacyPrefKeyMaxTrianglesPerMesh = "Meshia.Occlusion.MaxTrianglesPerMesh";
        private const string LegacyPrefKeyMaxTrianglesTotal = "Meshia.Occlusion.MaxTrianglesTotal";
        private const int PrefDefaultMaxVerticesPerMesh = 20000;
        private const int PrefDefaultMaxVerticesTotal = 80000;
        private const string PrefKeyColorPalette = "Meshia.Occlusion.ColorPalette";
        private const string PrefDefaultColorPalette = "Viridis"; // colorblind-friendly default

        private int _perMesh;
        private int _total;
        private string _palette = PrefDefaultColorPalette;

        [MenuItem("Window/Meshia/Occlusion Preview Settings")]
        public static void ShowWindow()
        {
            var wnd = GetWindow<OcclusionPreviewSettingsWindow>(true, "Occlusion Preview Settings");
            wnd.minSize = new Vector2(340, 120);
            wnd.LoadPrefs();
            wnd.Show();
        }

        private void LoadPrefs()
        {
            _perMesh = GetIntWithLegacyFallback(PrefKeyMaxVerticesPerMesh, LegacyPrefKeyMaxTrianglesPerMesh, PrefDefaultMaxVerticesPerMesh);
            _total = GetIntWithLegacyFallback(PrefKeyMaxVerticesTotal, LegacyPrefKeyMaxTrianglesTotal, PrefDefaultMaxVerticesTotal);
            _palette = EditorPrefs.GetString(PrefKeyColorPalette, PrefDefaultColorPalette);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Occlusion Preview Performance Settings", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Adjust sampled vertex caps for the occlusion preview. Higher values increase fidelity but may reduce editor responsiveness.", MessageType.Info);

            EditorGUI.BeginChangeCheck();
            _perMesh = EditorGUILayout.IntSlider(new GUIContent("Max Vertices Per Mesh"), _perMesh, 1000, 100000);
            _total = EditorGUILayout.IntSlider(new GUIContent("Max Vertices Total"), _total, 2000, 300000);
            _palette = EditorGUILayout.Popup(new GUIContent("Color Palette"), PaletteNameToIndex(_palette), new[] { "Green-Red", "Blue-Red", "Viridis" }) switch
            {
                0 => "GreenRed",
                1 => "BlueRed",
                _ => "Viridis",
            };
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetInt(PrefKeyMaxVerticesPerMesh, Mathf.Max(1, _perMesh));
                EditorPrefs.SetInt(PrefKeyMaxVerticesTotal, Mathf.Max(1, _total));
                EditorPrefs.SetString(PrefKeyColorPalette, _palette ?? PrefDefaultColorPalette);
                SceneView.RepaintAll();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to Defaults"))
            {
                _perMesh = PrefDefaultMaxVerticesPerMesh;
                _total = PrefDefaultMaxVerticesTotal;
                EditorPrefs.SetInt(PrefKeyMaxVerticesPerMesh, _perMesh);
                EditorPrefs.SetInt(PrefKeyMaxVerticesTotal, _total);
                EditorPrefs.SetString(PrefKeyColorPalette, PrefDefaultColorPalette);
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Close"))
            {
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }

        private static int PaletteNameToIndex(string name)
        {
            return name switch
            {
                "GreenRed" => 0,
                "BlueRed" => 1,
                "Viridis" => 2,
                _ => 2,
            };
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
