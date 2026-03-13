#nullable enable
using Meshia.MeshSimplification.Ndmf.Editor;
using Meshia.MeshSimplification.Ndmf.Editor.Preview;
using nadena.dev.ndmf;
using nadena.dev.ndmf.preview;
using System;
using System.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;


[assembly: ExportsPlugin(typeof(NdmfPlugin))]

namespace Meshia.MeshSimplification.Ndmf.Editor
{
    class NdmfPlugin : Plugin<NdmfPlugin>
    {
        public override string DisplayName => "Meshia NDMF Mesh Simplifier";

        protected override void Configure()
        {
#if ENABLE_MODULAR_AVATAR

            InPhase(BuildPhase.Resolving)
                .Run("Resolve References", context =>
                {
                    var meshiaCascadingMeshSimplifiers = context.AvatarRootObject.GetComponentsInChildren<MeshiaCascadingAvatarMeshSimplifier>(true);
                    foreach (var cascadingMeshSimplifier in meshiaCascadingMeshSimplifiers)
                    {
                        cascadingMeshSimplifier.ResolveReferences();
                    }
                });

#endif

            InPhase(BuildPhase.Optimizing)
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run("Simplify meshes", context =>
                {
                    var meshiaMeshSimplifiers = context.AvatarRootObject.GetComponentsInChildren<MeshiaMeshSimplifier>(true);
#if ENABLE_MODULAR_AVATAR
                    
                    var meshiaCascadingMeshSimplifiers = context.AvatarRootObject.GetComponentsInChildren<MeshiaCascadingAvatarMeshSimplifier>(true);
#endif

                    using (ListPool<(Mesh Mesh, MeshSimplificationTarget Target, MeshSimplifierOptions Options, BitArray? preserveBorderEdgesBoneIndices, float[]? VertexOcclusionWeights, Mesh Destination)>.Get(out var parameters))
                    {
                        foreach (var meshiaMeshSimplifier in meshiaMeshSimplifiers)
                        {
                            if(meshiaMeshSimplifier.enabled && meshiaMeshSimplifier.TryGetComponent<Renderer>(out var renderer))
                            {
                                var sourceMesh = RendererUtility.GetRequiredMesh(renderer);
                                Mesh simplifiedMesh = new();
                                parameters.Add((sourceMesh, meshiaMeshSimplifier.target, meshiaMeshSimplifier.options, null, null, simplifiedMesh));
                            }
                        }
#if ENABLE_MODULAR_AVATAR

                        foreach (var meshiaCascadingMeshSimplifier in meshiaCascadingMeshSimplifiers)
                        {
                            // Collect all active renderers for occlusion-weighted simplification
                            Renderer[]? allActiveRenderers = null;
                            if (meshiaCascadingMeshSimplifier.UseOcclusionWeightedSimplification)
                            {
                                allActiveRenderers = CollectActiveRenderers(context.AvatarRootObject);
                            }

                            foreach (var entry in meshiaCascadingMeshSimplifier.Entries)
                            {
                                if (!entry.IsValid(meshiaCascadingMeshSimplifier) || !entry.Enabled) continue;
                                var mesh = RendererUtility.GetRequiredMesh(entry.GetTargetRenderer(meshiaCascadingMeshSimplifier)!);
                                var target = new MeshSimplificationTarget() { Kind = MeshSimplificationTargetKind.AbsoluteTriangleCount, Value = entry.TargetTriangleCount };
                                Mesh simplifiedMesh = new();

                                var preserveBorderEdgesBoneIndices = MeshiaCascadingAvatarMeshSimplifier.GetPreserveBorderEdgesBoneIndices(context.AvatarRootObject, meshiaCascadingMeshSimplifier, entry);

                                float[]? vertexOcclusionWeights = null;
                                if (meshiaCascadingMeshSimplifier.UseOcclusionWeightedSimplification
                                    && allActiveRenderers != null
                                    && entry.GetTargetRenderer(meshiaCascadingMeshSimplifier) is SkinnedMeshRenderer skinnedMeshRenderer)
                                {
                                    vertexOcclusionWeights = ComputeOcclusionWeightsForRenderer(
                                        skinnedMeshRenderer, allActiveRenderers,
                                        meshiaCascadingMeshSimplifier.OcclusionWeightStrength);
                                }

                                parameters.Add((mesh, target, entry.Options, preserveBorderEdgesBoneIndices, vertexOcclusionWeights, simplifiedMesh));
                            }
                        }

#endif

                        MeshSimplifier.SimplifyBatch(parameters);
                        {
                            var i = 0;

                            foreach (var meshiaMeshSimplifier in meshiaMeshSimplifiers)
                            {
                                if(meshiaMeshSimplifier.enabled && meshiaMeshSimplifier.TryGetComponent<Renderer>(out var renderer))
                                {
                                    var (mesh, target, options, _, _, simplifiedMesh) = parameters[i++];
                                    AssetDatabase.AddObjectToAsset(simplifiedMesh, context.AssetContainer);
                                    RendererUtility.SetMesh(renderer, simplifiedMesh);
                                }
                            }
                            foreach (var meshiaMeshSimplifier in meshiaMeshSimplifiers)
                            {
                                UnityEngine.Object.DestroyImmediate(meshiaMeshSimplifier);
                            }

#if ENABLE_MODULAR_AVATAR

                            foreach (var meshiaCascadingMeshSimplifier in meshiaCascadingMeshSimplifiers)
                            {
                                foreach (var cascadingTarget in meshiaCascadingMeshSimplifier.Entries)
                                {
                                    if (!cascadingTarget.IsValid(meshiaCascadingMeshSimplifier) || !cascadingTarget.Enabled) continue;
                                    var renderer = cascadingTarget.GetTargetRenderer(meshiaCascadingMeshSimplifier)!;
                                    var (mesh, target, options, _, _, simplifiedMesh) = parameters[i++];
                                    AssetDatabase.AddObjectToAsset(simplifiedMesh, context.AssetContainer);
                                    RendererUtility.SetMesh(renderer, simplifiedMesh);

                                }
                            }

                            foreach (var meshiaCascadingMeshSimplifier in meshiaCascadingMeshSimplifiers)
                            {
                                UnityEngine.Object.DestroyImmediate(meshiaCascadingMeshSimplifier);
                            }
#endif

                        }
                    }
                }).PreviewingWith(new IRenderFilter[]
                {
                    new MeshiaMeshSimplifierPreview(),
#if ENABLE_MODULAR_AVATAR
                    new MeshiaCascadingAvatarMeshSimplifierPreview(),
#endif
                })
            ;
        }

#if ENABLE_MODULAR_AVATAR
        /// <summary>
        /// Returns all active, enabled renderers on the avatar.
        /// </summary>
        private static Renderer[] CollectActiveRenderers(GameObject avatarRoot)
        {
            var renderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            using (ListPool<Renderer>.Get(out var activeList))
            {
                foreach (var r in renderers)
                {
                    if (r.gameObject.activeInHierarchy && r.enabled)
                        activeList.Add(r);
                }
                return activeList.ToArray();
            }
        }

        /// <summary>
        /// Bakes the skinned mesh renderer to world space and computes per-vertex occlusion weights.
        /// Excludes the renderer itself from the occluder set using reference equality.
        /// </summary>
        private static float[] ComputeOcclusionWeightsForRenderer(
            SkinnedMeshRenderer skinnedMeshRenderer,
            Renderer[] allActiveRenderers,
            float occlusionWeightStrength)
        {
            var bakedMesh = new Mesh();
            try
            {
                skinnedMeshRenderer.BakeMesh(bakedMesh);

                // Transform vertices from local space to world space
                var localToWorld = skinnedMeshRenderer.transform.localToWorldMatrix;
                var verts = bakedMesh.vertices;
                for (int v = 0; v < verts.Length; v++)
                    verts[v] = localToWorld.MultiplyPoint3x4(verts[v]);
                bakedMesh.vertices = verts;

                // Build occluder bounds: all active renderers except the current one (by reference equality)
                using (ListPool<Bounds>.Get(out var occluderList))
                {
                    foreach (var r in allActiveRenderers)
                    {
                        if (!ReferenceEquals(r, skinnedMeshRenderer))
                            occluderList.Add(r.bounds);
                    }

                    return OcclusionVertexWeighter.ComputeWeights(bakedMesh, occluderList.ToArray(), occlusionWeightStrength);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(bakedMesh);
            }
        }
#endif
    }
}

