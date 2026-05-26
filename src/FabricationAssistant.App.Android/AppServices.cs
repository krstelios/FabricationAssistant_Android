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

        var services = new ServiceCollection();

        services.AddSingleton<Context>(applicationContext);
        services.AddSingleton<IDispatcher, AndroidDispatcher>();
        services.AddSingleton<IPlatformPaths>(_ => new AndroidPlatformPaths(applicationContext));
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

        var paths = provider.GetRequiredService<IPlatformPaths>();
        FabricationAssistantPaths.Configure(
            rootDirectory: paths.AppDataRoot,
            cacheDirectory: paths.CacheDir,
            tempDirectory: paths.TempDir,
            logsDirectory: paths.LogsDir);

        return provider;
    }
}
