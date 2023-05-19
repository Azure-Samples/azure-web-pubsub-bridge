using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace AzureWebPubSubBridge.Formats;

public static class PubSubDataFormat
{
    // 10 chars
    public const int Magic = 1302071213;
    public const int MagicStart = 1;
    public const int MagicLength = 10;
    public const int MessageIdStart = MagicStart + MagicLength;
    public const int MessageIdLength = 20;
    public const int SequenceNumberStart = MessageIdStart + MessageIdLength;
    public const int SequenceNumberLength = 10;

    public const int TotalDecodedDataSizeStart = SequenceNumberStart + SequenceNumberLength;
    public const int TotalDecodedDataSizeLength = 10;
    public const int EncodedDataSizeStart = TotalDecodedDataSizeStart + TotalDecodedDataSizeLength;
    public const int EncodedDataSizeLength = 10;

    public const int ToAddressStart = EncodedDataSizeStart + EncodedDataSizeLength;
    public const int AddressLength = 32;
    public const int FromAddressStart = ToAddressStart + AddressLength;

    public const int IsConnectStart = FromAddressStart + AddressLength;
    public const int IsConnectLength = 1;

    public const int DataStart = IsConnectStart + IsConnectLength;

    public const int QuotationEnd = 1;

    public static bool ContainsMagic(ReadOnlySpan<byte> buffer)
    {
        return GetInt(buffer.Slice(MagicStart, MagicLength)) == Magic;
    }

    public static void CopyFromJson(Span<byte> buffer, ReadOnlySpan<byte> value)
    {
        buffer[0] = (byte)'"';
        value.CopyTo(buffer.Slice(MagicStart));
        buffer[MagicStart + value.Length] = (byte)'"';
    }

    public static Span<byte> DecodeData(Span<byte> buffer, out int written)
    {
        var length = GetEncodedDataLength(buffer);
        var slice = buffer.Slice(DataStart, length);
        Base64.DecodeFromUtf8InPlace(slice, out written);
        return slice.Slice(0, written);
    }

    public static void EncodeToFormat(Span<byte> buffer, ReadOnlySpan<byte> data, string to, string from,
        long messageId, int sequenceNumber, int totalDecodedSize, out int consumed, out int written)
    {
        SetEncodeData(buffer, data, out consumed, out written);

        buffer[0] = (byte)'"';
        SetMagic(buffer);
        SetMessageId(buffer, messageId);
        SetSequenceNumber(buffer, sequenceNumber);
        SetTotalDecodedSize(buffer, totalDecodedSize);
        SetEncodedDataLength(buffer, written);
        SetToAddress(buffer, to);
        SetFromAddress(buffer, from);
        SetIsConnect(buffer, false);
        buffer[DataStart + written] = (byte)'"';
    }

    public static string FormatId(string id)
    {
        if (id.Length == AddressLength)
            return id;

        if (id.Length > AddressLength)
            throw new ArgumentException($"ID length is larger than maximum length: {AddressLength}", nameof(id));

        if (id.Length > AddressLength)
            return new string(id.AsSpan().Slice(0, AddressLength));

        var newId = new char[AddressLength].AsSpan();
        newId.Fill('-');
        id.AsSpan().CopyTo(newId);
        return new string(newId);
    }

    public static ReadOnlySpan<byte> GetData(ReadOnlySpan<byte> buffer, int length)
    {
        return buffer.Slice(DataStart, length);
    }

    public static ReadOnlyMemory<byte> GetData(ReadOnlyMemory<byte> buffer, int length)
    {
        return buffer.Slice(DataStart, length);
    }

    public static int GetEncodedDataLength(ReadOnlySpan<byte> buffer)
    {
        return GetInt(buffer.Slice(EncodedDataSizeStart, EncodedDataSizeLength));
    }

    public static string GetFromAddress(ReadOnlySpan<byte> buffer)
    {
        return Encoding.UTF8.GetString(buffer.Slice(FromAddressStart, AddressLength));
    }

    public static long GetMessageId(ReadOnlySpan<byte> buffer)
    {
        return GetLong(buffer.Slice(MessageIdStart, MessageIdLength));
    }

    public static int GetSequenceNumber(ReadOnlySpan<byte> buffer)
    {
        return GetInt(buffer.Slice(SequenceNumberStart, SequenceNumberLength));
    }

    public static int GetTotalDecodedSize(ReadOnlySpan<byte> buffer)
    {
        return GetInt(buffer.Slice(TotalDecodedDataSizeStart, TotalDecodedDataSizeLength));
    }

    public static int GetTotalLength(ReadOnlySpan<byte> buffer)
    {
        return GetEncodedDataLength(buffer) + DataStart + QuotationEnd;
    }

    public static bool IsAddressedTo(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> destination)
    {
        if (destination.Length < AddressLength)
            throw new ArgumentException($"Destination length is less than address length: {AddressLength}",
                nameof(destination));

        return buffer.Slice(ToAddressStart, AddressLength).SequenceEqual(destination);
    }

    public static bool IsConnect(ReadOnlySpan<byte> buffer)
    {
        return buffer.Slice(IsConnectStart)[0] == 'B';
    }

    public static void SetEncodeData(Span<byte> buffer, ReadOnlySpan<byte> data, out int consumed, out int written)
    {
        Base64.EncodeToUtf8(data, buffer.Slice(DataStart, buffer.Length - DataStart - QuotationEnd), out consumed,
            out written);
    }

    public static void SetFromAddress(Span<byte> buffer, string destination)
    {
        Encoding.UTF8.GetBytes(FormatId(destination)).CopyTo(buffer.Slice(FromAddressStart));
    }

    public static void SetIsConnect(Span<byte> buffer, bool isConnect)
    {
        buffer.Slice(IsConnectStart)[0] = isConnect ? (byte)'B' : (byte)'A';
    }

    public static void SetMagic(Span<byte> buffer)
    {
        SetInt(buffer.Slice(MagicStart), Magic);
    }

    public static void SetToAddress(Span<byte> buffer, string destination)
    {
        Encoding.UTF8.GetBytes(FormatId(destination)).CopyTo(buffer.Slice(ToAddressStart));
    }

    private static int GetInt(ReadOnlySpan<byte> span)
    {
        Utf8Parser.TryParse(span, out int value, out var consumed);
        return value;
    }

    private static long GetLong(ReadOnlySpan<byte> span)
    {
        Utf8Parser.TryParse(span, out long value, out var consumed);
        return value;
    }

    private static void SetEncodedDataLength(Span<byte> buffer, int length)
    {
        SetInt(buffer.Slice(EncodedDataSizeStart, EncodedDataSizeLength), length);
    }

    private static void SetInt(Span<byte> span, int value)
    {
        Utf8Formatter.TryFormat(value, span, out _, new StandardFormat('D', (byte)span.Length));
    }

    private static void SetLong(Span<byte> span, long value)
    {
        Utf8Formatter.TryFormat(value, span, out _, new StandardFormat('D', (byte)span.Length));
    }

    private static void SetMessageId(Span<byte> buffer, long messageId)
    {
        SetLong(buffer.Slice(MessageIdStart, MessageIdLength), messageId);
    }

    private static void SetSequenceNumber(Span<byte> buffer, int sequenceNumber)
    {
        SetInt(buffer.Slice(SequenceNumberStart, SequenceNumberLength), sequenceNumber);
    }

    private static void SetTotalDecodedSize(Span<byte> buffer, int totalDecodedSize)
    {
        SetInt(buffer.Slice(TotalDecodedDataSizeStart, TotalDecodedDataSizeLength), totalDecodedSize);
    }
}