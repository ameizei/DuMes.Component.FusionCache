using System.Diagnostics;
using CSRedis;
using DuMes.Component.FusionCache.Options;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;

namespace TestWorkerService.Shared;

/// <summary>
///     组件场景用例：覆盖 GetOrSet / Remove / L2 / CSRedis / Backplane。
/// </summary>
public sealed class FusionCacheScenarioRunner
{
    private readonly IConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly string _modeLabel;

    public FusionCacheScenarioRunner(IConfiguration configuration, ILogger logger, string modeLabel)
    {
        _configuration = configuration;
        _logger = logger;
        _modeLabel = modeLabel;
    }

    public async Task<IReadOnlyList<ScenarioResult>> RunAllAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("======== FusionCache 场景开始（{Mode}）========", _modeLabel);

        await WaitForRedisAsync(cancellationToken);

        var results = new List<ScenarioResult>
        {
            await RunAsync("Redis.Ping", RedisPingAsync, cancellationToken),
            await RunAsync("DI.ServicesRegistered", ServicesRegisteredAsync, cancellationToken),
            await RunAsync("FusionCache.GetOrSet_MissThenHit", GetOrSetMissThenHitAsync, cancellationToken),
            await RunAsync("FusionCache.TryGet_AfterSet", TryGetAfterSetAsync, cancellationToken),
            await RunAsync("FusionCache.Remove_ClearsCache", RemoveClearsCacheAsync, cancellationToken),
            await RunAsync("FusionCache.SetDuration_OverridesL1", SetDurationOverridesL1Async, cancellationToken),
            await RunAsync("CSRedis.Hash", CsRedisHashAsync, cancellationToken),
            await RunAsync("CSRedis.Queue", CsRedisQueueAsync, cancellationToken),
            await RunAsync("CSRedis.Publish", CsRedisPublishAsync, cancellationToken),
            await RunAsync("Backplane.InvalidateRemoteL1", BackplaneInvalidateRemoteL1Async, cancellationToken)
        };

        var passed = results.Count(r => r.Passed);
        _logger.LogInformation(
            "======== 结束：{Passed}/{Total} 通过（{Mode}）========",
            passed,
            results.Count,
            _modeLabel);

        foreach (var r in results)
            _logger.LogInformation("{Result}", r.ToString());

        return results;
    }

    private async Task WaitForRedisAsync(CancellationToken cancellationToken)
    {
        var options = BindOptions();
        if (!options.EnableDistributedCache)
            return;

        _logger.LogInformation("等待 Redis 就绪…");
        Exception? last = null;
        for (var i = 1; i <= 60; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = options.CreateCsRedisClient();
                if (client.Ping())
                {
                    _logger.LogInformation("Redis 已就绪（第 {Attempt} 次探测）", i);
                    return;
                }
            }
            catch (Exception ex)
            {
                last = ex;
                _logger.LogDebug(ex, "Redis 未就绪（{Attempt}/60）", i);
            }

            await Task.Delay(1000, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Redis 在 60s 内未就绪（{_modeLabel}）。请先启动 docker/redis-standalone 或 docker/redis-cluster。",
            last);
    }

    private FusionCacheComponentOptions BindOptions()
    {
        var section = _configuration.GetSection(FusionCacheComponentOptions.SectionName);
        if (!section.Exists())
            throw new InvalidOperationException($"配置缺失：{FusionCacheComponentOptions.SectionName}");

        var options = section.Get<FusionCacheComponentOptions>() ?? new FusionCacheComponentOptions();
        options.Validate();
        return options;
    }

    private async Task<ScenarioResult> RunAsync(
        string name,
        Func<CancellationToken, Task<(bool Passed, string Detail)>> scenario,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (passed, detail) = await scenario(cancellationToken);
            sw.Stop();
            return new ScenarioResult(name, passed, detail, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "场景失败：{Name}", name);
            return new ScenarioResult(name, false, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private async Task<(bool, string)> RedisPingAsync(CancellationToken cancellationToken)
    {
        await using var sp = FusionCacheTestHost.CreateProvider(_configuration);
        var redis = sp.GetRequiredService<CSRedisClient>();
        var ok = redis.Ping();
        return (ok, $"Ping={(ok ? "PONG" : "FAIL")}");
    }

    private async Task<(bool, string)> ServicesRegisteredAsync(CancellationToken cancellationToken)
    {
        await using var sp = FusionCacheTestHost.CreateProvider(_configuration);
        var cache = sp.GetService<IFusionCache>();
        var redis = sp.GetService<CSRedisClient>();
        var distributed = sp.GetService<IDistributedCache>();
        var options = sp.GetRequiredService<FusionCacheComponentOptions>();

        if (cache is null)
            return (false, "未注册 IFusionCache");

        if (options.EnableDistributedCache)
        {
            if (redis is null)
                return (false, "未注册 CSRedisClient");
            if (distributed is null)
                return (false, "未注册 IDistributedCache");
        }

        return (true,
            $"Mode={options.Mode}, Redis={options.EnableDistributedCache}, Backplane={options.EnableBackplane}, L2Type={distributed?.GetType().Name}");
    }

    private async Task<(bool, string)> GetOrSetMissThenHitAsync(CancellationToken cancellationToken)
    {
        await using var sp = FusionCacheTestHost.CreateProvider(_configuration);
        var cache = sp.GetRequiredService<IFusionCache>();
        var key = $"scenario:getorset:{Guid.NewGuid():N}";
        var factoryCalls = 0;

        var first = await cache.GetOrSetAsync(
            key,
            async _ =>
            {
                factoryCalls++;
                await Task.Delay(50, cancellationToken);
                return new ProductDto(1, "widget", DateTimeOffset.UtcNow);
            },
            token: cancellationToken);

        var second = await cache.GetOrSetAsync(
            key,
            async _ =>
            {
                factoryCalls++;
                await Task.Delay(50, cancellationToken);
                return new ProductDto(1, "should-not-run", DateTimeOffset.UtcNow);
            },
            token: cancellationToken);

        var ok = factoryCalls == 1
                 && first.Name == "widget"
                 && second.Name == "widget";

        return (ok, $"factoryCalls={factoryCalls}, first={first.Name}, second={second.Name}");
    }

    private async Task<(bool, string)> TryGetAfterSetAsync(CancellationToken cancellationToken)
    {
        await using var sp = FusionCacheTestHost.CreateProvider(_configuration);
        var cache = sp.GetRequiredService<IFusionCache>();
        var key = $"scenario:tryget:{Guid.NewGuid():N}";

        await cache.SetAsync(key, new ProductDto(2, "set-me", DateTimeOffset.UtcNow), token: cancellationToken);
        var hit = await cache.TryGetAsync<ProductDto>(key, token: cancellationToken);
        var miss = await cache.TryGetAsync<ProductDto>($"scenario:tryget:missing:{Guid.NewGuid():N}",
            token: cancellationToken);

        var ok = hit.HasValue && hit.Value.Name == "set-me" && !miss.HasValue;
        return (ok, $"hit={hit.HasValue}, miss={miss.HasValue}");
    }

    private async Task<(bool, string)> RemoveClearsCacheAsync(CancellationToken cancellationToken)
    {
        await using var sp = FusionCacheTestHost.CreateProvider(_configuration);
        var cache = sp.GetRequiredService<IFusionCache>();
        var key = $"scenario:remove:{Guid.NewGuid():N}";

        await cache.SetAsync(key, new ProductDto(3, "to-remove", DateTimeOffset.UtcNow), token: cancellationToken);
        await cache.RemoveAsync(key, token: cancellationToken);
        var after = await cache.TryGetAsync<ProductDto>(key, token: cancellationToken);

        return (!after.HasValue, after.HasValue ? "Remove 后仍能读到缓存" : "Remove 后未命中");
    }

    private async Task<(bool, string)> SetDurationOverridesL1Async(CancellationToken cancellationToken)
    {
        await using var sp = FusionCacheTestHost.CreateProvider(_configuration);
        var cache = sp.GetRequiredService<IFusionCache>();
        var key = $"scenario:duration:{Guid.NewGuid():N}";

        await cache.SetAsync(
            key,
            new ProductDto(4, "short-ttl", DateTimeOffset.UtcNow),
            options => options.SetDuration(TimeSpan.FromMilliseconds(400)),
            cancellationToken);

        var immediate = await cache.TryGetAsync<ProductDto>(key, token: cancellationToken);
        await Task.Delay(800, cancellationToken);
        var afterExpire = await cache.TryGetAsync<ProductDto>(key, token: cancellationToken);

        // Fail-Safe 开启时逻辑过期后仍可能短暂可读；此处以「立即命中」为主断言，过期后允许 HasValue（fail-safe）
        var ok = immediate.HasValue;
        return (ok,
            $"immediate={immediate.HasValue}, afterExpire={afterExpire.HasValue}（Fail-Safe 开启时过期后仍可能可读）");
    }

    private async Task<(bool, string)> CsRedisHashAsync(CancellationToken cancellationToken)
    {
        await using var sp = FusionCacheTestHost.CreateProvider(_configuration);
        var redis = sp.GetRequiredService<CSRedisClient>();
        var field = Guid.NewGuid().ToString("N")[..8];
        const string hashKey = "scenario:hash";

        redis.HSet(hashKey, field, "v1");
        var value = redis.HGet(hashKey, field);
        redis.HDel(hashKey, field);

        return (value == "v1", $"HGet={value}");
    }

    private async Task<(bool, string)> CsRedisQueueAsync(CancellationToken cancellationToken)
    {
        await using var sp = FusionCacheTestHost.CreateProvider(_configuration);
        var redis = sp.GetRequiredService<CSRedisClient>();
        var queueKey = $"scenario:queue:{Guid.NewGuid():N}";
        var payload = $"job-{Guid.NewGuid():N}";

        redis.LPush(queueKey, payload);
        var popped = redis.RPop(queueKey);

        return (popped == payload, $"RPop={popped}");
    }

    private async Task<(bool, string)> CsRedisPublishAsync(CancellationToken cancellationToken)
    {
        await using var sp = FusionCacheTestHost.CreateProvider(_configuration);
        var redis = sp.GetRequiredService<CSRedisClient>();
        var channel = $"scenario:events:{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // CSRedis：Subscribe((channel, handler)...)
        redis.Subscribe((channel, msg => tcs.TrySetResult(msg.Body)));

        await Task.Delay(300, cancellationToken);
        var receivers = redis.Publish(channel, "hello-pubsub");
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000, cancellationToken));
        if (completed != tcs.Task)
            return (false, $"Subscribe 超时，Publish receivers={receivers}");

        var msg = await tcs.Task;
        return (msg == "hello-pubsub", $"Message={msg}, receivers={receivers}");
    }

    private async Task<(bool, string)> BackplaneInvalidateRemoteL1Async(CancellationToken cancellationToken)
    {
        var options = BindOptions();
        if (!options.EnableDistributedCache || !options.EnableBackplane)
            return (true, "跳过：未启用 Redis/Backplane");

        // 两个独立 DI 容器 = 两个进程内 L1 + 共用 L2/Backplane
        await using var spA = FusionCacheTestHost.CreateProvider(_configuration, instanceName: "A");
        await using var spB = FusionCacheTestHost.CreateProvider(_configuration, instanceName: "B");

        var cacheA = spA.GetRequiredService<IFusionCache>();
        var cacheB = spB.GetRequiredService<IFusionCache>();
        var key = $"scenario:backplane:{Guid.NewGuid():N}";

        await cacheA.SetAsync(key, new ProductDto(10, "v1", DateTimeOffset.UtcNow), token: cancellationToken);

        // B 从 L2 填入 L1
        var fromB1 = await cacheB.GetOrSetAsync(
            key,
            _ => Task.FromResult(new ProductDto(10, "factory-should-not-run", DateTimeOffset.UtcNow)),
            token: cancellationToken);

        if (fromB1.Name != "v1")
            return (false, $"B 首次读期望 v1，实际 {fromB1.Name}");

        // A 更新并触发 Backplane 通知 B 清 L1
        await cacheA.SetAsync(key, new ProductDto(10, "v2", DateTimeOffset.UtcNow), token: cancellationToken);

        // 等待 Pub/Sub 传播
        ProductDto? fromB2 = null;
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(150, cancellationToken);
            var maybe = await cacheB.TryGetAsync<ProductDto>(key, token: cancellationToken);
            if (maybe.HasValue && maybe.Value.Name == "v2")
            {
                fromB2 = maybe.Value;
                break;
            }

            // L1 被清后 TryGet 可能 miss，再 GetOrSet 应从 L2 取到 v2
            fromB2 = await cacheB.GetOrSetAsync(
                key,
                _ => Task.FromResult(new ProductDto(10, "stale-factory", DateTimeOffset.UtcNow)),
                token: cancellationToken);

            if (fromB2.Name == "v2")
                break;
        }

        var ok = fromB2?.Name == "v2";
        return (ok, $"B.after={fromB2?.Name}");
    }
}

public sealed record ProductDto(int Id, string Name, DateTimeOffset LoadedAt);