using Android.Content;
using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Runtime;
using FabricationAssistant.Core.Sections;
using FabricationAssistant.Core.Selection;
using FabricationAssistant.Import.Fa;
using FabricationAssistant.Platform;
using FabricationAssistant.Platform.Android;
using Microsoft.Extensions.DependencyInjection;

namespace FabricationAssistant.App.Android;

public static class AppServices
{
    // S1-4: gate the fire-and-forget import-cache prune so two rapid create/destroy
    // cycles cannot overlap prunes. 0 = idle, 1 = a prune is running.
    private static int _importCachePruneInProgress;

    public static IServiceProvider Build(Context applicationContext)
    {
        ArgumentNullException.ThrowIfNull(applicationContext);

        var platformPaths = new AndroidPlatformPaths(applicationContext);
        FabricationAssistantPaths.Configure(
            rootDirectory: platformPaths.AppDataRoot,
            // S4#2: give the FA package cache its own subdirectory rather than
            // bare CacheDir, which has temp/ and logs/ as siblings. FaPackageCacheStore
            // enumerates the child directories of its cache root as candidate
            // package caches, so rooting it here keeps temp/ and logs/ out of
            // that scan (and out of any future retention cleanup) and keeps
            // retention size accounting correct. On Android the package store is
            // the only consumer of CacheDirectory, and the path-constraint shim
            // rejects pointing FaPackageStorageOptions.CacheRootPath at a CacheDir
            // subdirectory directly (it pins paths under RootDirectory/FilesDir),
            // so scoping it here is the safe equivalent. temp/ and logs/ stay
            // under bare CacheDir via the explicit params below.
            cacheDirectory: Path.Combine(platformPaths.CacheDir, "fa-package-cache"),
            tempDirectory: platformPaths.TempDir,
            logsDirectory: platformPaths.LogsDir);
        // S1-4: only start a prune when one is not already running. The prune touches
        // only the filesystem (no Activity references), so a fire-and-forget Task stays
        // safe; the gate just stops overlapping launches from pruning concurrently.
        if (Interlocked.CompareExchange(ref _importCachePruneInProgress, 1, 0) == 0)
        {
            string appDataRoot = platformPaths.AppDataRoot;
            _ = Task.Run(() =>
            {
                try
                {
                    ImportPipeline.PruneImportCache(appDataRoot);
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("FA.Cache", ex.ToString());
                }
                finally
                {
                    Interlocked.Exchange(ref _importCachePruneInProgress, 0);
                }
            });
        }

        var services = new ServiceCollection();

        services.AddSingleton<Context>(applicationContext);
        services.AddSingleton<IDispatcher, AndroidDispatcher>();
        services.AddSingleton<IPlatformPaths>(platformPaths);
        services.AddSingleton<ImportPipeline>();
        services.AddSingleton<CloudSecureStore>();
        services.AddSingleton<CloudApiClient>();
        services.AddSingleton<CloudNotificationClient>();
        services.AddSingleton<CameraState>();
        services.AddSingleton<SectionService>();
        services.AddSingleton<PackageSessionState>();
        services.AddSingleton<FaPackageQueryService>();
        // BodyMoveService, UndoService, and AndroidMeasureIntegration are Activity-owned because they capture the current runtime scene.

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return provider;
    }
}
