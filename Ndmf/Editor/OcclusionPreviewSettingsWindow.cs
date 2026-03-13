#nullable enable
#if ENABLE_MODULAR_AVATAR

using UnityEditor;
using UnityEngine;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    internal class OcclusionPreviewSettingsWindow : EditorWindow
    {
        private const string PrefKeyMaxTrianglesPerMesh = "Meshia.Occlusion.MaxTrianglesPerMesh";
        private const string PrefKeyMaxTrianglesTotal = "Meshia.Occlusion.MaxTrianglesTotal";
        private const int PrefDefaultMaxTrianglesPerMesh = 1000;
        private const int PrefDefaultMaxTrianglesTotal = 1000;

        private int _perMesh;
        private int _total;

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
            _perMesh = EditorPrefs.GetInt(PrefKeyMaxTrianglesPerMesh, PrefDefaultMaxTrianglesPerMesh);
            _total = EditorPrefs.GetInt(PrefKeyMaxTrianglesTotal, PrefDefaultMaxTrianglesTotal);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Occlusion Preview Performance Settings", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Adjust sampling caps for the occlusion preview. Higher values increase fidelity but may reduce editor responsiveness.", MessageType.Info);

            EditorGUI.BeginChangeCheck();
            _perMesh = EditorGUILayout.IntSlider(new GUIContent("Max Triangles Per Mesh"), _perMesh, 100, 20000);
            _total = EditorGUILayout.IntSlider(new GUIContent("Max Triangles Total"), _total, 100, 20000);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetInt(PrefKeyMaxTrianglesPerMesh, Mathf.Max(1, _perMesh));
                EditorPrefs.SetInt(PrefKeyMaxTrianglesTotal, Mathf.Max(1, _total));
                SceneView.RepaintAll();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to Defaults"))
            {
                _perMesh = PrefDefaultMaxTrianglesPerMesh;
                _total = PrefDefaultMaxTrianglesTotal;
                EditorPrefs.SetInt(PrefKeyMaxTrianglesPerMesh, _perMesh);
                EditorPrefs.SetInt(PrefKeyMaxTrianglesTotal, _total);
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Close"))
            {
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}

#endif
