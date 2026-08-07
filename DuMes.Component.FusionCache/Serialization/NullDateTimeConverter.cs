using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuMes.Component.FusionCache.Serialization;

/// <summary>
///     <see cref="DateTime?"/> 序列化：支持 JSON null、空字符串视为 null。
/// </summary>
public sealed class NullDateTimeConverter : JsonConverter<DateTime?>
{
    /// <inheritdoc />
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (DateTime.TryParse(text, out var parsed))
                return parsed;

            if (DateTime.TryParseExact(
                    text,
                    DateTimeConverter.Format,
                    null,
                    System.Globalization.DateTimeStyles.None,
                    out parsed))
                return parsed;

            throw new JsonException($"无法将 \"{text}\" 解析为 {nameof(DateTime)}。");
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var unixMs))
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;

        throw new JsonException($"意外的 Token 类型：{reader.TokenType}。");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.ToString(DateTimeConverter.Format));
    }
}
