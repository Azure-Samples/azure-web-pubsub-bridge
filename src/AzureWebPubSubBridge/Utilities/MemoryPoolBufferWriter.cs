using System.Buffers;

namespace AzureWebPubSubBridge.Utilities;

public class MemoryPoolBufferWriter : IBufferWriter<byte>
{
    private Memory<byte> _buffer;

    public MemoryPoolBufferWriter(Memory<byte> buffer)
    {
        _buffer = buffer;
    }

    public void Advance(int count)
    {
        _buffer = _buffer.Slice(count);
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        return _buffer;
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        return GetMemory(sizeHint).Span;
    }
}