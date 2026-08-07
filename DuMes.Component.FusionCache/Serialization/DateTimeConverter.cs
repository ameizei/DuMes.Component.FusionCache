using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuMes.Component.FusionCache.Serialization;

/// <summary>
///     <see cref="DateTime" /> 序列化：输出固定本地时间格式字符串。
/// </summary>
public sealed class DateTimeConverter : JsonConverter<DateTime>
{
    /// <summary>写入格式。</summary>
    public const string Format = "yyyy-MM-dd HH:mm:ss";

    /// <inheritdoc />
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            throw new JsonException("无法将 null 反序列化为 DateTime，请使用 DateTime?。");

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (string.IsNullOrWhiteSpace(text))
                throw new JsonException($"空字符串无法反序列化为 {nameof(DateTime)}。");

            if (DateTime.TryParse(text, out var parsed))
                return parsed;

            if (DateTime.TryParseExact(text, Format, null, DateTimeStyles.None, out parsed))
                return parsed;

            throw new JsonException($"无法将 \"{text}\" 解析为 {nameof(DateTime)}。");
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var unixMs))
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;

        throw new JsonException($"意外的 Token 类型：{reader.TokenType}。");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(Format));
    }
}