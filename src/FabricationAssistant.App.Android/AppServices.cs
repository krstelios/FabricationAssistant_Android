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
        _ = Task.Run(() => ImportPipeline.PruneImportCache(platformPaths.AppDataRoot))
            .ContinueWith(
                task => global::Android.Util.Log.Warn("FA.Cache", task.Exception?.ToString() ?? "Import cache prune failed."),
                TaskContinuationOptions.OnlyOnFaulted);

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
