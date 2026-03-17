#nullable enable
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Meshia.MeshSimplification
{
    [BurstCompile]
    struct ScaleVertexErrorQuadricsJob : IJobParallelForDefer
    {
        /// <summary>
        /// Per-vertex simplification weights. Higher value = simplify more aggressively.
        /// Applied as a divisor to the error quadric: lower quadric = cheaper merge = simplified first.
        /// </summary>
        [ReadOnly]
        public NativeArray<float> VertexSimplificationWeights;

        public NativeArray<ErrorQuadric> VertexErrorQuadrics;

        public void Execute(int index)
        {
            float simplificationWeight = index < VertexSimplificationWeights.Length
                ? VertexSimplificationWeights[index]
                : 1f;

            // Divide the quadric by simplificationWeight.
            // A smaller quadric means the vertex is cheaper to merge, so it gets simplified first.
            float quadricScale = simplificationWeight > 0f ? (1f / simplificationWeight) : 1f;
            VertexErrorQuadrics[index] = VertexErrorQuadrics[index] * quadricScale;
        }
    }
}
