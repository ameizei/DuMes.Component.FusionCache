# DuMes.Component.FusionCache

多脉缓存组件：基于 [FusionCache](https://github.com/ZiggyCreatures/FusionCache) 提供 **L1 内存 + 可选 L2 Redis（[CSRedis](https://github.com/2881099/csredis)）** 混合缓存，多实例通过 [StackExchange.Redis Backplane](https://github.com/ZiggyCreatures/FusionCache/blob/main/docs/Backplane.md) 即时同步 L1，并与 [DuMes.Component.Serilog](https://github.com/ameizei/DuMes.Component.Serilog) 共用 `ILogger` 管道。

## 项目结构

```text
DuMes.Component.FusionCache/
├── DependencyInjection/     # AddComponentFusionCache
├── Options/                 # FusionCacheComponentOptions、RedisMode
└── Serialization/           # FusionCacheJsonOptions、DateTime 转换器
```

## 分工

| 组件 | 职责 |
|------|------|
| FusionCache | 缓存门面：L1、Stampede、Fail-Safe、超时等 |
| Caching.CSRedis | L2：实现 `IDistributedCache` |
| StackExchange.Redis Backplane | 多实例 L1 即时失效通知（Pub/Sub） |
| System.Text.Json | L2 序列化（见下文「序列化规则」） |
| CSRedisClient / RedisHelper | 业务 Redis：Hash、List（队列）、Pub/Sub |
| DuMes.Component.Serilog | 日志管道（宿主侧 `UseComponentSerilog`） |

```text
业务代码
  ├─ IFusionCache
  │     ├─ L1 内存
  │     ├─ L2 Redis（CSRedis / IDistributedCache）
  │     └─ Backplane（StackExchange.Redis Pub/Sub，多实例同步 L1）
  └─ CSRedisClient         → Hash / 队列 / 业务订阅
         ↑
   同一套 Redis（单机或同一 Cluster；CSRedis 做 KV/L2，SE.Redis 做 Backplane）
```

## 接入

```csharp
using DuMes.Component.FusionCache.DependencyInjection;
using DuMes.Component.FusionCache.Options;
using DuMes.Component.Serilog.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseComponentSerilog();
builder.Services.AddComponentFusionCache(builder.Configuration);
```

配置节名固定为 **`FusionCache`**（缺失则启动失败）。

也可纯代码配置（不读配置节）：

```csharp
builder.Services.AddComponentFusionCache(o =>
{
    o.KeyPrefix = "DuMes:";
    // ...
});
```

### 注册后得到什么

| `EnableDistributedCache` | 注册内容 |
|--------------------------|----------|
| `true` | `IFusionCache`、`CSRedisClient`、`RedisHelper`、`IDistributedCache`；可选 Backplane（`IConnectionMultiplexer`） |
| `false` | 仅 `IFusionCache`（进程内 L1）；**不**连接 Redis，也**不**注册 `CSRedisClient` / `IDistributedCache` |

`EnableBackplane` 仅在启用 Redis 时生效；关闭 Redis 时即使配置为 `true` 也会被忽略。

## 配置说明

### 配置项一览

| 配置项 | 类型 | 默认值 | 必填 | 说明 |
|--------|------|--------|------|------|
| `Mode` | `Standalone` / `Cluster` | `Standalone` | 否 | Redis 部署模式 |
| `Host` | string | `127.0.0.1` | 单机有效 | 单机主机；`Cluster` 模式忽略 |
| `Port` | int | `6379` | 单机有效 | 单机端口 `1~65535`；`Cluster` 模式忽略 |
| `EndPoints` | string[] | `[]` | 集群必填 | 集群节点，格式 `host:port` 或 `[IPv6]:port`（建议写全主节点） |
| `Password` | string? | `null` | 否 | Redis 密码，可为空 |
| `DefaultDatabase` | int | `0` | 否 | 库号；**集群必须为 `0`** |
| `KeyPrefix` | string | — | 启用 Redis 时必填 | 经 CSRedis `prefix=` 自动加到所有 key；同时用作 Backplane 频道前缀；空则启动报错 |
| `PoolSize` | int | `50` | 启用 Redis 时有效 | CSRedis 连接池大小，必须 `> 0` |
| `EnableDistributedCache` | bool | `true` | 否 | 是否使用 Redis；`false` 时不连接 Redis，仅 L1 |
| `EnableBackplane` | bool | `true` | 否 | 多实例即时清 L1；仅在启用 Redis 时生效 |
| `DefaultL1DurationSeconds` | int | `300` | 否 | L1 / 逻辑过期秒数，必须 `> 0` |
| `DefaultL2DurationSeconds` | int | `300` | 否 | L2 过期秒数；`0` = 永不过期（`TimeSpan.MaxValue`）；不能为负 |
| `IsFailSafeEnabled` | bool | `true` | 否 | 工厂失败时是否短暂复用过期条目 |
| `FailSafeMaxDurationSeconds` | int | `3600` | 否 | Fail-Safe 最长保留秒数，必须 `> 0`（建议大于 L1） |

### 组件内置行为（不可配置项）

| 行为 | 说明 |
|------|------|
| L2 熔断 | `DistributedCacheCircuitBreakerDuration = 30s`：L2 连续失败后短暂跳过 Redis，避免拖垮请求 |
| Backplane 连接 | `AbortOnConnectFail=false`，`ConnectRetry=3`，`ConnectTimeout=5s`，`SyncTimeout=10s` |
| 默认条目过期 | `Duration` = L1；启用 Redis 时另设 `DistributedCacheDuration` = L2（**不**再写死 `MemoryCacheDuration`，以便 `SetDuration` 能覆盖 L1） |
| 配置校验 | 启动时 `Validate()`；关闭 Redis 时跳过连接相关校验，但仍校验 L1 / Fail-Safe 等 |

### 单机完整示例（含注释）

> 下列为 **JSONC** 示意（`//` 注释便于阅读）；拷贝到 `appsettings.json` 时请去掉注释。

```jsonc
{
  "FusionCache": {
    // Redis 部署模式：Standalone（单机） / Cluster（官方分片集群）
    "Mode": "Standalone",

    // 单机主机（Cluster 模式下忽略）
    "Host": "127.0.0.1",

    // 单机端口，范围 1~65535（Cluster 模式下忽略）
    "Port": 6379,

    // 集群节点列表；单机模式可不配
    // "EndPoints": [ "192.168.1.10:6379", "192.168.1.11:6379" ],

    // Redis 密码；无密码可省略或设为 null
    "Password": "your-password",

    // 默认数据库编号；单机任意 >=0，集群必须为 0
    "DefaultDatabase": 0,

    // Key 前缀（启用 Redis 时必填）：CSRedis 会自动加到所有 key；同时用作 Backplane 频道前缀
    "KeyPrefix": "DuMes:",

    // CSRedis 连接池大小，必须 > 0（仅启用 Redis 时有效）
    "PoolSize": 50,

    // 是否使用 Redis（L2 + CSRedisClient + 可选 Backplane）
    // false：完全不连接 Redis，仅进程内 L1 内存缓存
    "EnableDistributedCache": true,

    // 是否启用 Backplane（StackExchange.Redis Pub/Sub）；仅 EnableDistributedCache=true 时生效
    // true：某实例 Set/Remove/回源写入后，其它实例立刻清对应 L1
    "EnableBackplane": true,

    // L1（内存）默认过期时间（秒），必须 > 0；业务侧 SetDuration 覆盖的也是这一层
    "DefaultL1DurationSeconds": 300,

    // L2（Redis）默认过期时间（秒）；0 = 永不过期；与 L1 独立，SetDuration 不会改 L2
    "DefaultL2DurationSeconds": 300,

    // 是否启用 Fail-Safe（回源失败时短暂返回过期缓存）
    "IsFailSafeEnabled": true,

    // Fail-Safe 最大保留时间（秒），必须 > 0；建议大于 DefaultL1DurationSeconds
    "FailSafeMaxDurationSeconds": 3600
  }
}
```

### 集群完整示例（含注释）

```jsonc
{
  "FusionCache": {
    // 官方 Redis Cluster（分片）；同一集群内连不同节点，L2/Backplane 仍互通
    "Mode": "Cluster",

    // 集群节点（必填，至少 1 个；建议写全主节点），格式 host:port 或 [IPv6]:port
    "EndPoints": [
      "192.168.1.10:6379",
      "192.168.1.11:6379",
      "192.168.1.12:6379"
    ],

    // 集群模式不要依赖 Host/Port（会被忽略）
    // "Host": "127.0.0.1",
    // "Port": 6379,

    "Password": "your-password",

    // 集群仅支持 database 0
    "DefaultDatabase": 0,

    // 必填（启用 Redis 时）；各节点连接串会带上相同 prefix
    "KeyPrefix": "DuMes:",

    "PoolSize": 50,
    "EnableDistributedCache": true,
    "EnableBackplane": true,
    "DefaultL1DurationSeconds": 300,
    // L2 永不过期示例：
    // "DefaultL2DurationSeconds": 0,
    "DefaultL2DurationSeconds": 300,
    "IsFailSafeEnabled": true,
    "FailSafeMaxDurationSeconds": 3600
  }
}
```

### 仅 L1（不连 Redis）

```jsonc
{
  "FusionCache": {
    "EnableDistributedCache": false,
    "DefaultL1DurationSeconds": 300,
    "IsFailSafeEnabled": true,
    "FailSafeMaxDurationSeconds": 3600
    // 无需 KeyPrefix / Host / Password 等
  }
}
```

### 代码配置

```csharp
builder.Services.AddComponentFusionCache(o =>
{
    o.Mode = RedisMode.Standalone;
    o.Host = "127.0.0.1";
    o.Port = 6379;
    o.Password = "your-password";
    o.KeyPrefix = "DuMes:";
    o.DefaultL1DurationSeconds = 300;
    o.DefaultL2DurationSeconds = 300;
    o.EnableDistributedCache = true;
    o.EnableBackplane = true;
});

// 集群：
// builder.Services.AddComponentFusionCache(o =>
// {
//     o.Mode = RedisMode.Cluster;
//     o.EndPoints = ["192.168.1.10:6379", "192.168.1.11:6379"];
//     o.Password = "your-password";
//     o.KeyPrefix = "DuMes:";
//     o.DefaultDatabase = 0;
// });
```

也可在配置基础上再覆盖，并继续定制 FusionCache Builder：

```csharp
builder.Services.AddComponentFusionCache(
    builder.Configuration,
    configureOptions: o => o.DefaultL1DurationSeconds = 60,
    configureCache: b => { /* 额外 WithXxx */ });
```

## 序列化规则

L2 使用 `FusionCacheJsonOptions.JsonStringOptions`（进程内单例，首次访问时创建）：

| 规则 | 说明 |
|------|------|
| `WriteIndented = false` | 不缩进，节省 Redis 空间 |
| `PropertyNamingPolicy = CamelCase` | 属性名驼峰：`Name` → `name` |
| `PropertyNameCaseInsensitive = true` | 反序列化属性名大小写不敏感 |
| `NumberHandling = AllowReadingFromString` | `"123"` 也可读成数字 |
| `Encoder = UnsafeRelaxedJsonEscaping` | 中文等不转成 `\uXXXX` |
| `JsonStringEnumConverter` | 枚举序列化为名称而非数字 |
| `DateTimeConverter` | 写出 `yyyy-MM-dd HH:mm:ss`；读入支持该格式、常规解析、Unix 毫秒 |
| `NullDateTimeConverter` | `DateTime?` 支持 JSON `null` / 空串；写出格式同上 |
| 不使用 `WhenWritingNull` | null 字段仍会写出 |

业务侧若需同一套规则，可直接引用 `FusionCacheJsonOptions.JsonStringOptions`。

## 使用

```csharp
// 混合缓存（注入 IFusionCache）
var product = await cache.GetOrSetAsync(
    $"product:{id}",
    async _ => await db.GetProductAsync(id),
    options => options.SetDuration(TimeSpan.FromMinutes(5)));
// SetDuration 只覆盖 L1 / 逻辑过期；L2 仍用 DefaultL2DurationSeconds
// 若本次也要改 L2：
// options => options
//     .SetDuration(TimeSpan.FromMinutes(5))
//     .SetDistributedCacheDuration(TimeSpan.FromHours(1));

// 改配置后主动失效（开启 Backplane 时其它实例 L1 也会清）
await cache.RemoveAsync($"product:{id}");

// Hash / 队列 / 发布（注入 CSRedisClient，与缓存共用连接）
// 实际 Redis key = KeyPrefix + "demo:product"（CSRedis prefix 自动拼接）
redis.HSet("demo:product", "name", "widget");
redis.LPush("demo:queue", "job-1");
redis.Publish("demo:events", "hello");
// 或 RedisHelper.HSet / LPush / Publish
```

> 同一批业务 key 请统一走 `IFusionCache`，不要再直接改 L2，以免 L1/L2 不一致。Hash / 队列请使用独立 key 名（前缀已由 `KeyPrefix` 统一加上）。

### 过期时间怎么生效

| 配置 / API | 作用对象 |
|------------|----------|
| `DefaultL1DurationSeconds` | 默认 `Duration`（L1 / 逻辑过期） |
| `DefaultL2DurationSeconds` | 默认 `DistributedCacheDuration`（仅启用 Redis 时） |
| `options.SetDuration(...)` | 覆盖本次调用的 L1 / 逻辑过期；**不**改 L2 |
| `options.SetDistributedCacheDuration(...)` | 覆盖本次调用的 L2 |
| `FailSafeMaxDurationSeconds` | 启用 Fail-Safe 时，过期条目在内存中可保留的上限 |

## 适用场景

### 何时用 `IFusionCache`

读多写少、回源贵、可能多实例共享的数据，例如：配置、字典、组织树、权限、物料/工艺主数据。

典型用法是读侧 `GetOrSet`：未命中时由**当前读请求**去 DB/远程拉取并回填 L1（+ 可选 L2）。多实例请开启 `EnableBackplane`，否则其它节点 L1 最多等到 `DefaultL1DurationSeconds`（或该次 `SetDuration`）才刷新。

### 何时用 `CSRedisClient`（不要硬套 FusionCache）

一端只写、另一端只读，且**写端才是数据源头**时（例如采集客户端推设备状态，网页端只展示），不适合 `GetOrSet`。

```text
采集客户端（只写）              Redis                 网页 API（只读）
      │                          │                        │
      │  HSET / SET 最新状态      │                        │
      ├─────────────────────────►│                        │
      │  可选：PUBLISH 变更通知   │                        │
      ├─────────────────────────►│── Subscribe ──────────►│ 推前端 / 刷新展示
      │                          │◄── HGET / GET ─────────┤ 打开页面、轮询
```

```csharp
// 采集端
redis.HSet("device:status", deviceId, json);
redis.Publish("device:status:changed", deviceId);

// 网页端
var json = redis.HGet("device:status", deviceId);
```

| 需求 | 建议 |
|------|------|
| 设备/工位最新状态给网页看 | Redis Hash/String：写端 SET，读端 GET |
| 页面要近实时刷新 | 写端 PUBLISH + 读端 Subscribe（再转 SignalR/SSE） |
| 历史曲线 / 追溯 | 另写时序库或 DB，不要只靠 Redis |
| 配置、字典等读多写少 | 用 `IFusionCache` + `EnableBackplane` |
| 网页读极频繁且可接受秒级旧数据 | 可读 Redis 外包短 TTL 的 `GetOrSet`；多数状态场景直接读 Redis 即可 |

**一句话**：采集写 Redis、网页读 Redis（可选 Pub/Sub）；FusionCache 留给「读的人也会回源」的那类数据。

## 测试工程

### Worker / Console 场景（单机 / 集群）

共用场景在 `TestWorkerService.Shared`。Worker 与 Console 各有单机、集群工程（跑完全部用例后退出，退出码 `0`=全过）。

| 工程 | 类型 | Redis | 默认连接 |
|------|------|-------|----------|
| `TestWorkerService` | Worker | 单机 | `127.0.0.1:6389` |
| `TestWorkerService.Cluster` | Worker | Cluster | `127.0.0.1:17001` |
| `TestConsole` | Console | 单机 | `127.0.0.1:6389` |
| `TestConsole.Cluster` | Console | Cluster | `127.0.0.1:17001` |

```bash
# 1) 启动 Docker Redis
docker compose -f docker/redis-standalone/docker-compose.yml up -d
docker compose -f docker/redis-cluster/docker-compose.yml up -d
# 集群由 init 自动分配槽位；可用：
# docker exec dumes-redis-cluster redis-cli cluster info

# 2) Worker
dotnet run --project TestWorkerService
dotnet run --project TestWorkerService.Cluster

# 3) Console
dotnet run --project TestConsole
dotnet run --project TestConsole.Cluster
```

覆盖场景：`Ping`、DI 注册、`GetOrSet` 命中、`TryGet`/`Set`、`Remove`、`SetDuration`、CSRedis Hash/队列/PubSub、双实例 Backplane 清 L1。

### TestWebApi（手动演示）

`TestWebApi` 已接入本地 Redis（见 `appsettings.json`）。

```bash
cd TestWebApi && dotnet run
```

| 接口 | 说明 |
|------|------|
| `GET /cache/` | 接口清单 |
| `GET /cache/product/{id}` | GetOrSet（首次回源，再次命中缓存） |
| `DELETE /cache/product/{id}` | 删缓存 |
| `GET /cache/product/{id}/raw` | 仅读缓存 |
| `POST /cache/redis/hash` | Hash 写入 |
| `GET /cache/redis/hash/{field}` | Hash 读取 |
| `POST /cache/redis/queue` | 入队 |
| `GET /cache/redis/queue` | 出队 |
| `POST /cache/redis/publish` | Pub/Sub 发布 |
| `GET /cache/redis/ping` | Redis PING |

```bash
curl http://127.0.0.1:5017/cache/product/1
curl http://127.0.0.1:5017/cache/product/1

curl -X POST http://127.0.0.1:5017/cache/redis/hash \
  -H 'Content-Type: application/json' \
  -d '{"field":"name","value":"widget"}'
```
