using System.Buffers;
using AzureWebPubSubBridge.Formats;

namespace AzureWebPubSubBridge.Messages;

public record ReceivedMessageSegment(IMemoryOwner<byte> Buffer, int Length, long SequenceNumber) : IDisposable
{
    public ReadOnlyMemory<byte> Data { get; } = PubSubDataFormat.GetData(Buffer.Memory, Length);

    public void Dispose()
    {
        Buffer.Dispose();
    }

    public int CopyTo(Span<byte> destination, int start, int length)
    {
        if (start >= Data.Length)
            throw new ArgumentOutOfRangeException(nameof(start));

        length = Math.Min(length, Data.Span.Length - start);
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        Data.Slice(start, length).Span.CopyTo(destination);

        return length;
    }
}