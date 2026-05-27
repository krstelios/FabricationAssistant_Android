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
            cacheDirectory: platformPaths.CacheDir,
            tempDirectory: platformPaths.TempDir,
            logsDirectory: platformPaths.LogsDir);
        _ = Task.Run(() => ImportPipeline.PruneImportCache(platformPaths.AppDataRoot));

        var services = new ServiceCollection();

        services.AddSingleton<Context>(applicationContext);
        services.AddSingleton<IDispatcher, AndroidDispatcher>();
        services.AddSingleton<IPlatformPaths>(platformPaths);
        services.AddSingleton<ImportPipeline>();
        services.AddSingleton<CameraState>();
        services.AddSingleton<SectionService>();
        services.AddSingleton<PackageSessionState>();
        services.AddSingleton<FaPackageQueryService>();

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return provider;
    }
}
