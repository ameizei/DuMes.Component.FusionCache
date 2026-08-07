using DuMes.Component.FusionCache.DependencyInjection;
using DuMes.Component.FusionCache.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TestWorkerService.Shared;

/// <summary>根据配置构建带 FusionCache 组件的 <see cref="ServiceProvider"/>。</summary>
public static class FusionCacheTestHost
{
    public static ServiceProvider CreateProvider(
        IConfiguration configuration,
        Action<FusionCacheComponentOptions>? configureOptions = null,
        string? instanceName = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddConsole();
            b.SetMinimumLevel(LogLevel.Information);
        });

        services.AddComponentFusionCache(configuration, configureOptions);

        if (!string.IsNullOrWhiteSpace(instanceName))
            services.AddSingleton(new TestInstanceName(instanceName));

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    public static ServiceProvider CreateProvider(
        Action<FusionCacheComponentOptions> configureOptions,
        string? instanceName = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddConsole();
            b.SetMinimumLevel(LogLevel.Information);
        });

        services.AddComponentFusionCache(configureOptions);

        if (!string.IsNullOrWhiteSpace(instanceName))
            services.AddSingleton(new TestInstanceName(instanceName));

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }
}

/// <summary>用于区分多实例 Backplane 测试中的缓存实例。</summary>
public sealed record TestInstanceName(string Value);
