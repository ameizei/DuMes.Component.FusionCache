using System.Diagnostics;
using CSRedis;
using DuMes.Component.Serilog.Logging;
using Microsoft.Extensions.Caching.Distributed;
using ZiggyCreatures.Caching.Fusion;

namespace TestWebApi.Endpoints;

/// <summary>
///     FusionCache + CSRedis 演示接口。
/// </summary>
public static class FusionCacheEndpoints
{
    private const string DemoHashKey = "demo:product";
    private const string DemoQueueKey = "demo:queue";
    private const string DemoChannel = "demo:events";

    public static IEndpointRouteBuilder MapFusionCacheEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/cache").WithTags("FusionCache");

        group.MapGet("/", () => Results.Ok(new
        {
            message = "DuMes.Component.FusionCache TestWebApi",
            endpoints = new[]
            {
                "GET  /cache/product/{id}          — GetOrSet（L1+L2，模拟读库）",
                "DELETE /cache/product/{id}        — 删除缓存",
                "GET  /cache/product/{id}/raw      — 仅查缓存（不回源）",
                "POST /cache/redis/hash            — Hash 写入",
                "GET  /cache/redis/hash/{field}    — Hash 读取",
                "POST /cache/redis/queue           — List 入队",
                "GET  /cache/redis/queue           — List 出队",
                "POST /cache/redis/publish         — Pub/Sub 发布",
                "GET  /cache/redis/ping            — Redis PING"
            }
        }));

        group.MapGet("/product/{id:int}", GetOrSetProductAsync);
        group.MapDelete("/product/{id:int}", RemoveProductAsync);
        group.MapGet("/product/{id:int}/raw", TryGetProductAsync);

        group.MapPost("/redis/hash", SetHashAsync);
        group.MapGet("/redis/hash/{field}", GetHashAsync);
        group.MapPost("/redis/queue", PushQueueAsync);
        group.MapGet("/redis/queue", PopQueueAsync);
        group.MapPost("/redis/publish", PublishAsync);
        group.MapGet("/redis/ping", PingAsync);

        return app;
    }

    private static async Task<IResult> GetOrSetProductAsync(
        int id,
        IFusionCache cache,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("FusionCacheDemo");
        var sw = Stopwatch.StartNew();
        var factoryCalls = 0;

        var product = await cache.GetOrSetAsync(
            $"product:{id}",
            async _ =>
            {
                factoryCalls++;
                // 模拟数据库慢查询
                await Task.Delay(200, cancellationToken);
                return new ProductDto(id, $"Product-{id}", DateTimeOffset.Now);
            },
            options => options.SetDuration(TimeSpan.FromSeconds(60)),
            token: cancellationToken);

        sw.Stop();
        logger.WriteInformation(
            "cache",
            "GetOrSet product:{Id} factoryCalls={FactoryCalls} elapsedMs={ElapsedMs}",
            args: [id, factoryCalls, sw.ElapsedMilliseconds]);

        return Results.Ok(new
        {
            product,
            fromFactory = factoryCalls > 0,
            elapsedMs = sw.ElapsedMilliseconds,
            tip = factoryCalls > 0
                ? "本次回源（模拟 DB），结果已写入 L1+L2"
                : "本次命中缓存（L1 或 L2），未回源"
        });
    }

    private static async Task<IResult> RemoveProductAsync(
        int id,
        IFusionCache cache,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("FusionCacheDemo");
        await cache.RemoveAsync($"product:{id}", token: cancellationToken);
        logger.WriteInformation("cache", "Remove product:{Id}", args: [id]);
        return Results.Ok(new { removed = true, key = $"product:{id}" });
    }

    private static async Task<IResult> TryGetProductAsync(
        int id,
        IFusionCache cache,
        CancellationToken cancellationToken)
    {
        var product = await cache.TryGetAsync<ProductDto>($"product:{id}", token: cancellationToken);
        if (!product.HasValue)
            return Results.NotFound(new { key = $"product:{id}", message = "缓存未命中" });

        return Results.Ok(product.Value);
    }

    private static IResult SetHashAsync(HashSetRequest request, CSRedisClient redis, ILoggerFactory loggerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Field);
        redis.HSet(DemoHashKey, request.Field, request.Value ?? string.Empty);
        loggerFactory.CreateLogger("FusionCacheDemo")
            .WriteInformation("redis", "HSET {Key} {Field}", args: [DemoHashKey, request.Field]);
        return Results.Ok(new { key = DemoHashKey, request.Field, request.Value });
    }

    private static IResult GetHashAsync(string field, CSRedisClient redis)
    {
        var value = redis.HGet(DemoHashKey, field);
        return value is null
            ? Results.NotFound(new { key = DemoHashKey, field })
            : Results.Ok(new { key = DemoHashKey, field, value });
    }

    private static IResult PushQueueAsync(QueuePushRequest request, CSRedisClient redis, ILoggerFactory loggerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);
        var len = redis.LPush(DemoQueueKey, request.Message);
        loggerFactory.CreateLogger("FusionCacheDemo")
            .WriteInformation("redis", "LPUSH {Key} len={Len}", args: [DemoQueueKey, len]);
        return Results.Ok(new { key = DemoQueueKey, length = len, request.Message });
    }

    private static IResult PopQueueAsync(CSRedisClient redis)
    {
        var message = redis.RPop(DemoQueueKey);
        return message is null
            ? Results.Ok(new { key = DemoQueueKey, message = (string?)null, tip = "队列为空" })
            : Results.Ok(new { key = DemoQueueKey, message });
    }

    private static IResult PublishAsync(PublishRequest request, CSRedisClient redis, ILoggerFactory loggerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);
        var receivers = redis.Publish(DemoChannel, request.Message);
        loggerFactory.CreateLogger("FusionCacheDemo")
            .WriteInformation("redis", "PUBLISH {Channel} receivers={Receivers}", args: [DemoChannel, receivers]);
        return Results.Ok(new { channel = DemoChannel, request.Message, receivers });
    }

    private static IResult PingAsync(CSRedisClient redis, IDistributedCache distributedCache)
    {
        var pong = redis.Ping();
        return Results.Ok(new
        {
            ping = pong,
            distributedCacheType = distributedCache.GetType().FullName,
            tip = "CSRedisClient 与 IDistributedCache（FusionCache L2）共用同一 Redis 连接"
        });
    }
}

public sealed record ProductDto(int Id, string Name, DateTimeOffset LoadedAt);

public sealed record HashSetRequest(string Field, string? Value);

public sealed record QueuePushRequest(string Message);

public sealed record PublishRequest(string Message);
