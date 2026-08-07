namespace DuMes.Component.FusionCache.Options;

/// <summary>
///     Redis 部署模式。
/// </summary>
public enum RedisMode
{
    /// <summary>单机（Standalone）。</summary>
    Standalone = 0,

    /// <summary>官方 Redis Cluster（分片集群）。</summary>
    Cluster = 1
}