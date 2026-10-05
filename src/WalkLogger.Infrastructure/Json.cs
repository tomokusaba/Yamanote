using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WalkLogger.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(WalkSession))
            {
                foreach (var property in info.Properties.Where(p =>
                    p.Name is "stats" or "displayTitle" or "displayDate" or "displayStats").ToArray())
                    info.Properties.Remove(property);
            }
            if (info.Type == typeof(TrackPoint) || info.Type == typeof(PhotoRecord))
            {
                foreach (var property in info.Properties)
                    if (property.Name is "time" or "lat" or "lon" ||
                        (info.Type == typeof(PhotoRecord) && property.Name == "image"))
                        property.IsRequired = true;
            }
        });
        return new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.Strict,
            TypeInfoResolver = resolver,
            Converters = { new UtcTimestampConverter() }
        };
    }
}

public sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? throw new JsonException("日時がありません。");
        if (!TrackImporter.HasTimezone(text) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new JsonException("日時はUTCのZまたはオフセットを含めてください。");
        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}
