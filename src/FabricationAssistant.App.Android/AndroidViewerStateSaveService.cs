using System.Text.Json;
using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Sections;
using FabricationAssistant.Core.Selection;
using FabricationAssistant.Import.Fa;
using FabricationAssistant.Import.Fa.FaViewerState;

namespace FabricationAssistant.App.Android;

internal sealed class AndroidViewerStateSaveService
{
    private const string BRepBinaryEntryName = "Geometry/BRep/occt-shapes.bin";
    private const string BRepNodeIdsEntryName = "Geometry/BRep/node-ids.json";

    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public async Task SaveAsync(
        string archivePath,
        AndroidViewerStateSnapshot snapshot,
        CancellationToken ct,
        bool preserveGeometryPayload = false,
        bool updateArchiveManifest = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(snapshot);

        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string manifestJson;
            using (FaArchive archive = FaArchive.Open(archivePath))
                manifestJson = await archive.ReadEntryAsTextAsync(FaArchiveManifest.ManifestEntryName, ct).ConfigureAwait(false);

            string viewerManifestSource = updateArchiveManifest
                ? FaArchiveManifestMutator.BumpAndAddViewerEntries(manifestJson)
                : manifestJson;
            ViewerManifestDto viewerManifest = BuildViewerManifest(viewerManifestSource);

            using FaArchiveWriter writer = FaArchiveWriter.ForRewrite(archivePath);
            if (updateArchiveManifest)
                writer.AddOrReplaceTextEntry(FaArchiveManifest.ManifestEntryName, viewerManifestSource);
            writer.AddOrReplaceTextEntry(
                FaViewerStateReader.ManifestEntryName,
                JsonSerializer.Serialize(viewerManifest, ViewerJsonOptions.Pretty));
            writer.AddOrReplaceTextEntry(
                FaViewerStateReader.CameraEntryName,
                JsonSerializer.Serialize(BuildCameraDto(snapshot.Camera), ViewerJsonOptions.Pretty));
            writer.AddOrReplaceTextEntry(
                FaViewerStateReader.VisibilityEntryName,
                JsonSerializer.Serialize(BuildVisibilityDto(snapshot.PackageSession), ViewerJsonOptions.Pretty));
            writer.AddOrReplaceTextEntry(
                FaViewerStateReader.SectionsEntryName,
                JsonSerializer.Serialize(BuildSectionsDto(snapshot.Sections), ViewerJsonOptions.Pretty));
            writer.AddOrReplaceTextEntry(
                FaViewerStateReader.MeasurementsEntryName,
                JsonSerializer.Serialize(BuildMeasurementsDto(snapshot.Measurements), ViewerJsonOptions.Pretty));

            // Android does not own markup editing yet. Preserve any existing
            // Viewer/Markups.json and snapshot entries by not touching them.
            if (!preserveGeometryPayload)
            {
                writer.RemoveEntriesByPrefix(BRepBinaryEntryName);
                writer.RemoveEntriesByPrefix(BRepNodeIdsEntryName);
                string finalManifest = FaArchiveManifestMutator.SetBRepFields(
                    viewerManifestSource,
                    brepEntryName: null,
                    brepSchemaVersion: null,
                    occtMajor: null,
                    occtMinor: null,
                    brepUnit: null);
                writer.AddOrReplaceTextEntry(FaArchiveManifest.ManifestEntryName, finalManifest);
            }

            await writer.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static ViewerManifestDto BuildViewerManifest(string archiveManifestJson)
    {
        string exportRootName = string.Empty;
        try
        {
            FaArchiveManifest archiveManifest = FaArchiveManifest.Parse(archiveManifestJson);
            exportRootName = archiveManifest.ExportRootName;
        }
        catch
        {
            // Keep a valid viewer manifest even if an older archive manifest
            // has a field the current parser does not understand.
        }

        return new ViewerManifestDto
        {
            SchemaVersion = "1.0",
            SavedUtc = DateTimeOffset.UtcNow.ToString("O"),
            AppVersion = typeof(AndroidViewerStateSaveService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ExportRootName = exportRootName,
            Files = new()
            {
                new() { Path = FaViewerStateReader.ManifestEntryName, Kind = "viewer-manifest" },
                new() { Path = FaViewerStateReader.MarkupsEntryName, Kind = "viewer-markups" },
                new() { Path = FaViewerStateReader.CameraEntryName, Kind = "viewer-camera" },
                new() { Path = FaViewerStateReader.VisibilityEntryName, Kind = "viewer-visibility" },
                new() { Path = FaViewerStateReader.SectionsEntryName, Kind = "viewer-sections" },
                new() { Path = FaViewerStateReader.MeasurementsEntryName, Kind = "viewer-measurements" },
                new() { Path = FaViewerStateReader.SnapshotPrefix, Kind = "viewer-snapshot-folder" },
            },
        };
    }

    private static ViewerCameraDto BuildCameraDto(CameraState camera)
    {
        lock (camera)
        {
            return new ViewerCameraDto
            {
                SchemaVersion = "1.0",
                Position = [camera.Position.X, camera.Position.Y, camera.Position.Z],
                Target = [camera.Target.X, camera.Target.Y, camera.Target.Z],
                Up = [camera.UpDirection.X, camera.UpDirection.Y, camera.UpDirection.Z],
                WorldUp = [camera.WorldUpDirection.X, camera.WorldUpDirection.Y, camera.WorldUpDirection.Z],
                FieldOfViewRadians = camera.FieldOfView,
                Near = camera.NearPlane,
                Far = camera.FarPlane,
                IsPerspective = camera.IsPerspective,
                OrthoWidth = camera.OrthoWidth,
            };
        }
    }

    private static ViewerVisibilityDto BuildVisibilityDto(PackageSessionState packageSession) => new()
    {
        SchemaVersion = "1.0",
        HiddenOccurrenceIds = packageSession.HiddenOccurrenceIds.OrderBy(s => s, StringComparer.Ordinal).ToList(),
        IsolatedOccurrenceIds = packageSession.IsolatedOccurrenceIds.OrderBy(s => s, StringComparer.Ordinal).ToList(),
        SelectedOccurrenceIds = packageSession.SelectedOccurrenceIds.OrderBy(s => s, StringComparer.Ordinal).ToList(),
        ExpandedNodeIds = packageSession.ExpandedPresentedNodeIds.OrderBy(s => s, StringComparer.Ordinal).ToList(),
    };

    private static ViewerSectionsDto BuildSectionsDto(SectionService sections) => new()
    {
        SchemaVersion = "1.0",
        Planes = sections.Planes.Select(p => new ViewerSectionPlaneDto
        {
            Id = p.Id,
            Anchor = [(float)p.Anchor.X, (float)p.Anchor.Y, (float)p.Anchor.Z],
            AxisX = [(float)p.AxisX.X, (float)p.AxisX.Y, (float)p.AxisX.Z],
            AxisY = [(float)p.AxisY.X, (float)p.AxisY.Y, (float)p.AxisY.Z],
            Normal = [(float)p.Normal.X, (float)p.Normal.Y, (float)p.Normal.Z],
        }).ToList(),
        FillVisible = sections.FillVisible,
        EdgesVisible = sections.EdgesVisible,
    };

    private static ViewerMeasurementsDto BuildMeasurementsDto(IReadOnlyList<MeasurementResult> measurements) => new()
    {
        SchemaVersion = "1.0",
        Items = measurements.Select(MeasurementToDto).ToList(),
    };

    private static ViewerMeasurementDto MeasurementToDto(MeasurementResult m)
    {
        var dto = new ViewerMeasurementDto
        {
            Id = m.Id.Value,
            IsVisible = m.IsVisible,
        };

        switch (m)
        {
            case AnnotationMeasurement a:
                dto.Kind = "annotation";
                dto.Anchor = [a.Anchor.X, a.Anchor.Y, a.Anchor.Z];
                dto.Text = a.Text;
                dto.OccurrenceId = a.OccurrenceId;
                dto.Color = a.Color;
                dto.Author = a.Author;
                dto.CreatedUtc = a.CreatedUtc == default ? null : a.CreatedUtc.ToString("O");
                dto.UpdatedUtc = a.UpdatedUtc == default ? null : a.UpdatedUtc.ToString("O");
                return dto;

            case PointToPointMeasurement p:
                dto.Kind = "point-to-point";
                dto.Points =
                [
                    [p.A.World.X, p.A.World.Y, p.A.World.Z],
                    [p.B.World.X, p.B.World.Y, p.B.World.Z],
                ];
                dto.Distance = p.Distance.Meters;
                dto.DistanceUnits = "m";
                return dto;

            case FaceToPointMeasurement fp:
                dto.Kind = "face-to-point";
                dto.Points = [[fp.Point.World.X, fp.Point.World.Y, fp.Point.World.Z]];
                dto.Distance = fp.Distance.Meters;
                dto.DistanceUnits = "m";
                return dto;

            case FaceToFaceMeasurement ff:
                dto.Kind = "face-to-face";
                dto.Points =
                [
                    [ff.AnchorA.X, ff.AnchorA.Y, ff.AnchorA.Z],
                    [ff.AnchorB.X, ff.AnchorB.Y, ff.AnchorB.Z],
                ];
                dto.Distance = ff.Distance.Meters;
                dto.DistanceUnits = "m";
                dto.ParallelWithinTolerance = ff.ParallelWithinTolerance;
                return dto;

            case BoundingBoxMeasurement b:
                dto.Kind = "bounding-box";
                dto.BoxOrigin = [b.Box.Center.X, b.Box.Center.Y, b.Box.Center.Z];
                dto.BoxAxisX = [b.Box.AxisX.X, b.Box.AxisX.Y, b.Box.AxisX.Z];
                dto.BoxAxisY = [b.Box.AxisY.X, b.Box.AxisY.Y, b.Box.AxisY.Z];
                dto.BoxAxisZ = [b.Box.AxisZ.X, b.Box.AxisZ.Y, b.Box.AxisZ.Z];
                dto.BoxLengthX = b.XLength.Meters;
                dto.BoxLengthY = b.YLength.Meters;
                dto.BoxLengthZ = b.ZLength.Meters;
                dto.BoxBasisSource = b.BasisSource.ToString();
                return dto;

            default:
                dto.Kind = "unknown";
                return dto;
        }
    }
}

internal sealed record AndroidViewerStateSnapshot(
    CameraState Camera,
    PackageSessionState PackageSession,
    SectionService Sections,
    IReadOnlyList<MeasurementResult> Measurements);
