#nullable enable
#if ENABLE_MODULAR_AVATAR

using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Meshia.MeshSimplification.Ndmf.Editor.Preview;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    [CustomEditor(typeof(MeshiaCascadingAvatarMeshSimplifier))]
    internal class MeshiaCascadingAvatarMeshSimplifierEditor : UnityEditor.Editor
    {
        [SerializeField] VisualTreeAsset editorVisualTreeAsset = null!;
        [SerializeField] VisualTreeAsset entryEditorVisualTreeAsset = null!;
        private MeshiaCascadingAvatarMeshSimplifier Target => (MeshiaCascadingAvatarMeshSimplifier)target;
        private bool _isAdjustingQuality;

        private SerializedProperty AutoAdjustEnabledProperty => serializedObject.FindProperty(nameof(MeshiaCascadingAvatarMeshSimplifier.AutoAdjustEnabled));
        private SerializedProperty TargetTriangleCountProperty => serializedObject.FindProperty(nameof(MeshiaCascadingAvatarMeshSimplifier.TargetTriangleCount));
        private SerializedProperty EntriesProperty => serializedObject.FindProperty(nameof(MeshiaCascadingAvatarMeshSimplifier.Entries));


        [MenuItem("GameObject/Meshia Mesh Simplification/Meshia Cascading Avatar Mesh Simplifier", false, 0)]
        static void AddCascadingAvatarMeshSimplifier()
        {
            var go = new GameObject("Meshia Cascading Avatar Mesh Simplifier");
            go.AddComponent<MeshiaCascadingAvatarMeshSimplifier>();
            go.transform.parent = Selection.activeGameObject.transform;
            Undo.RegisterCreatedObjectUndo(go, "Create Meshia Cascading Avatar Mesh Simplifier");
        }
        private void OnEnable()
        {
            RefreshEntries();
        }

        private void RefreshEntries()
        {
            if (Target.transform.parent == null)
            {
                return;
            }
            Undo.RecordObject(Target, "Get entries");
            try
            {
                Target.RefreshEntries();
            }
            catch (InvalidOperationException e)
            {
                Debug.LogException(e, target);
                return;
            }

            serializedObject.Update();


        }

        private void RefreshEntriesAndSync(bool clearCaches)
        {
            Undo.RecordObject(Target, "Refresh Entries");
            if (clearCaches)
            {
                Target.Entries.ForEach(e => e.ClearCache());
            }

            Target.RefreshEntries();
            serializedObject.Update();
        }

        private void ApplyDeterministicAdjustQuality(int fixedIndex = -1)
        {
            if (_isAdjustingQuality) return;

            _isAdjustingQuality = true;
            try
            {
                // Sync any pending serializedObject changes to target before adjustment
                serializedObject.ApplyModifiedProperties();
                AdjustQuality(fixedIndex);
                // AdjustQuality calls serializedObject.Update() at the end
            }
            finally
            {
                _isAdjustingQuality = false;
            }
        }

        private static bool IsRendererActiveAndEnabled(Renderer? renderer)
        {
            return renderer != null && renderer.gameObject.activeInHierarchy && renderer.enabled;
        }

        private CostumeGroup? FindCostumeGroup(string groupName)
        {
            return Target.CostumeGroups.FirstOrDefault(group => group.GroupName == groupName);
        }

        private void RefreshEntriesListView(ListView entriesListView)
        {
            serializedObject.Update();
            entriesListView.RefreshItems();
        }

        public override VisualElement CreateInspectorGUI()
        {
            VisualElement root = new();
            editorVisualTreeAsset.CloneTree(root);

            serializedObject.Update();

            root.Bind(serializedObject);
            var attachedToRootWarning = root.Q<HelpBox>("AttachedToRootWarning");
            var mainElement = root.Q<VisualElement>("MainElement");
            var targetTriangleCountField = root.Q<IntegerField>("TargetTriangleCountField");
            var targetTriangleCountPresetDropdownField = root.Q<DropdownField>("TargetTriangleCountPresetDropdownField");
            var adjustButton = root.Q<Button>("AdjustButton");
            var autoAdjustEnabledToggle = root.Q<Toggle>("AutoAdjustEnabledToggle");

            var minimumThresholdField = new IntegerField("Minimum Triangle Threshold");
            minimumThresholdField.bindingPath = nameof(MeshiaCascadingAvatarMeshSimplifier.MinimumTriangleThreshold);
            minimumThresholdField.isDelayed = true;
            minimumThresholdField.RegisterValueChangedCallback(changeEvent =>
            {
                if (AutoAdjustEnabledProperty.boolValue)
                {
                    ApplyDeterministicAdjustQuality();
                }
            });
            autoAdjustEnabledToggle.parent.Add(minimumThresholdField);

            var triangleCountLabel = root.Q<IMGUIContainer>("TriangleCountLabel");

            var removeInvalidEntriesButton = root.Q<Button>("RemoveInvalidEntriesButton");
            var resetButton = root.Q<Button>("ResetButton");

            var refreshButton = new Button(() =>
            {
                RefreshEntriesAndSync(clearCaches: true);
            })
            { text = "Refresh Entries" };
            removeInvalidEntriesButton.parent.Insert(0, refreshButton);

            var entriesListView = root.Q<ListView>("EntriesListView");
            var ndmfPreviewToggle = root.Q<Toggle>("NdmfPreviewToggle");

            // Add costume groups UI
            var costumeGroupsFoldout = new Foldout { text = "Costume Groups (Per-Costume Target Triangle Counts)", value = true };
            var costumeGroupsContainer = new IMGUIContainer(() =>
            {
                var target = Target;
                EditorGUILayout.LabelField("Configure triangle count targets per costume:", EditorStyles.helpBox);

                // Calculate groups in one pass to avoid lag
                var currentByGroup = new Dictionary<string, int>();
                var maxByGroup = new Dictionary<string, int>();
                var optimizeDisabledByGroup = new Dictionary<string, bool>();
                var optimizeEnabledByGroup = new Dictionary<string, bool>();
                foreach (var cg in target.CostumeGroups)
                {
                    currentByGroup[cg.GroupName] = 0; maxByGroup[cg.GroupName] = 0;
                    optimizeDisabledByGroup[cg.GroupName] = cg.OptimizeDisabledGameObjects;
                    optimizeEnabledByGroup[cg.GroupName] = cg.OptimizeGroupEnabled;
                }
                foreach (var entry in target.Entries)
                {
                    if (!entry.IsValid(target)) continue;

                    bool groupOptimizeEnabled = optimizeEnabledByGroup.TryGetValue(entry.CostumeGroup, out var optEnabled) && optEnabled;
                    var r = entry.GetTargetRenderer(target);
                    bool isActive = IsRendererActiveAndEnabled(r);
                    bool optimizeDisabled = optimizeDisabledByGroup.TryGetValue(entry.CostumeGroup, out var optDis) ? optDis : false;

                    if (!optimizeDisabled && !isActive) continue;

                    // For disabled groups, count original triangles; for enabled groups, count simplified
                    if (groupOptimizeEnabled)
                    {
                        if (TryGetSimplifiedTriangleCount(entry, true, out var c))
                        {
                            if (currentByGroup.ContainsKey(entry.CostumeGroup)) currentByGroup[entry.CostumeGroup] += c;
                        }
                    }
                    else
                    {
                        if (TryGetOriginalTriangleCount(entry, false, out var c))
                        {
                            if (currentByGroup.ContainsKey(entry.CostumeGroup)) currentByGroup[entry.CostumeGroup] += c;
                        }
                    }

                    if (TryGetOriginalTriangleCount(entry, false, out var m))
                    {
                        if (maxByGroup.ContainsKey(entry.CostumeGroup)) maxByGroup[entry.CostumeGroup] += m;
                    }
                }

                foreach (var costumeGroup in target.CostumeGroups)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(costumeGroup.GroupName, GUILayout.Width(130));

                    EditorGUI.BeginChangeCheck();
                    var newOptimizeEnabled = EditorGUILayout.ToggleLeft("Optimize", costumeGroup.OptimizeGroupEnabled, GUILayout.Width(80));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(target, "Change Costume Group Optimize State");
                        costumeGroup.OptimizeGroupEnabled = newOptimizeEnabled;
                        EditorUtility.SetDirty(target);
                        serializedObject.Update();

                        if (!newOptimizeEnabled)
                        {
                            // When disabling optimization, reset all entries to original triangle counts
                            for (int i = 0; i < target.Entries.Count; i++)
                            {
                                var entry = target.Entries[i];
                                if (entry.CostumeGroup == costumeGroup.GroupName && TryGetOriginalTriangleCount(entry, false, out var originalCount))
                                {
                                    entry.TargetTriangleCount = originalCount;
                                }
                            }
                            serializedObject.Update();
                        }
                        else if (AutoAdjustEnabledProperty.boolValue)
                        {
                            // When enabling, run adjustment to redistribute budget
                            ApplyDeterministicAdjustQuality();
                        }

                        RefreshEntriesListView(entriesListView);
                    }

                    EditorGUI.BeginChangeCheck();
                    var optimizeDisabled = EditorGUILayout.ToggleLeft("Apply To Inactive", costumeGroup.OptimizeDisabledGameObjects, GUILayout.Width(130));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(target, "Change Costume Group Optimize Disabled");
                        costumeGroup.OptimizeDisabledGameObjects = optimizeDisabled;
                        EditorUtility.SetDirty(target);
                        serializedObject.Update();
                        RefreshEntriesListView(entriesListView);
                        // Apply To Inactive changes which renderers are included, so always re-adjust
                        if (costumeGroup.OptimizeGroupEnabled && AutoAdjustEnabledProperty.boolValue)
                        {
                            ApplyDeterministicAdjustQuality();
                            RefreshEntriesListView(entriesListView);
                        }
                    }

                    if (GUILayout.Button("Ref", GUILayout.Width(35)))
                    {
                        foreach (var entry in target.Entries)
                        {
                            if (entry.CostumeGroup == costumeGroup.GroupName)
                            {
                                entry.ClearCache();
                            }
                        }
                        RefreshEntriesAndSync(clearCaches: false);
                        RefreshEntriesListView(entriesListView);
                        // Entries list might have resized, UIElements sometimes needs a kick for list changes but bindings should eventually sync.
                    }

                    if (GUILayout.Button("Reset", GUILayout.Width(45)))
                    {
                        var entriesProperty = EntriesProperty;
                        for (int i = 0; i < target.Entries.Count; i++)
                        {
                            if (target.Entries[i].CostumeGroup == costumeGroup.GroupName)
                            {
                                entriesProperty.GetArrayElementAtIndex(i).FindPropertyRelative(nameof(MeshiaCascadingAvatarMeshSimplifierRendererEntry.Enabled)).boolValue = true;
                                entriesProperty.GetArrayElementAtIndex(i).FindPropertyRelative(nameof(MeshiaCascadingAvatarMeshSimplifierRendererEntry.Fixed)).boolValue = false;
                            }
                        }
                        serializedObject.ApplyModifiedProperties();
                        RefreshEntriesListView(entriesListView);
                        ApplyDeterministicAdjustQuality();
                        RefreshEntriesListView(entriesListView);
                    }

                    EditorGUI.BeginChangeCheck();
                    var newValue = EditorGUILayout.IntField(costumeGroup.TargetTriangleCount, GUILayout.Width(70));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(target, "Change Costume Group Target");
                        costumeGroup.TargetTriangleCount = newValue;
                        if (AutoAdjustEnabledProperty.boolValue)
                        {
                            ApplyDeterministicAdjustQuality();
                            RefreshEntriesListView(entriesListView);
                        }
                    }

                    var current = currentByGroup.TryGetValue(costumeGroup.GroupName, out var cc) ? cc : 0;
                    var max = maxByGroup.TryGetValue(costumeGroup.GroupName, out var mm) ? mm : 0;
                    EditorGUILayout.LabelField($"({current}/{max})", GUILayout.Width(100));

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.Space();
                if (GUILayout.Button("Set All From Preset"))
                {
                    var menu = new GenericMenu();
                    foreach (var preset in TargetTriangleCountPresetNameToValue)
                    {
                        menu.AddItem(new GUIContent(preset.Key), false, () =>
                        {
                            Undo.RecordObject(target, "Set All Costume Targets From Preset");
                            foreach (var costumeGroup in target.CostumeGroups)
                            {
                                costumeGroup.TargetTriangleCount = preset.Value;
                            }
                            if (AutoAdjustEnabledProperty.boolValue)
                            {
                                ApplyDeterministicAdjustQuality();
                            }
                        });
                    }
                    menu.ShowAsContext();
                }
            });
            costumeGroupsFoldout.Add(costumeGroupsContainer);

            // Insert after the target triangle count field's parent group
            var targetCountGroup = targetTriangleCountField.parent;
            var insertIndex = mainElement.IndexOf(targetCountGroup) + 1;
            mainElement.Insert(insertIndex, costumeGroupsFoldout);

            attachedToRootWarning.style.display = Target.transform.parent == null ? DisplayStyle.Flex : DisplayStyle.None;


            targetTriangleCountField.RegisterValueChangedCallback(changeEvent =>
            {
                if (!TargetTriangleCountPresetValueToName.TryGetValue(changeEvent.newValue, out var name))
                {
                    name = "Custom";
                }
                targetTriangleCountPresetDropdownField.SetValueWithoutNotify(name);
                if (AutoAdjustEnabledProperty.boolValue)
                {
                    ApplyDeterministicAdjustQuality();
                }
            });

            targetTriangleCountPresetDropdownField.choices = TargetTriangleCountPresetNameToValue.Keys.ToList();
            targetTriangleCountPresetDropdownField.RegisterValueChangedCallback(changeEvent =>
            {
                if (TargetTriangleCountPresetNameToValue.TryGetValue(changeEvent.newValue, out var value))
                {
                    TargetTriangleCountProperty.intValue = value;
                    serializedObject.ApplyModifiedProperties();
                }

            });

            adjustButton.clicked += () =>
            {
                ApplyDeterministicAdjustQuality();
            };

            autoAdjustEnabledToggle.RegisterValueChangedCallback(changeEvent =>
            {
                var autoAdjustEnabled = AutoAdjustEnabledProperty.boolValue;

                if (autoAdjustEnabled)
                {
                    ApplyDeterministicAdjustQuality();
                }
            });

            // --- Occlusion-Weighted Simplification UI ---
            var occlusionWeightedToggle = root.Q<Toggle>("OcclusionWeightedToggle");
            var occlusionWeightStrengthSlider = root.Q<Slider>("OcclusionWeightStrengthSlider");
            var previewOcclusionWeightsButton = root.Q<Button>("PreviewOcclusionWeightsButton");

            if (occlusionWeightedToggle != null && occlusionWeightStrengthSlider != null && previewOcclusionWeightsButton != null)
            {
                // Set initial visibility based on current serialized value
                bool initialOcclusionEnabled = Target.UseOcclusionWeightedSimplification;
                occlusionWeightStrengthSlider.style.display = initialOcclusionEnabled ? DisplayStyle.Flex : DisplayStyle.None;
                previewOcclusionWeightsButton.style.display = initialOcclusionEnabled ? DisplayStyle.Flex : DisplayStyle.None;

                occlusionWeightedToggle.RegisterValueChangedCallback(evt =>
                {
                    bool enabled = evt.newValue;
                    occlusionWeightStrengthSlider.style.display = enabled ? DisplayStyle.Flex : DisplayStyle.None;
                    previewOcclusionWeightsButton.style.display = enabled ? DisplayStyle.Flex : DisplayStyle.None;
                    if (!enabled)
                        OcclusionWeightGizmoDrawer.ClearPreviewData();
                });

                previewOcclusionWeightsButton.clicked += () => ComputeAndPreviewOcclusionWeights();
            }


            triangleCountLabel.onGUIHandler = () =>
            {
                var target = Target;
                // Display total - use optimized single pass loop
                int totalCurrent = 0;
                int totalSum = 0;

                var optimizeDisabledByGroup = new Dictionary<string, bool>();
                var optimizeEnabledByGroup = new Dictionary<string, bool>();
                foreach (var cg in target.CostumeGroups)
                {
                    optimizeDisabledByGroup[cg.GroupName] = cg.OptimizeDisabledGameObjects;
                    optimizeEnabledByGroup[cg.GroupName] = cg.OptimizeGroupEnabled;
                }

                foreach (var entry in target.Entries)
                {
                    if (entry.IsValid(target))
                    {
                        bool groupOptimizeEnabled = optimizeEnabledByGroup.TryGetValue(entry.CostumeGroup, out var optEnabled) && optEnabled;
                        var r = entry.GetTargetRenderer(target);
                        bool isActive = IsRendererActiveAndEnabled(r);
                        bool optimizeDisabled = optimizeDisabledByGroup.TryGetValue(entry.CostumeGroup, out var optDis) ? optDis : false;

                        if (!optimizeDisabled && !isActive) continue;

                        // For disabled groups, count original triangles; for enabled groups, count simplified
                        if (groupOptimizeEnabled)
                        {
                            if (TryGetSimplifiedTriangleCount(entry, true, out var c)) totalCurrent += c;
                        }
                        else
                        {
                            if (TryGetOriginalTriangleCount(entry, false, out var c)) totalCurrent += c;
                        }

                        if (TryGetOriginalTriangleCount(entry, false, out var m)) totalSum += m;
                    }
                }

                var totalLabel = $"Total Current: {totalCurrent} / {totalSum}";

                var isOverflow = TargetTriangleCountProperty.intValue < totalCurrent;
                if (isOverflow) EditorGUILayout.LabelField(totalLabel + " - Overflow!", GUIStyleHelper.RedStyle, GUILayout.Width(7f * (totalLabel.Length + 12)));
                else EditorGUILayout.LabelField(totalLabel, EditorStyles.boldLabel);
            };
            removeInvalidEntriesButton.clicked += () =>
            {
                var target = Target;
                var entries = target.Entries;

                Undo.RecordObject(target, "Remove Invalid Entries");
                for (int i = 0; i < entries.Count;)
                {
                    var entry = entries[i];
                    if (entry.IsValid(target))
                    {
                        i++;
                    }
                    else
                    {
                        entries.RemoveAt(i);
                    }

                }
                serializedObject.Update();
            };
            resetButton.clicked += () =>
            {
                var originalTriangleCount = GetTotalOriginalTriangleCount();

                var quality = originalTriangleCount > 0 ? TargetTriangleCountProperty.intValue / (float)originalTriangleCount : 1f;

                var entriesProperty = EntriesProperty;
                var arraySize = entriesProperty.arraySize;
                for (int i = 0; i < arraySize; i++)
                {
                    var entryProperty = entriesProperty.GetArrayElementAtIndex(i);
                    entryProperty.FindPropertyRelative(nameof(MeshiaCascadingAvatarMeshSimplifierRendererEntry.Enabled)).boolValue = true;
                    entryProperty.FindPropertyRelative(nameof(MeshiaCascadingAvatarMeshSimplifierRendererEntry.Fixed)).boolValue = false;
                }

                serializedObject.ApplyModifiedProperties();
                serializedObject.Update();

                SetQualityAll(quality);
                serializedObject.ApplyModifiedProperties();
            };
            entriesListView.bindItem = (itemElement, index) =>
            {
                var entry = Target.Entries[index];
                var entryProperty = EntriesProperty.GetArrayElementAtIndex(index);
                var itemRoot = (TemplateContainer)itemElement;
                var targetObjectField = itemRoot.Q<ObjectField>("TargetObjectField");
                var targetPathField = itemRoot.Q<TextField>("TargetPathField");
                var targetTriangleCountSlider = itemRoot.Q<SliderInt>("TargetTriangleCountSlider");
                var targetTriangleCountField = itemRoot.Q<IntegerField>("TargetTriangleCountField");
                var originalTriangleCountField = itemRoot.Q<IntegerField>("OriginalTriangleCountField");
                var unknownOriginalTriangleCountField = itemRoot.Q<TextField>("UnknownOriginalTriangleCountField");
                var preserveBorderEdgesBonesFoldout = itemRoot.Q<Foldout>("PreserveBorderEdgesBonesFoldout");
                itemRoot.BindProperty(entryProperty);
                itemRoot.userData = index;

                // Add costume group label if it doesn't exist
                var costumeGroupLabel = itemRoot.Q<Label>("CostumeGroupLabel");
                if (costumeGroupLabel == null)
                {
                    costumeGroupLabel = new Label();
                    costumeGroupLabel.name = "CostumeGroupLabel";
                    costumeGroupLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                    costumeGroupLabel.style.color = new StyleColor(new Color(0.7f, 0.7f, 1f));
                    costumeGroupLabel.style.marginTop = 10;
                    costumeGroupLabel.style.marginBottom = 5;
                    costumeGroupLabel.style.fontSize = 14;
                    itemRoot.Insert(0, costumeGroupLabel);
                }

                var isFirstInGroup = index == 0 || Target.Entries[index - 1].CostumeGroup != entry.CostumeGroup;
                if (isFirstInGroup)
                {
                    costumeGroupLabel.style.display = DisplayStyle.Flex;
                    costumeGroupLabel.text = $"⬇ {entry.CostumeGroup}";
                }
                else
                {
                    costumeGroupLabel.style.display = DisplayStyle.None;
                }

                var targetRenderer = entry.GetTargetRenderer(Target);
                if (targetRenderer != null)
                {
                    targetObjectField.style.display = DisplayStyle.Flex;
                    targetObjectField.value = targetRenderer;
                    targetObjectField.EnableInClassList("editor-only", MeshiaCascadingAvatarMeshSimplifierRendererEntry.IsEditorOnlyInHierarchy(targetRenderer.gameObject));

                    targetPathField.style.display = DisplayStyle.None;
                }
                else
                {
                    targetPathField.style.display = DisplayStyle.Flex;
                    targetPathField.value = entry.RendererObjectReference.referencePath;
                    targetObjectField.style.display = DisplayStyle.None;
                }


                if (TryGetOriginalTriangleCount(entry, true, out var originalTriangleCount))
                {
                    targetTriangleCountSlider.highValue = originalTriangleCount;

                    originalTriangleCountField.style.display = DisplayStyle.Flex;
                    originalTriangleCountField.value = originalTriangleCount;

                    unknownOriginalTriangleCountField.style.display = DisplayStyle.None;
                }
                else
                {
                    targetTriangleCountSlider.visible = false;

                    unknownOriginalTriangleCountField.style.display = DisplayStyle.Flex;


                    originalTriangleCountField.style.display = DisplayStyle.None;

                }

                var humanBodyBoneIndex = 0;
                var preserveBorderEdgesBonesProperty = EntriesProperty.GetArrayElementAtIndex(index).FindPropertyRelative(nameof(MeshiaCascadingAvatarMeshSimplifierRendererEntry.PreserveBorderEdgesBones));
                var preserveBorderEdgesBones = preserveBorderEdgesBonesProperty.ulongValue;
                foreach (var preserveBorderEdgesBoneToggle in preserveBorderEdgesBonesFoldout.Children().OfType<Toggle>())
                {
                    preserveBorderEdgesBoneToggle.value = (preserveBorderEdgesBones & (1ul << humanBodyBoneIndex)) != 0ul;

                    humanBodyBoneIndex++;
                }

                var group = FindCostumeGroup(entry.CostumeGroup);
                bool groupOptimizeEnabled = group?.OptimizeGroupEnabled ?? true;
                bool applyToInactive = group?.OptimizeDisabledGameObjects ?? false;
                bool shouldDisableRow = !groupOptimizeEnabled || (!applyToInactive && targetRenderer != null && !IsRendererActiveAndEnabled(targetRenderer));
                itemRoot.SetEnabled(!shouldDisableRow);
            };


            entriesListView.makeItem = () =>
            {
                var itemRoot = entryEditorVisualTreeAsset.CloneTree();
                var enabledToggle = itemRoot.Q<Toggle>("EnabledToggle");
                var targetObjectField = itemRoot.Q<ObjectField>("TargetObjectField");
                var targetTriangleCountSlider = itemRoot.Q<SliderInt>("TargetTriangleCountSlider");
                var targetTriangleCountField = itemRoot.Q<IntegerField>("TargetTriangleCountField");
                var triangleCountDivider = itemRoot.Q<Label>("TriangleCountDivider");
                var optionsToggle = itemRoot.Q<Toggle>("OptionsToggle");
                var optionsField = itemRoot.Q<PropertyField>("OptionsField");
                var preserveBorderEdgesBonesFoldout = itemRoot.Q<Foldout>("PreserveBorderEdgesBonesFoldout");
                enabledToggle.RegisterValueChangedCallback(changeEvent =>
                {
                    var enabled = changeEvent.newValue;

                    targetTriangleCountSlider.visible = enabled;
                    targetTriangleCountField.visible = enabled;
                    triangleCountDivider.visible = enabled;


                    if (AutoAdjustEnabledProperty.boolValue)
                    {
                        ApplyDeterministicAdjustQuality();
                    }
                });

                targetObjectField.SetEnabled(false);

                targetTriangleCountSlider.RegisterValueChangedCallback(changeEvent =>
                {
                    if (itemRoot.userData is int itemIndex && AutoAdjustEnabledProperty.boolValue)
                    {
                        ApplyDeterministicAdjustQuality(itemIndex);
                    }
                });

                optionsToggle.RegisterValueChangedCallback(changeEvent =>
                {
                    optionsField.style.display = preserveBorderEdgesBonesFoldout.style.display = changeEvent.newValue ? DisplayStyle.Flex : DisplayStyle.None;
                });



                for (HumanBodyBones bone = 0; bone < HumanBodyBones.LastBone; bone++)
                {
                    var humanBodyBoneIndex = (int)bone;
                    Toggle preserveBorderEdgesBoneToggle = new(bone.ToString());
                    preserveBorderEdgesBoneToggle.RegisterValueChangedCallback(changeEvent =>
                    {
                        if (itemRoot.userData is int itemIndex)
                        {
                            var preserveBorderEdgesBonesProperty = EntriesProperty.GetArrayElementAtIndex(itemIndex).FindPropertyRelative(nameof(MeshiaCascadingAvatarMeshSimplifierRendererEntry.PreserveBorderEdgesBones));
                            serializedObject.Update();
                            var currentMask = preserveBorderEdgesBonesProperty.ulongValue;
                            if (changeEvent.newValue)
                            {
                                currentMask |= (1ul << humanBodyBoneIndex);
                            }
                            else
                            {
                                currentMask &= ~(1ul << humanBodyBoneIndex);
                            }
                            preserveBorderEdgesBonesProperty.ulongValue = currentMask;

                            serializedObject.ApplyModifiedProperties();
                        }

                    });
                    preserveBorderEdgesBonesFoldout.Add(preserveBorderEdgesBoneToggle);
                }

                return itemRoot;
            };

            ndmfPreviewToggle.SetValueWithoutNotify(MeshiaCascadingAvatarMeshSimplifierPreview.PreviewControlNode.IsEnabled.Value);
            ndmfPreviewToggle.RegisterValueChangedCallback(changeEvent =>
            {
                MeshiaCascadingAvatarMeshSimplifierPreview.PreviewControlNode.IsEnabled.Value = changeEvent.newValue;
            });

            Action<bool> onNdmfPreviewEnabledChanged = (newValue) =>
            {
                ndmfPreviewToggle.SetValueWithoutNotify(newValue);
            };
            MeshiaCascadingAvatarMeshSimplifierPreview.PreviewControlNode.IsEnabled.OnChange += onNdmfPreviewEnabledChanged;
            ndmfPreviewToggle.RegisterCallback<DetachFromPanelEvent>(detachFromPanelEvent =>
            {
                MeshiaCascadingAvatarMeshSimplifierPreview.PreviewControlNode.IsEnabled.OnChange -= onNdmfPreviewEnabledChanged;
            });


            return root;
        }

        static Dictionary<string, int> TargetTriangleCountPresetNameToValue { get; } = new()
        {
            ["PC-Poor-Medium-Good"] = 70000,
            ["PC-Excellent"] = 32000,
            ["Mobile-Poor"] = 20000,
            ["Mobile-Medium"] = 15000,
            ["Mobile-Good"] = 10000,
            ["Mobile-Excellent"] = 7500,
        };

        static Dictionary<int, string> TargetTriangleCountPresetValueToName { get; } = TargetTriangleCountPresetNameToValue.ToDictionary(keyValue => keyValue.Value, keyValue => keyValue.Key);


        private int GetTotalSimplifiedTriangleCount(bool usePreview)
        {
            var totalCount = 0;
            var target = Target;
            var optimizeDisabledByGroup = new Dictionary<string, bool>();
            var optimizeEnabledByGroup = new Dictionary<string, bool>();
            foreach (var cg in target.CostumeGroups) optimizeDisabledByGroup[cg.GroupName] = cg.OptimizeDisabledGameObjects;
            foreach (var cg in target.CostumeGroups) optimizeEnabledByGroup[cg.GroupName] = cg.OptimizeGroupEnabled;

            foreach (var entry in target.Entries)
            {
                if (entry.IsValid(target))
                {
                    bool groupOptimizeEnabled = optimizeEnabledByGroup.TryGetValue(entry.CostumeGroup, out var optEnabled) && optEnabled;
                    bool optimizeDisabled = optimizeDisabledByGroup.TryGetValue(entry.CostumeGroup, out var optDis) ? optDis : false;
                    var r = entry.GetTargetRenderer(target);
                    bool isActive = IsRendererActiveAndEnabled(r);
                    if (!optimizeDisabled && !isActive) continue;

                    // For disabled groups, count original triangles; for enabled groups, count simplified
                    if (groupOptimizeEnabled)
                    {
                        totalCount += TryGetSimplifiedTriangleCount(entry, usePreview, out var triangleCount) ? triangleCount : 0;
                    }
                    else
                    {
                        totalCount += TryGetOriginalTriangleCount(entry, false, out var triangleCount) ? triangleCount : 0;
                    }
                }
            }
            return totalCount;
        }

        private int GetTotalOriginalTriangleCount()
        {
            var totalCount = 0;
            var target = Target;
            var optimizeDisabledByGroup = new Dictionary<string, bool>();
            var optimizeEnabledByGroup = new Dictionary<string, bool>();
            foreach (var cg in target.CostumeGroups) optimizeDisabledByGroup[cg.GroupName] = cg.OptimizeDisabledGameObjects;
            foreach (var cg in target.CostumeGroups) optimizeEnabledByGroup[cg.GroupName] = cg.OptimizeGroupEnabled;

            foreach (var entry in target.Entries)
            {
                if (entry.IsValid(target))
                {
                    bool groupOptimizeEnabled = optimizeEnabledByGroup.TryGetValue(entry.CostumeGroup, out var optEnabled) && optEnabled;
                    bool optimizeDisabled = optimizeDisabledByGroup.TryGetValue(entry.CostumeGroup, out var optDis) ? optDis : false;
                    var r = entry.GetTargetRenderer(target);
                    bool isActive = IsRendererActiveAndEnabled(r);
                    if (!optimizeDisabled && !isActive) continue;

                    // Always count original triangles for total
                    totalCount += TryGetOriginalTriangleCount(entry, false, out var triangleCount) ? triangleCount : 0;
                }
            }
            return totalCount;
        }

        private int GetCostumeSimplifiedTriangleCount(string costumeGroup, bool usePreview)
        {
            var totalCount = 0;
            var target = Target;
            var cg = target.CostumeGroups.FirstOrDefault(g => g.GroupName == costumeGroup);
            bool optimizeDisabled = cg != null ? cg.OptimizeDisabledGameObjects : false;
            bool optimizeEnabled = cg == null || cg.OptimizeGroupEnabled;

            foreach (var entry in target.Entries)
            {
                if (entry.IsValid(target) && entry.CostumeGroup == costumeGroup)
                {
                    var r = entry.GetTargetRenderer(target);
                    bool isActive = IsRendererActiveAndEnabled(r);
                    if (!optimizeDisabled && !isActive) continue;

                    // For disabled groups, count original triangles; for enabled groups, count simplified
                    if (optimizeEnabled)
                    {
                        totalCount += TryGetSimplifiedTriangleCount(entry, usePreview, out var triangleCount) ? triangleCount : 0;
                    }
                    else
                    {
                        totalCount += TryGetOriginalTriangleCount(entry, false, out var triangleCount) ? triangleCount : 0;
                    }
                }
            }
            return totalCount;
        }

        private int GetCostumeOriginalTriangleCount(string costumeGroup)
        {
            var totalCount = 0;
            var target = Target;
            var cg = target.CostumeGroups.FirstOrDefault(g => g.GroupName == costumeGroup);
            bool optimizeDisabled = cg != null ? cg.OptimizeDisabledGameObjects : false;
            bool optimizeEnabled = cg == null || cg.OptimizeGroupEnabled;

            foreach (var entry in target.Entries)
            {
                if (entry.IsValid(target) && entry.CostumeGroup == costumeGroup)
                {
                    var r = entry.GetTargetRenderer(target);
                    bool isActive = IsRendererActiveAndEnabled(r);
                    if (!optimizeDisabled && !isActive) continue;

                    // Always count original triangles for total
                    totalCount += TryGetOriginalTriangleCount(entry, false, out var triangleCount) ? triangleCount : 0;
                }
            }
            return totalCount;
        }
        private bool TryGetSimplifiedTriangleCount(MeshiaCascadingAvatarMeshSimplifierRendererEntry entry, bool preferPreview, out int triangleCount)
        {

            if (!entry.Enabled)
            {
                return TryGetOriginalTriangleCount(entry, preferPreview, out triangleCount);
            }
            if (entry.GetTargetRenderer(Target) is not { } targetRenderer)
            {
                triangleCount = -1;
                return false;
            }
            if (preferPreview && MeshiaCascadingAvatarMeshSimplifierPreview.TriangleCountCache.TryGetValue(targetRenderer, out var triCount))
            {
                triangleCount = triCount.simplified;
                return true;
            }
            else
            {

                if (RendererUtility.GetMesh(targetRenderer) is { } mesh)
                {
                    triangleCount = Math.Min(mesh.GetTriangleCount(), entry.TargetTriangleCount);
                    return true;
                }
                else
                {
                    triangleCount = -1;
                    return false;
                }
            }
        }
        private bool TryGetOriginalTriangleCount(MeshiaCascadingAvatarMeshSimplifierRendererEntry entry, bool preferPreview, out int triangleCount)
        {
            if (entry.GetTargetRenderer(Target) is not { } targetRenderer)
            {
                triangleCount = -1;
                return false;
            }
            if (preferPreview && MeshiaCascadingAvatarMeshSimplifierPreview.TriangleCountCache.TryGetValue(targetRenderer, out var triCount))
            {
                triangleCount = triCount.proxy;
                return true;
            }
            else
            {
                if (RendererUtility.GetMesh(targetRenderer) is { } mesh)
                {

                    triangleCount = mesh.GetTriangleCount();

                    return true;
                }
                else
                {
                    triangleCount = -1;
                    return false;
                }
            }
        }

        private void AdjustQuality(int fixedIndex = -1)
        {
            // Don't call ApplyModifiedProperties() here - we want to read the current state
            // from the target object which was just updated by the caller
            var target = Target;
            var entries = target.Entries;

            Undo.RecordObject(target, "Adjust Quality");

            // Group entries by costume
            var entriesByCostume = new Dictionary<string, List<int>>();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!entry.IsValid(target)) continue;

                var costumeGroup = entry.CostumeGroup;
                if (!entriesByCostume.ContainsKey(costumeGroup))
                {
                    entriesByCostume[costumeGroup] = new List<int>();
                }
                entriesByCostume[costumeGroup].Add(i);
            }

            // Process each costume group separately
            foreach (var costumeGroup in target.CostumeGroups)
            {
                if (!entriesByCostume.TryGetValue(costumeGroup.GroupName, out var groupIndices))
                    continue;

                if (!costumeGroup.OptimizeGroupEnabled)
                {
                    continue;
                }

                var targetTotalCount = costumeGroup.TargetTriangleCount;
                var optimizeDisabled = costumeGroup.OptimizeDisabledGameObjects;

                // Determine effective minimum and original counts for each entry
                var originalCounts = new int[entries.Count];
                var minCounts = new int[entries.Count];
                var isAdjustable = new bool[entries.Count];
                var finalCounts = new int[entries.Count];

                int fixedTotal = 0;
                int remainingTarget = targetTotalCount;

                foreach (var i in groupIndices)
                {
                    var entry = entries[i];
                    var r = entry.GetTargetRenderer(target);
                    bool isActive = IsRendererActiveAndEnabled(r);

                    if (!optimizeDisabled && !isActive)
                    {
                        finalCounts[i] = 0;
                        continue;
                    }

                    TryGetOriginalTriangleCount(entry, false, out var maxTriangleCount);
                    originalCounts[i] = maxTriangleCount;
                    minCounts[i] = Mathf.Min(maxTriangleCount, target.MinimumTriangleThreshold);

                    if (!entry.Enabled || entry.Fixed || i == fixedIndex)
                    {
                        isAdjustable[i] = false;
                        TryGetSimplifiedTriangleCount(entry, false, out var fixedCount);
                        finalCounts[i] = fixedCount;
                        fixedTotal += fixedCount;
                    }
                    else
                    {
                        isAdjustable[i] = true;
                        finalCounts[i] = -1; // to be calculated
                    }
                }

                remainingTarget -= fixedTotal;

                var activeAdjustableIndices = groupIndices.Where(i => isAdjustable[i]).ToList();

                // Iteratively distribute remaining target
                bool changed = true;
                while (changed && activeAdjustableIndices.Count > 0 && remainingTarget > 0)
                {
                    changed = false;
                    long adjustableOriginalTotal = activeAdjustableIndices.Sum(i => (long)originalCounts[i]);

                    if (adjustableOriginalTotal == 0) break;

                    double proportion = (double)remainingTarget / adjustableOriginalTotal;

                    for (int j = activeAdjustableIndices.Count - 1; j >= 0; j--)
                    {
                        int index = activeAdjustableIndices[j];
                        int proposedValue = (int)(originalCounts[index] * proportion);

                        // Check bounds
                        if (proposedValue <= minCounts[index])
                        {
                            finalCounts[index] = minCounts[index];
                            remainingTarget -= minCounts[index];
                            activeAdjustableIndices.RemoveAt(j);
                            changed = true;
                        }
                        else if (proposedValue >= originalCounts[index])
                        {
                            finalCounts[index] = originalCounts[index];
                            remainingTarget -= originalCounts[index];
                            activeAdjustableIndices.RemoveAt(j);
                            changed = true;
                        }
                    }
                }

                // If no more bounds are hit, give everyone exactly their proportional share
                if (activeAdjustableIndices.Count > 0)
                {
                    long adjustableOriginalTotal = activeAdjustableIndices.Sum(i => (long)originalCounts[i]);
                    if (adjustableOriginalTotal > 0)
                    {
                        double proportion = Math.Max(0.0, (double)remainingTarget / adjustableOriginalTotal);
                        foreach (var index in activeAdjustableIndices)
                        {
                            finalCounts[index] = Mathf.Clamp((int)(originalCounts[index] * proportion), minCounts[index], originalCounts[index]);
                        }
                    }
                    else
                    {
                        foreach (var index in activeAdjustableIndices)
                        {
                            finalCounts[index] = minCounts[index];
                        }
                    }
                }

                // Apply to entries
                foreach (var i in groupIndices)
                {
                    if (isAdjustable[i])
                    {
                        entries[i].TargetTriangleCount = finalCounts[i];
                    }
                }
            }

            serializedObject.Update();
        }

        private void SetQualityAll(float ratio)
        {
            var target = Target;
            var entries = target.Entries;
            var entriesProperty = EntriesProperty;
            for (int i = 0; i < entries.Count; i++)
            {

                var entry = entries[i];
                if (!entry.IsValid(target))
                {
                    continue;
                }

                if (!entry.Fixed)
                {
                    var entryProperty = entriesProperty.GetArrayElementAtIndex(i);

                    TryGetOriginalTriangleCount(entry, true, out var originalTriangleCount);
                    var targetTriangleCountProperty = entryProperty.FindPropertyRelative(nameof(MeshiaCascadingAvatarMeshSimplifierRendererEntry.TargetTriangleCount));


                    targetTriangleCountProperty.intValue = (int)(originalTriangleCount * ratio);
                }
            }
        }

        /// <summary>
        /// Computes per-vertex occlusion weights for all entries and sends the first valid
        /// SkinnedMeshRenderer's data to <see cref="OcclusionWeightGizmoDrawer"/> for Scene View preview.
        /// </summary>
        private void ComputeAndPreviewOcclusionWeights()
        {
            var target = Target;
            if (!target.UseOcclusionWeightedSimplification) return;

            // Collect all active renderer bounds on the avatar
            var avatarRoot = target.transform.parent?.gameObject;
            if (avatarRoot == null) return;

            var allRenderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            var allBoundsList = new System.Collections.Generic.List<Bounds>();
            foreach (var r in allRenderers)
            {
                if (r.gameObject.activeInHierarchy && r.enabled)
                    allBoundsList.Add(r.bounds);
            }
            var allBounds = allBoundsList.ToArray();

            // Find the first valid SkinnedMeshRenderer entry to preview
            foreach (var entry in target.Entries)
            {
                if (!entry.IsValid(target) || !entry.Enabled) continue;
                if (entry.GetTargetRenderer(target) is not SkinnedMeshRenderer smr) continue;

                var bakedMesh = new Mesh();
                try
                {
                    smr.BakeMesh(bakedMesh);

                    // Transform vertices to world space
                    var localToWorld = smr.transform.localToWorldMatrix;
                    var verts = bakedMesh.vertices;
                    for (int v = 0; v < verts.Length; v++)
                        verts[v] = localToWorld.MultiplyPoint3x4(verts[v]);
                    bakedMesh.vertices = verts;

                    // Exclude this renderer's own bounds
                    var ownBounds = smr.bounds;
                    var occluderList = new System.Collections.Generic.List<Bounds>();
                    foreach (var b in allBounds)
                    {
                        if (b.center == ownBounds.center && b.size == ownBounds.size) continue;
                        occluderList.Add(b);
                    }

                    var weights = OcclusionVertexWeighter.ComputeWeights(bakedMesh, occluderList.ToArray(), target.OcclusionWeightStrength);
                    OcclusionWeightGizmoDrawer.SetPreviewData(bakedMesh, weights);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(bakedMesh);
                }

                // Only preview the first entry
                break;
            }
        }

    }

    internal static class GUIStyleHelper
    {
        private static GUIStyle? m_iconButtonStyle;
        public static GUIStyle IconButtonStyle
        {
            get
            {
                if (m_iconButtonStyle == null) m_iconButtonStyle = InitIconButtonStyle();
                return m_iconButtonStyle;
            }
        }
        static GUIStyle InitIconButtonStyle()
        {
            var style = new GUIStyle();
            return style;
        }

        private static GUIStyle? m_redStyle;
        public static GUIStyle RedStyle
        {
            get
            {
                if (m_redStyle == null) m_redStyle = InitRedStyle();
                return m_redStyle;
            }
        }
        static GUIStyle InitRedStyle()
        {
            var style = new GUIStyle();
            style.normal = new GUIStyleState() { textColor = Color.red };
            return style;
        }
    }
}

#endif
