using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureWebPubSubBridge.Formats;

public class PubSubDataConverter : JsonConverter<IMemoryOwner<byte>>
{
    public override IMemoryOwner<byte> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var data = MemoryPool<byte>.Shared.Rent(reader.ValueSpan.Length);
        PubSubDataFormat.CopyFromJson(data.Memory.Span, reader.ValueSpan);
        return data;
    }

    public override void Write(Utf8JsonWriter writer, IMemoryOwner<byte> value, JsonSerializerOptions options)
    {
        var length = PubSubDataFormat.GetTotalLength(value.Memory.Span);
        writer.WriteRawValue(value.Memory.Span.Slice(0, length));
    }
}