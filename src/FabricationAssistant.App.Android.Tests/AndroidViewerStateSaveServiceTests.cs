using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FabricationAssistant.App.Android;
using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Sections;
using FabricationAssistant.Core.Selection;
using FabricationAssistant.Import.Fa;
using FabricationAssistant.Import.Fa.FaViewerState;
using FabricationAssistant.Import.Fa.Internal;
using ICSharpCode.SharpZipLib.Zip;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class AndroidViewerStateSaveServiceTests
{
    [Fact]
    public async Task SaveAsync_WritesViewerState_PreservesMarkup_AndStripsBRep()
    {
        string archivePath = Path.Combine(Path.GetTempPath(), $"fa-android-save-{Guid.NewGuid():N}.fa");
        try
        {
            byte[] snapshotPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            const string markupJson = """{"schemaVersion":"1.0","items":[{"id":"markup-1"}]}""";
            WriteArchive(archivePath, new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [FaArchiveManifest.ManifestEntryName] = Encoding.UTF8.GetBytes(CreateManifestJson()),
                ["Geometry/BRep/occt-shapes.bin"] = [1, 2, 3],
                ["Geometry/BRep/node-ids.json"] = Encoding.UTF8.GetBytes("""{"nodes":["old"]}"""),
                [FaViewerStateReader.MarkupsEntryName] = Encoding.UTF8.GetBytes(markupJson),
                [FaViewerStateReader.SnapshotPrefix + "markup-1.png"] = snapshotPng,
            });

            CameraState camera = new()
            {
                Position = new Vector3d(10, 20, 30),
                Target = new Vector3d(1, 2, 3),
                UpDirection = Vector3d.UnitZ,
                WorldUpDirection = Vector3d.UnitZ,
                FieldOfView = 0.6,
                NearPlane = 0.2,
                FarPlane = 400,
                IsPerspective = false,
                OrthoWidth = 12.5,
            };

            var packageState = new PackageSessionState();
            packageState.SetHidden(["occ-hidden-b", "occ-hidden-a"]);
            packageState.SetIsolated(["occ-isolated"]);
            packageState.SetSelection(["occ-selected"]);
            packageState.SetExpandedPresentedNodes(["node-expanded"]);

            var sections = new SectionService
            {
                FillVisible = false,
                EdgesVisible = true,
            };
            Guid sectionId = sections.Add(new SectionPlane(
                Guid.NewGuid(),
                new Vector3(1, 2, 3),
                Vector3.UnitX,
                Vector3.UnitY,
                Vector3.UnitZ));

            MeasurementId annotationId = MeasurementId.New();
            MeasurementId dimensionId = MeasurementId.New();
            MeasurementResult[] measurements =
            [
                new AnnotationMeasurement(
                    annotationId,
                    new Vector3d(7, 8, 9),
                    "Saved note",
                    "occ-selected",
                    "#2DD4BF",
                    "android-test",
                    DateTimeOffset.Parse("2026-05-28T07:00:00Z"),
                    DateTimeOffset.Parse("2026-05-28T07:05:00Z")),
                new PointToPointMeasurement(
                    dimensionId,
                    new ScenePoint(new Vector3d(0, 0, 0)),
                    new ScenePoint(new Vector3d(0, 3, 4)),
                    new SceneLength(5),
                    new Vector3d(0, 3, 4)),
            ];

            var service = new AndroidViewerStateSaveService();
            await service.SaveAsync(
                archivePath,
                new AndroidViewerStateSnapshot(camera, packageState, sections, measurements),
                CancellationToken.None);

            FaViewerStatePayload payload = await FaViewerStateReader.ReadAsync(archivePath, CancellationToken.None);
            Assert.NotNull(payload.Manifest);
            Assert.Equal("SmokeExport", payload.Manifest!.ExportRootName);
            Assert.Equal(markupJson, payload.MarkupDocumentJson);
            Assert.True(payload.SnapshotPngsByMarkupId.TryGetValue("markup-1", out byte[]? preservedSnapshot));
            Assert.Equal(snapshotPng, preservedSnapshot);

            Assert.NotNull(payload.Camera);
            Assert.Equal([10, 20, 30], payload.Camera!.Position);
            Assert.False(payload.Camera.IsPerspective);
            Assert.Equal(12.5, payload.Camera.OrthoWidth);

            Assert.NotNull(payload.Visibility);
            Assert.Equal(["occ-hidden-a", "occ-hidden-b"], payload.Visibility!.HiddenOccurrenceIds);
            Assert.Equal(["occ-isolated"], payload.Visibility.IsolatedOccurrenceIds);
            Assert.Equal(["occ-selected"], payload.Visibility.SelectedOccurrenceIds);
            Assert.Equal(["node-expanded"], payload.Visibility.ExpandedNodeIds);

            Assert.NotNull(payload.Sections);
            Assert.False(payload.Sections!.FillVisible);
            Assert.True(payload.Sections.EdgesVisible);
            ViewerSectionPlaneDto savedSection = Assert.Single(payload.Sections.Planes);
            Assert.Equal(sectionId, savedSection.Id);

            Assert.NotNull(payload.Measurements);
            Assert.Collection(
                payload.Measurements!.Items,
                annotation =>
                {
                    Assert.Equal(annotationId.Value, annotation.Id);
                    Assert.Equal("annotation", annotation.Kind);
                    Assert.Equal("Saved note", annotation.Text);
                    Assert.NotNull(annotation.Anchor);
                    Assert.Equal([7, 8, 9], annotation.Anchor!);
                },
                dimension =>
                {
                    Assert.Equal(dimensionId.Value, dimension.Id);
                    Assert.Equal("point-to-point", dimension.Kind);
                    Assert.Equal(5, dimension.Distance);
                    Assert.Equal("m", dimension.DistanceUnits);
                });

            using FaArchive archive = FaArchive.Open(archivePath);
            Assert.False(archive.TryGetEntry("Geometry/BRep/occt-shapes.bin", out _));
            Assert.False(archive.TryGetEntry("Geometry/BRep/node-ids.json", out _));

            string manifestJson = await archive.ReadEntryAsTextAsync(FaArchiveManifest.ManifestEntryName, CancellationToken.None);
            JsonObject manifest = JsonNode.Parse(manifestJson)!.AsObject();
            Assert.Equal("1.1", (string?)manifest["schemaVersion"]);
            Assert.False(manifest.ContainsKey("brepEntryName"));
            Assert.Contains(
                manifest["files"]!.AsArray(),
                node => string.Equals((string?)node?["path"], FaViewerStateReader.CameraEntryName, StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                if (File.Exists(archivePath))
                    File.Delete(archivePath);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Fact]
    public async Task SaveAsync_WhenArchiveManifestPreserved_TouchesOnlyViewerEntries()
    {
        string archivePath = Path.Combine(Path.GetTempPath(), $"fa-android-cloud-save-{Guid.NewGuid():N}.fa");
        try
        {
            string originalManifestJson = CreateManifestJson();
            byte[] brepPayload = [1, 2, 3, 4];
            byte[] nodeMapPayload = Encoding.UTF8.GetBytes("""{"nodes":["same"]}""");
            WriteArchive(archivePath, new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [FaArchiveManifest.ManifestEntryName] = Encoding.UTF8.GetBytes(originalManifestJson),
                ["SmokeExport.glb"] = [10, 11, 12],
                ["SmokeExport.json"] = Encoding.UTF8.GetBytes("""{"components":[]}"""),
                ["Geometry/BRep/occt-shapes.bin"] = brepPayload,
                ["Geometry/BRep/node-ids.json"] = nodeMapPayload,
            });

            var camera = new CameraState
            {
                Position = new Vector3d(5, 6, 7),
                Target = new Vector3d(0, 0, 0),
                UpDirection = Vector3d.UnitZ,
                WorldUpDirection = Vector3d.UnitZ,
            };

            var service = new AndroidViewerStateSaveService();
            await service.SaveAsync(
                archivePath,
                new AndroidViewerStateSnapshot(
                    camera,
                    new PackageSessionState(),
                    new SectionService(),
                    Array.Empty<MeasurementResult>()),
                CancellationToken.None,
                preserveGeometryPayload: true,
                updateArchiveManifest: false);

            using FaArchive archive = FaArchive.Open(archivePath);
            Assert.Equal(
                originalManifestJson,
                await archive.ReadEntryAsTextAsync(FaArchiveManifest.ManifestEntryName, CancellationToken.None));
            Assert.True(archive.TryGetEntry(FaViewerStateReader.ManifestEntryName, out _));
            Assert.True(archive.TryGetEntry(FaViewerStateReader.CameraEntryName, out _));
            Assert.Equal(
                [10, 11, 12],
                await ReadEntryAsync(archive, "SmokeExport.glb", CancellationToken.None));
            Assert.Equal(
                Encoding.UTF8.GetBytes("""{"components":[]}"""),
                await ReadEntryAsync(archive, "SmokeExport.json", CancellationToken.None));
            Assert.True(archive.TryGetEntry("Geometry/BRep/occt-shapes.bin", out _));
            Assert.True(archive.TryGetEntry("Geometry/BRep/node-ids.json", out _));
            Assert.Equal(
                brepPayload,
                await ReadEntryAsync(archive, "Geometry/BRep/occt-shapes.bin", CancellationToken.None));
            Assert.Equal(
                nodeMapPayload,
                await ReadEntryAsync(archive, "Geometry/BRep/node-ids.json", CancellationToken.None));
        }
        finally
        {
            try
            {
                if (File.Exists(archivePath))
                    File.Delete(archivePath);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static string CreateManifestJson() =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            archiveType = "fa",
            exportRootName = "SmokeExport",
            brepEntryName = "Geometry/BRep/occt-shapes.bin",
            brepSchemaVersion = 1,
            occtMajor = 7,
            occtMinor = 9,
            brepUnit = "mm",
            files = new[]
            {
                new { path = "Geometry/BRep/occt-shapes.bin", kind = "brep", required = false },
                new { path = "Geometry/BRep/node-ids.json", kind = "brep-node-map", required = false },
            },
        });

    private static void WriteArchive(string archivePath, IReadOnlyDictionary<string, byte[]> entries)
    {
        using FileStream file = File.Create(archivePath);
        using var zip = new ZipOutputStream(file)
        {
            IsStreamOwner = false,
            Password = FaArchiveCredential.Password,
        };
        zip.SetLevel(0);

        foreach ((string name, byte[] payload) in entries)
        {
            var entry = new ZipEntry(name)
            {
                CompressionMethod = CompressionMethod.Stored,
                DateTime = DateTime.UtcNow,
                IsCrypted = true,
                Size = payload.Length,
            };
            zip.PutNextEntry(entry);
            zip.Write(payload, 0, payload.Length);
            zip.CloseEntry();
        }

        zip.Finish();
    }

    private static async Task<byte[]> ReadEntryAsync(FaArchive archive, string entryName, CancellationToken ct)
    {
        Assert.True(archive.TryGetEntry(entryName, out ZipEntry? entry));
        using Stream stream = archive.OpenEntryStream(entry!);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
