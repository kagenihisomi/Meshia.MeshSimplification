#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using nadena.dev.ndmf.preview;
using System.Collections;

namespace Meshia.MeshSimplification.Ndmf.Editor.Preview
{
    internal abstract class MeshiaMeshSimplifierPreviewBase<TDerived> : IRenderFilter
        where TDerived : MeshiaMeshSimplifierPreviewBase<TDerived>
    {
        public static readonly Dictionary<Renderer, (int proxy, int simplified)> TriangleCountCache = new();
        private static readonly Dictionary<Renderer, Mesh> ActiveNodeMeshByRenderer = new();

        public static TogglablePreviewNode PreviewControlNode { get; } = TogglablePreviewNode.Create(
                () => typeof(TDerived).Name,
                qualifiedName: typeof(TDerived).FullName
            );

        static TogglablePreviewNode[] PreviewControlNodes { get; } = { PreviewControlNode };

        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes() => PreviewControlNodes;

        public bool IsEnabled(ComputeContext context)
        {
            return context.Observe(PreviewControlNode.IsEnabled);
        }

        public static bool IsEnabled() => PreviewControlNode.IsEnabled.Value;

        public abstract ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context);

        async Task<IRenderFilterNode> IRenderFilter.Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context)
        {
            var original = proxyPairs.First().Item1;
            var proxy = proxyPairs.First().Item2;
            var proxyMesh = RendererUtility.GetRequiredMesh(proxy);

            var (target, options, preserveBorderEdgesBoneIndices) = QueryTarget(context, group, original, proxy);

            Mesh simplifiedMesh = new();
            try
            {
                await MeshSimplifier.SimplifyAsync(proxyMesh, target, options, preserveBorderEdgesBoneIndices, simplifiedMesh);
            }
            catch (Exception)
            {
                UnityEngine.Object.DestroyImmediate(simplifiedMesh);
                throw;
            }

            PruneDestroyedRenderers();
            if (original != null)
            {
                TriangleCountCache[original] = (proxyMesh.GetTriangleCount(), simplifiedMesh.GetTriangleCount());
                ActiveNodeMeshByRenderer[original] = simplifiedMesh;
            }
         
            return new NdmfMeshSimplifierPreviewNode(original, simplifiedMesh, OnNodeDisposed);
        }

        private static void OnNodeDisposed(Renderer? original, Mesh simplifiedMesh)
        {
            if (original != null &&
                ActiveNodeMeshByRenderer.TryGetValue(original, out var currentMesh) &&
                ReferenceEquals(currentMesh, simplifiedMesh))
            {
                ActiveNodeMeshByRenderer.Remove(original);
                TriangleCountCache.Remove(original);
            }
            PruneDestroyedRenderers();
        }

        private static void PruneDestroyedRenderers()
        {
            List<Renderer>? dead = null;
            foreach (var key in TriangleCountCache.Keys)
            {
                if (key == null)
                {
                    dead ??= new List<Renderer>();
                    dead.Add(key);
                }
            }
            if (dead != null)
            {
                foreach (var key in dead)
                {
                    TriangleCountCache.Remove(key);
                    ActiveNodeMeshByRenderer.Remove(key);
                }
            }
        }

        protected abstract (MeshSimplificationTarget, MeshSimplifierOptions, BitArray?) QueryTarget(ComputeContext context, RenderGroup group, Renderer original, Renderer proxy);
    }

    internal class NdmfMeshSimplifierPreviewNode : IRenderFilterNode
    {
        public RenderAspects WhatChanged => RenderAspects.Mesh;
        private readonly Renderer? _original;
        private readonly Mesh _simplifiedMesh;
        private readonly Action<Renderer?, Mesh>? _onDispose;

        public NdmfMeshSimplifierPreviewNode(Renderer? original, Mesh mesh, Action<Renderer?, Mesh>? onDispose = null)
        {
            _original = original;
            _simplifiedMesh = mesh;
            _onDispose = onDispose;
        }

        public void OnFrame(Renderer original, Renderer proxy)
        {
            RendererUtility.SetMesh(proxy, _simplifiedMesh);
        }

        void IDisposable.Dispose()
        {
            _onDispose?.Invoke(_original, _simplifiedMesh);
            UnityEngine.Object.DestroyImmediate(_simplifiedMesh);
        }
    }
}
