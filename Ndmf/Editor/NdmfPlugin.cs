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
                            if (meshiaMeshSimplifier.enabled && meshiaMeshSimplifier.TryGetComponent<Renderer>(out var renderer))
                            {
                                var sourceMesh = RendererUtility.GetRequiredMesh(renderer);
                                Mesh simplifiedMesh = new();
                                parameters.Add((sourceMesh, meshiaMeshSimplifier.target, meshiaMeshSimplifier.options, null, null, simplifiedMesh));
                            }
                        }
#if ENABLE_MODULAR_AVATAR

                        foreach (var meshiaCascadingMeshSimplifier in meshiaCascadingMeshSimplifiers)
                        {
                            // Build one whole-avatar occluder set (static meshes, post-MA) for this
                            // simplifier.  No BakeMesh is used – the NDMF build operates on
                            // sharedMesh which is already the final MA-processed geometry.
                            AvatarOccluderSet? occluderSet = null;
                            if (meshiaCascadingMeshSimplifier.UseOcclusionWeightedSimplification)
                            {
                                occluderSet = AvatarOccluderSet.Build(context.AvatarRootObject);
                            }

                            try
                            {
                                foreach (var entry in meshiaCascadingMeshSimplifier.Entries)
                                {
                                    if (!entry.IsValid(meshiaCascadingMeshSimplifier) || !entry.Enabled) continue;
                                    var mesh = RendererUtility.GetRequiredMesh(entry.GetTargetRenderer(meshiaCascadingMeshSimplifier)!);
                                    var target = new MeshSimplificationTarget() { Kind = MeshSimplificationTargetKind.AbsoluteTriangleCount, Value = entry.TargetTriangleCount };
                                    Mesh simplifiedMesh = new();

                                    var preserveBorderEdgesBoneIndices = MeshiaCascadingAvatarMeshSimplifier.GetPreserveBorderEdgesBoneIndices(context.AvatarRootObject, meshiaCascadingMeshSimplifier, entry);

                                    float[]? vertexOcclusionWeights = null;
                                    if (occluderSet != null
                                        && entry.GetTargetRenderer(meshiaCascadingMeshSimplifier) is SkinnedMeshRenderer skinnedMeshRenderer)
                                    {
                                        vertexOcclusionWeights = ComputeOcclusionWeightsForRenderer(
                                            skinnedMeshRenderer,
                                            occluderSet,
                                            meshiaCascadingMeshSimplifier.OcclusionWeightStrength);
                                    }

                                    parameters.Add((mesh, target, entry.Options, preserveBorderEdgesBoneIndices, vertexOcclusionWeights, simplifiedMesh));
                                }
                            }
                            finally
                            {
                                occluderSet?.Dispose();
                            }
                        }

#endif

                        MeshSimplifier.SimplifyBatch(parameters);
                        {
                            var i = 0;

                            foreach (var meshiaMeshSimplifier in meshiaMeshSimplifiers)
                            {
                                if (meshiaMeshSimplifier.enabled && meshiaMeshSimplifier.TryGetComponent<Renderer>(out var renderer))
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
        /// Builds world-space MeshCollider objects for every active renderer in the avatar,
        /// using each renderer's static <c>sharedMesh</c> (post-MA, no BakeMesh).
        /// Must be disposed to clean up the temporary GameObjects and Mesh assets.
        /// </summary>
        private sealed class AvatarOccluderSet : System.IDisposable
        {
            private struct Entry
            {
                public Renderer Renderer;
                public MeshCollider Collider;
                public GameObject TempGo;
                public Mesh TempMesh;
            }

            private readonly Entry[] _entries;
            public int Count => _entries.Length;

            private AvatarOccluderSet(Entry[] entries) { _entries = entries; }

            public static AvatarOccluderSet Build(GameObject avatarRoot)
            {
                var allRenderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
                using (ListPool<Entry>.Get(out var list))
                {
                    foreach (var r in allRenderers)
                    {
                        if (!r.gameObject.activeInHierarchy || !r.enabled) continue;
                        var srcMesh = RendererUtility.GetMesh(r);
                        if (srcMesh == null || srcMesh.vertexCount == 0) continue;

                        // Transform static mesh vertices to world space (no BakeMesh – avoids
                        // any dependency on bone/blend-shape state at build time).
                        var worldMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                        var verts = srcMesh.vertices;
                        var l2w = r.transform.localToWorldMatrix;
                        for (int i = 0; i < verts.Length; i++)
                            verts[i] = l2w.MultiplyPoint3x4(verts[i]);
                        worldMesh.vertices = verts;
                        worldMesh.triangles = srcMesh.triangles;

                        var go = new GameObject("MeshiaOccluder") { hideFlags = HideFlags.HideAndDontSave };
                        var col = go.AddComponent<MeshCollider>();
                        col.sharedMesh = worldMesh;
                        list.Add(new Entry { Renderer = r, Collider = col, TempGo = go, TempMesh = worldMesh });
                    }
                    return new AvatarOccluderSet(list.ToArray());
                }
            }

            /// <summary>
            /// Fills <paramref name="buffer"/> with all colliders except the one for
            /// <paramref name="targetRenderer"/>.  Returns the count via <paramref name="count"/>.
            /// </summary>
            public void GetExternalColliders(Renderer targetRenderer, MeshCollider[] buffer, out int count)
            {
                count = 0;
                foreach (var e in _entries)
                {
                    if (!ReferenceEquals(e.Renderer, targetRenderer))
                        buffer[count++] = e.Collider;
                }
            }

            public void Dispose()
            {
                foreach (var e in _entries)
                {
                    if (e.TempGo != null) UnityEngine.Object.DestroyImmediate(e.TempGo);
                    if (e.TempMesh != null) UnityEngine.Object.DestroyImmediate(e.TempMesh);
                }
            }
        }

        /// <summary>
        /// Builds a world-space static mesh from <paramref name="skinnedMeshRenderer"/>'s
        /// <c>sharedMesh</c> and computes per-vertex occlusion weights using the pre-built
        /// <paramref name="occluderSet"/>.  No <c>BakeMesh</c> is called – this is intentional
        /// so the result is deterministic and independent of editor bone/blend-shape state.
        /// </summary>
        private static float[] ComputeOcclusionWeightsForRenderer(
            SkinnedMeshRenderer skinnedMeshRenderer,
            AvatarOccluderSet occluderSet,
            float occlusionWeightStrength)
        {
            var srcMesh = RendererUtility.GetMesh(skinnedMeshRenderer);
            if (srcMesh == null || srcMesh.vertexCount == 0)
                return System.Array.Empty<float>();

            var worldMesh = new Mesh();
            try
            {
                var verts = srcMesh.vertices;
                var normals = srcMesh.normals;
                var l2w = skinnedMeshRenderer.transform.localToWorldMatrix;
                for (int v = 0; v < verts.Length; v++)
                    verts[v] = l2w.MultiplyPoint3x4(verts[v]);
                for (int n = 0; n < normals.Length; n++)
                    normals[n] = l2w.MultiplyVector(normals[n]).normalized;

                worldMesh.vertices = verts;
                worldMesh.normals = normals;
                worldMesh.triangles = srcMesh.triangles;

                var buffer = new MeshCollider[occluderSet.Count];
                occluderSet.GetExternalColliders(skinnedMeshRenderer, buffer, out int count);
                return OcclusionVertexWeighter.ComputeWeights(worldMesh, buffer, count, occlusionWeightStrength);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(worldMesh);
            }
        }
#endif
    }
}

