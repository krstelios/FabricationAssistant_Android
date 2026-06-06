using FabricationAssistant.Core.Measurement.Engine;

namespace FabricationAssistant.App.Android.Measurement;

internal sealed class AndroidSnapWarmupRunner
{
    private readonly EdgeSnapService _snapWarmup = new();
    private readonly FeatureEdgeExtractor _featureEdges = new();

    public AndroidSnapWarmupResult WarmSnapModels(
        IReadOnlyList<AndroidSnapWarmupMesh> snapshots,
        long budgetMs,
        CancellationToken token)
    {
        long segmentCount = 0;
        int warmedMeshes = 0;
        bool budgetReached = false;
        long sliceStart = Environment.TickCount64;

        foreach (AndroidSnapWarmupMesh snapshot in snapshots)
        {
            token.ThrowIfCancellationRequested();

            float[] edges = snapshot.EdgePositions.Length >= 6
                ? snapshot.EdgePositions
                : _featureEdges.Extract(snapshot.Positions, snapshot.Indices);
            if (edges.Length < 6)
                continue;

            int meshSegments = edges.Length / 6;
            _snapWarmup.Prepare(edges);
            segmentCount += meshSegments;
            warmedMeshes++;

            if (Environment.TickCount64 - sliceStart >= budgetMs)
            {
                budgetReached = true;
                Thread.Yield();
                sliceStart = Environment.TickCount64;
            }
        }

        return new AndroidSnapWarmupResult(warmedMeshes, segmentCount, budgetReached);
    }
}

internal readonly record struct AndroidSnapWarmupMesh(float[] EdgePositions, float[] Positions, int[] Indices);

internal readonly record struct AndroidSnapWarmupResult(int WarmedMeshes, long SegmentCount, bool BudgetReached);
