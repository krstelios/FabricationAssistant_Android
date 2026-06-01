using System.Diagnostics;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

[Collection("EdgeSnapState")]
public sealed class EdgeSnapPerfGuardTests
{
    public EdgeSnapPerfGuardTests()
    {
        EdgeSnapService.SnapEnabled = true;
        EdgeSnapService.EndpointSnapEnabled = true;
        EdgeSnapService.MidpointSnapEnabled = true;
        EdgeSnapService.EdgeSnapToleranceFactor = 1.0;
        EdgeSnapService.EndpointSnapToleranceFactor = 1.0;
        EdgeSnapService.WeldToleranceScale = 1.0e-5;
        EdgeSnapService.VisibilityFilter = null;
        EdgeSnapService.DiagnosticsLog = null;
    }

    [Fact]
    public void HoverScan_OnTenThousandSegmentMesh_StaysWellUnderFrameBudget()
    {
        float[] edges = WeldedRuns(10_000);
        var svc = new EdgeSnapService();
        svc.Prepare(edges);
        var origin = new Vector3d(5, 0, 1000);
        var dir = new Vector3d(0, 0, -1);

        for (int i = 0; i < 500; i++)
            svc.TrySnapPrepared(edges, Matrix4d.Identity, origin, dir, 0.022, 0.0085);

        const int iters = 2000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
            svc.TrySnapPrepared(edges, Matrix4d.Identity, origin, dir, 0.022, 0.0085);
        sw.Stop();
        double perScanMs = sw.Elapsed.TotalMilliseconds / iters;

        // Desktop baseline ~0.012 ms. Guard at 2 ms (catastrophic-regression only).
        Assert.True(perScanMs < 2.0, $"hover scan {perScanMs:0.000} ms/op exceeds 2 ms guard");
    }

    [Fact]
    public void Build_OnFiftyThousandSegmentMesh_CompletesInReasonableTime()
    {
        float[] edges = WeldedRuns(50_000);
        var svc = new EdgeSnapService();
        var sw = Stopwatch.StartNew();
        svc.Prepare(edges);
        sw.Stop();

        // Desktop baseline ~40 ms. Guard at 1500 ms (ARM headroom; build is off the
        // hot path on a background warmup thread).
        Assert.True(sw.Elapsed.TotalMilliseconds < 1500, $"build {sw.Elapsed.TotalMilliseconds:0} ms exceeds 1500 ms guard");
    }

    private static float[] WeldedRuns(int segments)
    {
        var v = new float[segments * 6];
        const int perRun = 10;
        for (int s = 0; s < segments; s++)
        {
            int i = s % perRun;
            float y = (s / perRun) * 5.0f;
            int o = s * 6;
            v[o] = i; v[o + 1] = y; v[o + 2] = 0;
            v[o + 3] = i + 1; v[o + 4] = y; v[o + 5] = 0;
        }
        return v;
    }
}
