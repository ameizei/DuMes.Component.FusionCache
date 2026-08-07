using CSRedis;
using DuMes.Component.FusionCache.Options;
using DuMes.Component.FusionCache.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace DuMes.Component.FusionCache.DependencyInjection;

/// <summary>
///     服务集合扩展：注册 FusionCache（L1）+ 可选 Redis（CSRedis L2 / Backplane / CSRedisClient）。
/// </summary>
public static class FusionCacheServiceCollectionExtensions
{
    /// <summary>
    ///     从配置节 <c>FusionCache</c> 注册缓存组件。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置根；读取 <see cref="FusionCacheComponentOptions.SectionName"/>。</param>
    /// <param name="configureOptions">可选，覆盖配置项。</param>
    /// <param name="configureCache">可选，进一步配置 <see cref="IFusionCacheBuilder"/>。</param>
    public static IServiceCollection AddComponentFusionCache(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<FusionCacheComponentOptions>? configureOptions = null,
        Action<IFusionCacheBuilder>? configureCache = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(FusionCacheComponentOptions.SectionName);
        if (!section.Exists())
            throw new InvalidOperationException($"配置缺失：{FusionCacheComponentOptions.SectionName}");

        var options = section.Get<FusionCacheComponentOptions>() ?? new FusionCacheComponentOptions();
        configureOptions?.Invoke(options);

        return Register(services, options, configureCache, section);
    }

    /// <summary>
    ///     仅用代码配置注册（无 appsettings 配置节）。
    /// </summary>
    public static IServiceCollection AddComponentFusionCache(
        this IServiceCollection services,
        Action<FusionCacheComponentOptions> configureOptions,
        Action<IFusionCacheBuilder>? configureCache = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new FusionCacheComponentOptions();
        configureOptions(options);

        return Register(services, options, configureCache, configurationSection: null);
    }

    private static IServiceCollection Register(
        IServiceCollection services,
        FusionCacheComponentOptions options,
        Action<IFusionCacheBuilder>? configureCache,
        IConfigurationSection? configurationSection)
    {
        options.Validate();

        services.AddSingleton(options);
        if (configurationSection is not null)
            services.Configure<FusionCacheComponentOptions>(configurationSection);

        var useRedis = options.EnableDistributedCache;
        // Backplane 依赖 Redis Pub/Sub；关闭 Redis 时一并禁用
        var useBackplane = useRedis && options.EnableBackplane;

        if (useRedis)
        {
            var redis = options.CreateCSRedisClient();
            RedisHelper.Initialization(redis);

            services.AddSingleton(redis);
            services.AddSingleton<IDistributedCache>(new CSRedisCache(redis));
        }

        IConnectionMultiplexer? backplaneMuxer = null;
        if (useBackplane)
        {
            backplaneMuxer = ConnectionMultiplexer.Connect(options.BuildStackExchangeConfiguration());
            services.AddSingleton(backplaneMuxer);
        }

        var l1 = options.GetL1Duration();
        var l2 = options.GetL2Duration();

        var builder = services.AddFusionCache()
            .WithOptions(opt =>
            {
                opt.DistributedCacheCircuitBreakerDuration = TimeSpan.FromSeconds(30);
                if (useBackplane)
                    opt.BackplaneChannelPrefix = options.KeyPrefix.Trim();
            })
            .WithDefaultEntryOptions(entry =>
            {
                entry.Duration = l1;
                entry.MemoryCacheDuration = l1;
                if (useRedis)
                    entry.DistributedCacheDuration = l2;
                entry.IsFailSafeEnabled = options.IsFailSafeEnabled;
                entry.FailSafeMaxDuration = options.GetFailSafeMaxDuration();
            });

        if (useRedis)
        {
            builder.WithSerializer(new FusionCacheSystemTextJsonSerializer(FusionCacheJsonOptions.JsonStringOptions));
            builder.WithRegisteredDistributedCache();
        }

        if (useBackplane && backplaneMuxer is not null)
        {
            var muxer = backplaneMuxer;
            builder.WithBackplane(new RedisBackplane(new RedisBackplaneOptions
            {
                ConnectionMultiplexerFactory = () => Task.FromResult(muxer)
            }));
        }

        configureCache?.Invoke(builder);

        return services;
    }
}
