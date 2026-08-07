using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuMes.Component.FusionCache.Serialization;

/// <summary>
///     FusionCache / 组件共用的 System.Text.Json 选项。
/// </summary>
public static class FusionCacheJsonOptions
{
    /// <summary>
    ///     Json 字符串设置：驼峰命名、枚举写名称、中文不转义、数字可读字符串、可空时间支持 null/空串。
    /// </summary>
    public static JsonSerializerOptions JsonStringOptions { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            // 属性名驼峰：Name → name
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // 读写属性名大小写不敏感
            PropertyNameCaseInsensitive = true,
            // "123" 也可反序列化为数字
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            // 遇到中文等字符不使用 \uXXXX 转义
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new DateTimeConverter());
        options.Converters.Add(new NullDateTimeConverter());

        return options;
    }
}
