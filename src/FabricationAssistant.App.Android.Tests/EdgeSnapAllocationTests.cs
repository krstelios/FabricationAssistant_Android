using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

[Collection("EdgeSnapState")]
public sealed class EdgeSnapAllocationTests
{
    [Fact]
    public void TrySnapPrepared_WithDiagnosticsOff_DoesNotAllocatePerScan()
    {
        EdgeSnapService.SnapEnabled = true;
        EdgeSnapService.EndpointSnapEnabled = true;
        EdgeSnapService.MidpointSnapEnabled = true;
        EdgeSnapService.EdgeSnapToleranceFactor = 1.0;
        EdgeSnapService.EndpointSnapToleranceFactor = 1.0;
        EdgeSnapService.WeldToleranceScale = 1.0e-5;
        EdgeSnapService.VisibilityFilter = null;
        EdgeSnapService.DiagnosticsLog = null;

        var svc = new EdgeSnapService();
        float[] edges = { 0, 0, 0, 1, 0, 0 };
        svc.Prepare(edges);
        var origin = new Vector3d(0.5, 0, 1);
        var dir = new Vector3d(0, 0, -1);

        // warm up JIT
        for (int i = 0; i < 200; i++)
            svc.TrySnapPrepared(edges, Matrix4d.Identity, origin, dir, 0.022, 0.0085);

        const int iterations = 2000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            svc.TrySnapPrepared(edges, Matrix4d.Identity, origin, dir, 0.022, 0.0085);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // With diagnostics off there must be no per-scan heap allocation.
        Assert.True(allocated < 256, $"allocated {allocated} bytes over {iterations} scans");
    }
}
