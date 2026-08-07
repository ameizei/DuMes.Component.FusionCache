using CSRedis;
using StackExchange.Redis;

namespace DuMes.Component.FusionCache.Options;

/// <summary>
///     FusionCache 组件配置项（配置节名 <see cref="SectionName"/>）。
/// </summary>
public sealed class FusionCacheComponentOptions
{
    /// <summary>配置节名称：<c>FusionCache</c>。</summary>
    public const string SectionName = "FusionCache";

    /// <summary>
    ///     Redis 部署模式。默认 <see cref="RedisMode.Standalone"/>。
    ///     <see cref="RedisMode.Cluster"/> 时使用 <see cref="EndPoints"/>；单机时使用 <see cref="Host"/> + <see cref="Port"/>。
    /// </summary>
    public RedisMode Mode { get; set; } = RedisMode.Standalone;

    /// <summary>单机模式：Redis 主机。默认 <c>127.0.0.1</c>。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>单机模式：Redis 端口。默认 <c>6379</c>。</summary>
    public int Port { get; set; } = 6379;

    /// <summary>
    ///     集群模式：节点列表，每项格式为 <c>host:port</c>（可只写部分种子节点，建议写全主节点）。
    ///     <see cref="Mode"/> 为 <see cref="RedisMode.Cluster"/> 时必填。
    /// </summary>
    public string[] EndPoints { get; set; } = [];

    /// <summary>Redis 密码。可为空。</summary>
    public string? Password { get; set; }

    /// <summary>
    ///     默认数据库编号。默认 <c>0</c>。
    ///     集群模式仅支持 <c>0</c>。
    /// </summary>
    public int DefaultDatabase { get; set; }

    /// <summary>
    ///     Key 前缀，用于隔离不同应用/环境的缓存与业务 Redis 数据。
    ///     启用 Redis 时必填（经 CSRedis <c>prefix=</c> 自动加到所有 key）；同时用作 Backplane 频道前缀。
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>CSRedis 连接池大小。默认 <c>50</c>。</summary>
    public int PoolSize { get; set; } = 50;

    /// <summary>
    ///     是否启用 Redis（L2 分布式缓存、CSRedisClient、以及可选的 Backplane）。
    ///     为 <c>false</c> 时完全不连接 Redis，仅使用进程内 L1 内存缓存。
    /// </summary>
    public bool EnableDistributedCache { get; set; } = true;

    /// <summary>
    ///     是否启用 FusionCache Backplane（StackExchange.Redis Pub/Sub），用于多实例间即时同步 L1 失效。
    ///     仅在 <see cref="EnableDistributedCache"/> 为 <c>true</c> 时生效；关闭 Redis 时自动忽略。
    ///     默认 <c>true</c>。
    /// </summary>
    public bool EnableBackplane { get; set; } = true;

    /// <summary>
    ///     L1（内存）默认过期时间（秒）。默认 <c>300</c>（5 分钟），必须大于 0。
    /// </summary>
    public int DefaultL1DurationSeconds { get; set; } = 300;

    /// <summary>
    ///     L2（Redis）默认过期时间（秒）。默认 <c>300</c>。
    ///     <c>0</c> 表示 L2 永不过期；必须大于等于 0。
    /// </summary>
    public int DefaultL2DurationSeconds { get; set; } = 300;

    /// <summary>是否启用 Fail-Safe（工厂失败时短暂复用过期条目）。默认 <c>true</c>。</summary>
    public bool IsFailSafeEnabled { get; set; } = true;

    /// <summary>
    ///     Fail-Safe 最大持续时间（秒）。默认 <c>3600</c>，必须大于 0。
    ///     建议大于 <see cref="DefaultL1DurationSeconds"/>，以便过期条目仍可作回退。
    /// </summary>
    public int FailSafeMaxDurationSeconds { get; set; } = 3600;

    /// <summary>校验配置并创建 <see cref="CSRedisClient"/>（单机或集群）。</summary>
    public CSRedisClient CreateCSRedisClient()
    {
        Validate();

        return Mode switch
        {
            RedisMode.Standalone => new CSRedisClient(BuildNodeConnectionString($"{Host.Trim()}:{Port}")),
            RedisMode.Cluster => new CSRedisClient(null, BuildClusterConnectionStrings()),
            _ => throw new InvalidOperationException($"不支持的 Redis 模式：{Mode}")
        };
    }

    /// <summary>校验全部配置项。</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Mode))
            throw new InvalidOperationException($"配置无效：{SectionName}:{nameof(Mode)} 取值无效。");

        if (DefaultL1DurationSeconds <= 0)
            throw new InvalidOperationException($"配置无效：{SectionName}:{nameof(DefaultL1DurationSeconds)} 必须大于 0。");

        if (DefaultL2DurationSeconds < 0)
            throw new InvalidOperationException($"配置无效：{SectionName}:{nameof(DefaultL2DurationSeconds)} 不能为负数（0 表示 L2 永不过期）。");

        if (FailSafeMaxDurationSeconds <= 0)
            throw new InvalidOperationException($"配置无效：{SectionName}:{nameof(FailSafeMaxDurationSeconds)} 必须大于 0。");

        // 关闭 Redis 时仅校验内存缓存相关项，不校验连接信息
        if (!EnableDistributedCache)
            return;

        if (PoolSize <= 0)
            throw new InvalidOperationException($"配置无效：{SectionName}:{nameof(PoolSize)} 必须大于 0。");

        if (string.IsNullOrWhiteSpace(KeyPrefix))
            throw new InvalidOperationException($"配置缺失：{SectionName}:{nameof(KeyPrefix)} 为必填项。");

        if (Mode == RedisMode.Standalone)
        {
            if (string.IsNullOrWhiteSpace(Host))
                throw new InvalidOperationException($"配置无效：{SectionName}:{nameof(Host)} 在单机模式下不能为空。");

            if (Port <= 0 || Port > 65535)
                throw new InvalidOperationException($"配置无效：{SectionName}:{nameof(Port)} 必须在 1~65535。");

            if (DefaultDatabase < 0)
                throw new InvalidOperationException($"配置无效：{SectionName}:{nameof(DefaultDatabase)} 不能为负数。");
        }
        else if (Mode == RedisMode.Cluster)
        {
            var endpoints = NormalizeEndPoints();
            if (endpoints.Count == 0)
                throw new InvalidOperationException($"配置缺失：{SectionName}:{nameof(EndPoints)} 在集群模式下至少配置一个 host:port。");

            foreach (var ep in endpoints)
            {
                if (!TryParseEndPoint(ep, out _, out _))
                    throw new InvalidOperationException($"配置无效：{SectionName}:{nameof(EndPoints)} 项 \"{ep}\" 格式应为 host:port。");
            }

            if (DefaultDatabase != 0)
                throw new InvalidOperationException($"配置无效：{SectionName}:{nameof(DefaultDatabase)} 在集群模式下必须为 0。");
        }
    }

    /// <summary>L1 默认过期时间。</summary>
    public TimeSpan GetL1Duration() => TimeSpan.FromSeconds(DefaultL1DurationSeconds);

    /// <summary>
    ///     L2 默认过期时间。
    ///     <see cref="DefaultL2DurationSeconds"/> 为 <c>0</c> 时返回 <see cref="TimeSpan.MaxValue"/>（永不过期）。
    /// </summary>
    public TimeSpan GetL2Duration() =>
        DefaultL2DurationSeconds == 0
            ? TimeSpan.MaxValue
            : TimeSpan.FromSeconds(DefaultL2DurationSeconds);

    /// <summary>Fail-Safe 最大持续时间。</summary>
    public TimeSpan GetFailSafeMaxDuration() => TimeSpan.FromSeconds(FailSafeMaxDurationSeconds);

    /// <summary>
    ///     构建 StackExchange.Redis 连接配置（供 Backplane 使用）。
    ///     单机与集群均通过 <see cref="ConfigurationOptions.EndPoints"/> 接入。
    /// </summary>
    public ConfigurationOptions BuildStackExchangeConfiguration()
    {
        Validate();

        var config = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            ConnectRetry = 3,
            ConnectTimeout = 5000,
            SyncTimeout = 10000,
            DefaultDatabase = Mode == RedisMode.Cluster ? 0 : DefaultDatabase
        };

        foreach (var ep in ResolveEndPoints())
        {
            if (!TryParseEndPoint(ep, out var host, out var port))
                throw new InvalidOperationException($"配置无效：无法解析端点 \"{ep}\"。");

            config.EndPoints.Add(host, port);
        }

        if (!string.IsNullOrEmpty(Password))
            config.Password = Password;

        return config;
    }

    private string[] BuildClusterConnectionStrings() =>
        ResolveEndPoints().Select(BuildNodeConnectionString).ToArray();

    private string BuildNodeConnectionString(string hostPort)
    {
        var parts = new List<string>
        {
            hostPort.Trim(),
            $"defaultDatabase={(Mode == RedisMode.Cluster ? 0 : DefaultDatabase)}",
            $"poolsize={PoolSize}",
            $"prefix={KeyPrefix.Trim()}"
        };

        if (!string.IsNullOrEmpty(Password))
            parts.Insert(1, $"password={Password}");

        return string.Join(',', parts);
    }

    private IReadOnlyList<string> ResolveEndPoints()
    {
        if (Mode == RedisMode.Cluster)
            return NormalizeEndPoints();

        return [$"{Host.Trim()}:{Port}"];
    }

    private List<string> NormalizeEndPoints() =>
        EndPoints
            .Where(static e => !string.IsNullOrWhiteSpace(e))
            .Select(static e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool TryParseEndPoint(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        // 支持 [IPv6]:port 与 host:port
        if (value.StartsWith('[') && value.Contains(']'))
        {
            var end = value.IndexOf(']');
            if (end <= 1 || end + 2 >= value.Length || value[end + 1] != ':')
                return false;

            host = value[1..end];
            return int.TryParse(value[(end + 2)..], out port) && port is > 0 and <= 65535;
        }

        var idx = value.LastIndexOf(':');
        if (idx <= 0 || idx == value.Length - 1)
            return false;

        host = value[..idx];
        return !string.IsNullOrWhiteSpace(host)
               && int.TryParse(value[(idx + 1)..], out port)
               && port is > 0 and <= 65535;
    }
}
