#nullable enable

#if ENABLE_MODULAR_AVATAR

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using nadena.dev.modular_avatar.core;
using UnityEngine.Pool;
using System.Diagnostics.CodeAnalysis;
using System.Collections;
using Unity.Mathematics;

namespace Meshia.MeshSimplification.Ndmf
{
    [Serializable]
    public class CostumeGroup
    {
        public string GroupName = "";
        public int TargetTriangleCount = 70000;
        public bool OptimizeGroupEnabled = true;
        public bool OptimizeDisabledGameObjects = false;
    }

    [AddComponentMenu("Meshia Mesh Simplification/Meshia Cascading Avatar Mesh Simplifier")]
    public class MeshiaCascadingAvatarMeshSimplifier : MonoBehaviour
#if ENABLE_VRCHAT_BASE
    , VRC.SDKBase.IEditorOnly
#endif
    {
        public List<MeshiaCascadingAvatarMeshSimplifierRendererEntry> Entries = new();
        public int TargetTriangleCount = 70000;
        public bool AutoAdjustEnabled = true;
        public List<CostumeGroup> CostumeGroups = new();
        public int MinimumTriangleThreshold = 500;

        /// <summary>
        /// When true, per-vertex occlusion weights are computed at build time and fed into
        /// the mesh simplifier so that occluded regions of a mesh collapse more aggressively
        /// than visible regions. This enables localized simplification within a single mesh.
        /// </summary>
        public bool UseOcclusionWeightedSimplification = false;

        /// <summary>
        /// Controls how strongly occlusion affects the per-vertex simplification weight.
        /// Range [0, 1]. 0 = no effect (uniform simplification), 1 = maximum differentiation
        /// (occluded vertices collapse as aggressively as the quality setting allows).
        /// </summary>
        [Range(0f, 1f)]
        public float OcclusionWeightStrength = 0.7f;

        public void RefreshEntries()
        {
            using (ListPool<Renderer>.Get(out var ownedRenderers))
            {
                GetOwnedRenderers(ownedRenderers);
                var currentEntries = Entries.Select(t => t.GetTargetRenderer(this));
                var addedEntries = ownedRenderers.Except(currentEntries)
                    .Where(MeshiaCascadingAvatarMeshSimplifierRendererEntry.IsValidTarget)
                    .Select(renderer =>
                    {
                        var entry = new MeshiaCascadingAvatarMeshSimplifierRendererEntry(renderer!);
                        // Normalize group name to avoid accidental duplicates (trim whitespace)
                        entry.CostumeGroup = (GetCostumeGroupName(renderer!) ?? string.Empty).Trim();
                        return entry;
                    }).ToArray();

                Entries.AddRange(addedEntries);
            }

            // Sync CostumeGroups list: add any new groups, preserve existing settings
            // Normalize existing group names and use ordinal comparer to avoid
            // accidental duplicates caused by casing/whitespace differences.
            var existingGroupNames = new HashSet<string>(CostumeGroups.Select(g => (g.GroupName ?? string.Empty).Trim()), StringComparer.Ordinal);
            foreach (var entry in Entries)
            {
                if (!string.IsNullOrEmpty(entry.CostumeGroup) && !existingGroupNames.Contains(entry.CostumeGroup))
                {
                    CostumeGroups.Add(new CostumeGroup { GroupName = entry.CostumeGroup, TargetTriangleCount = TargetTriangleCount });
                    existingGroupNames.Add(entry.CostumeGroup);
                }
            }

            // Normalize entries' CostumeGroup values so they match the normalized
            // keys used above. This helps avoid creating new groups when a renderer
            // toggles active/inactive state and its group string contains stray
            // whitespace or casing differences.
            for (int i = 0; i < Entries.Count; i++)
            {
                Entries[i].CostumeGroup = (Entries[i].CostumeGroup ?? string.Empty).Trim();
            }

            // Deduplicate CostumeGroups while preserving first-seen order. Merge
            // non-default settings from later duplicates into the first occurrence.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var deduped = new List<CostumeGroup>();
            foreach (var cg in CostumeGroups)
            {
                var key = (cg.GroupName ?? string.Empty).Trim();
                if (!seen.Contains(key))
                {
                    seen.Add(key);
                    cg.GroupName = key;
                    deduped.Add(cg);
                }
                else
                {
                    var existing = deduped.First(d => string.Equals(d.GroupName, key, StringComparison.Ordinal));
                    existing.OptimizeGroupEnabled = existing.OptimizeGroupEnabled || cg.OptimizeGroupEnabled;
                    existing.OptimizeDisabledGameObjects = existing.OptimizeDisabledGameObjects || cg.OptimizeDisabledGameObjects;
                    if (existing.TargetTriangleCount == TargetTriangleCount && cg.TargetTriangleCount != TargetTriangleCount)
                    {
                        existing.TargetTriangleCount = cg.TargetTriangleCount;
                    }
                }
            }
            CostumeGroups.Clear();
            CostumeGroups.AddRange(deduped);
        }

        private string GetCostumeGroupName(Renderer renderer)
        {
            var myScopeOrigin = transform.parent;
            if (myScopeOrigin == null) return "";
            Transform current = renderer.transform;
            while (current.parent != null && current.parent != myScopeOrigin)
            {
                current = current.parent;
            }
            if (current.parent != myScopeOrigin) return "";
            // When the renderer itself is a direct child of the avatar root (no
            // costume-container object between it and the root), group it with
            // all other such renderers under the avatar root name so that base
            // avatar body parts share a single group rather than each appearing
            // in its own isolated group.
            if (current == renderer.transform)
                return myScopeOrigin.gameObject.name;
            return current.gameObject.name;
        }

        private void GetOwnedRenderers(List<Renderer> ownedRenderers)
        {
            var myScopeOrigin = transform.parent;

            if (myScopeOrigin == null)
            {
                throw new InvalidOperationException($"{nameof(MeshiaCascadingAvatarMeshSimplifier)} should not be attached to root GameObject.");
            }
            using (ListPool<MeshiaCascadingAvatarMeshSimplifier>.Get(out var childSimplifiers))
            using (HashSetPool<Transform>.Get(out var otherScopeOrigins))
            {
                myScopeOrigin.gameObject.GetComponentsInChildren(childSimplifiers);
                foreach (var childSimplifier in childSimplifiers)
                {
                    if (childSimplifier != this)
                    {
                        var otherScopeOrigin = childSimplifier.transform.parent;
                        if (otherScopeOrigin == myScopeOrigin)
                        {
                            throw new InvalidOperationException($"Multiple {nameof(MeshiaCascadingAvatarMeshSimplifier)} is attached to direct children of GameObject. This is not allowed.");
                        }
                        otherScopeOrigins.Add(otherScopeOrigin);

                    }
                }

                using (ListPool<Renderer>.Get(out var childRenderers))
                {
                    // Include inactive children so disabled renderers are tracked
                    // and their costume groups are visible in the inspector.
                    // Populate the list with all renderers (including inactive)
                    childRenderers.AddRange(myScopeOrigin.gameObject.GetComponentsInChildren<Renderer>(true));
                    for (int i = 0; i < childRenderers.Count;)
                    {
                        Renderer? childRenderer = childRenderers[i];

                        if (childRenderer is MeshRenderer or SkinnedMeshRenderer)
                        {
                            i++;
                        }
                        else
                        {
                            childRenderers[i] = childRenderers[^1];
                            childRenderers.RemoveAt(childRenderers.Count - 1);
                        }
                    }
                    if (otherScopeOrigins.Count != 0)
                    {
                        for (int i = 0; i < childRenderers.Count;)
                        {
                            Renderer? childRenderer = childRenderers[i];
                            if (IsOwnedByThis(childRenderer))
                            {
                                i++;
                            }
                            else
                            {
                                childRenderers[i] = childRenderers[^1];
                                childRenderers.RemoveAt(childRenderers.Count - 1);
                            }
                            bool IsOwnedByThis(Renderer childRenderer)
                            {
                                var currentTransform = childRenderer.transform;
                                while (currentTransform != myScopeOrigin)
                                {
                                    if (otherScopeOrigins.Contains(currentTransform))
                                    {
                                        return false;
                                    }
                                    else
                                    {
                                        currentTransform = currentTransform.parent;
                                    }

                                }
                                return true;
                            }
                        }
                    }
                    ownedRenderers.AddRange(childRenderers);
                }
            }


        }

        public void ResolveReferences()
        {
            foreach (var target in Entries)
            {
                target.ResolveReference(this);
            }
        }
        public static BitArray? GetPreserveBorderEdgesBoneIndices(GameObject avatarRoot, MeshiaCascadingAvatarMeshSimplifier avatarMeshSimplifier, MeshiaCascadingAvatarMeshSimplifierRendererEntry entry)
        {
            if (avatarRoot.TryGetComponent(out Animator avatarAnimator) && entry.GetTargetRenderer(avatarMeshSimplifier) is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                var bones = skinnedMeshRenderer.bones;
                var preserveBorderEdgeBoneIndices = new BitArray(bones.Length);

                for (ulong boneMask = entry.PreserveBorderEdgesBones; boneMask != 0ul; boneMask &= boneMask - 1)
                {
                    var bone = (HumanBodyBones)math.tzcnt(boneMask);
                    var boneTransform = avatarAnimator.GetBoneTransform(bone);
                    if (boneTransform != null)
                    {
                        var boneIndex = Array.IndexOf(bones, boneTransform);
                        if (boneIndex != -1)
                        {
                            preserveBorderEdgeBoneIndices.Set(boneIndex, true);
                        }
                    }
                }
                return preserveBorderEdgeBoneIndices;
            }
            else
            {
                return null;
            }


        }
    }

    [Serializable]
    public record MeshiaCascadingAvatarMeshSimplifierRendererEntry
    {
        public AvatarObjectReference RendererObjectReference;
        public int TargetTriangleCount;
        public MeshSimplifierOptions Options = MeshSimplifierOptions.Default;
        public ulong PreserveBorderEdgesBones =
            (1ul << (int)HumanBodyBones.LeftThumbProximal) |
            (1ul << (int)HumanBodyBones.LeftThumbIntermediate) |
            (1ul << (int)HumanBodyBones.LeftThumbDistal) |
            (1ul << (int)HumanBodyBones.LeftIndexProximal) |
            (1ul << (int)HumanBodyBones.LeftIndexIntermediate) |
            (1ul << (int)HumanBodyBones.LeftIndexDistal) |
            (1ul << (int)HumanBodyBones.LeftMiddleProximal) |
            (1ul << (int)HumanBodyBones.LeftMiddleIntermediate) |
            (1ul << (int)HumanBodyBones.LeftMiddleDistal) |
            (1ul << (int)HumanBodyBones.LeftRingProximal) |
            (1ul << (int)HumanBodyBones.LeftRingIntermediate) |
            (1ul << (int)HumanBodyBones.LeftRingDistal) |
            (1ul << (int)HumanBodyBones.LeftLittleProximal) |
            (1ul << (int)HumanBodyBones.LeftLittleIntermediate) |
            (1ul << (int)HumanBodyBones.LeftLittleDistal) |
            (1ul << (int)HumanBodyBones.RightThumbProximal) |
            (1ul << (int)HumanBodyBones.RightThumbIntermediate) |
            (1ul << (int)HumanBodyBones.RightThumbDistal) |
            (1ul << (int)HumanBodyBones.RightIndexProximal) |
            (1ul << (int)HumanBodyBones.RightIndexIntermediate) |
            (1ul << (int)HumanBodyBones.RightIndexDistal) |
            (1ul << (int)HumanBodyBones.RightMiddleProximal) |
            (1ul << (int)HumanBodyBones.RightMiddleIntermediate) |
            (1ul << (int)HumanBodyBones.RightMiddleDistal) |
            (1ul << (int)HumanBodyBones.RightRingProximal) |
            (1ul << (int)HumanBodyBones.RightRingIntermediate) |
            (1ul << (int)HumanBodyBones.RightRingDistal) |
            (1ul << (int)HumanBodyBones.RightLittleProximal) |
            (1ul << (int)HumanBodyBones.RightLittleIntermediate) |
            (1ul << (int)HumanBodyBones.RightLittleDistal);
        public bool Enabled = true;
        public bool Fixed = false;
        public string CostumeGroup = "";

        internal void ClearCache() { }

        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [Obsolete("For serialization only", true)]
#pragma warning disable CS8618
        private MeshiaCascadingAvatarMeshSimplifierRendererEntry()
#pragma warning restore CS8618
        {
        }
        public MeshiaCascadingAvatarMeshSimplifierRendererEntry(Renderer renderer)
        {
            RendererObjectReference = new AvatarObjectReference();
            RendererObjectReference.Set(renderer.gameObject);
            TargetTriangleCount = RendererUtility.GetMesh(renderer)?.GetTriangleCount() ?? 0;
        }

        internal static bool IsValidTarget([NotNullWhen(true)] Renderer? renderer)
        {
            if (renderer == null) return false;
            if (IsEditorOnlyInHierarchy(renderer.gameObject)) return false;
            if (renderer is not SkinnedMeshRenderer and not MeshRenderer) return false;
            var mesh = RendererUtility.GetMesh(renderer);
            if (mesh == null || mesh.GetTriangleCount() == 0) return false;
            return true;
        }

        internal Renderer? GetTargetRenderer(Component container)
        {
            var obj = RendererObjectReference.Get(container);
            if (obj == null) return null;
            return obj.TryGetComponent<Renderer>(out var renderer) && renderer is (MeshRenderer or SkinnedMeshRenderer) ? renderer : null;
        }

        internal bool IsValid(MeshiaCascadingAvatarMeshSimplifier container) => IsValidTarget(GetTargetRenderer(container));

        internal static bool IsEditorOnlyInHierarchy(GameObject gameObject)
        {
            if (gameObject == null) return false;
            Transform current = gameObject.transform;
            while (current != null)
            {
                if (current.CompareTag("EditorOnly"))
                {
                    return true;
                }
                current = current.parent;
            }
            return false;
        }

        internal void ResolveReference(Component container)
        {
            RendererObjectReference.Get(container);
        }
    }

}

#endif