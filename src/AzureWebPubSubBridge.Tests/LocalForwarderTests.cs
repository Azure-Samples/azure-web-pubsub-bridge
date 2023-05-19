using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit.Abstractions;

namespace AzureWebPubSubBridge.Tests;

public class LocalForwarderTests : IClassFixture<DirectBridgeFixture>
{
    private readonly TcpClient _client;
    private readonly DirectBridgeFixture _fixture;
    private readonly HttpClient _httpClient;

    public LocalForwarderTests(DirectBridgeFixture fixture, ITestOutputHelper testOutputHelper)
    {
        _fixture = fixture;
        _fixture.TestOutputHelper = testOutputHelper;
        _client = new TcpClient();
        _httpClient = new HttpClient();
    }

    [Fact]
    public async Task WhenSendingGetRequest_ItShouldReceiveTheMessage()
    {
        await _fixture.StartLocalForwarderAsync();
        await _fixture.StartRemoteListenerAsync();

        var receiverTask = Task.Run(async () =>
        {
            using var readBuffer = MemoryPool<byte>.Shared.Rent(_fixture.Config.PacketSize);
            var receiveStreamTask = _fixture.AcceptListenerConnectionAsync();
            await using var receiveStream = await receiveStreamTask;
            var result = string.Empty;
            var written = await receiveStream.ReadAsync(readBuffer.Memory, _fixture.Cancel.Token);
            result += Encoding.UTF8.GetString(readBuffer.Memory.Slice(0, written).Span);
            return result;
        });

        _ = Task.Run(() => _httpClient.GetAsync(new Uri($"http://{IPAddress.Loopback}:{_fixture.Config.Port}")));

        var result = await receiverTask;

        _fixture.TestOutputHelper.WriteLine(result);
    }

    [Fact]
    public async Task WhenSendingLargeMessage_ItShouldReceiveTheMessage()
    {
        _fixture.Config.PacketSize = 1024;
        await _fixture.StartLocalForwarderAsync();
        await _fixture.StartRemoteListenerAsync();

        var receiveStreamTask = _fixture.AcceptListenerConnectionAsync();
        await _client.ConnectAsync(IPAddress.Loopback, _fixture.Config.Port, _fixture.Cancel.Token);

        await using var receiveStream = await receiveStreamTask;

        var stream = _client.GetStream();

        using var buffer = MemoryPool<byte>.Shared.Rent(_fixture.Config.PacketSize * 3);
        using var readBuffer = MemoryPool<byte>.Shared.Rent(_fixture.Config.PacketSize);

        var message = "Big message.";
        var curr = 0;
        var finalMessage = string.Empty;
        for (var messageSize = _fixture.Config.PacketSize * 2; messageSize >= 0; messageSize -= message.Length)
        {
            finalMessage += message + curr + "|";
            curr += message.Length;
        }

        Encoding.UTF8.GetBytes(finalMessage).CopyTo(buffer.Memory);
        var stopwatch = Stopwatch.StartNew();
        await stream.WriteAsync(buffer.Memory.Slice(0, finalMessage.Length), _fixture.Cancel.Token);

        var result = string.Empty;
        while (result.Length < finalMessage.Length)
        {
            var written = await receiveStream.ReadAsync(readBuffer.Memory, _fixture.Cancel.Token);
            result += Encoding.UTF8.GetString(readBuffer.Memory.Slice(0, written).Span);
        }

        stopwatch.Stop();

        _fixture.TestOutputHelper.WriteLine($"Time it took: {stopwatch.Elapsed.Milliseconds}");

        Assert.Equal(finalMessage, result);
    }

    [Fact]
    public async Task WhenSendingMessage_ItShouldReceiveMessage()
    {
        await _fixture.StartLocalForwarderAsync();
        await _fixture.StartRemoteListenerAsync();

        var receiveStreamTask = _fixture.AcceptListenerConnectionAsync();
        await _client.ConnectAsync(IPAddress.Loopback, _fixture.Config.Port, _fixture.Cancel.Token);
        await using var receiveStream = await receiveStreamTask;
        var stream = _client.GetStream();
        var message = "This is a test message.";
        var data = Encoding.UTF8.GetBytes(message);
        await stream.WriteAsync(data.AsMemory(), _fixture.Cancel.Token);

        using var readBuffer = MemoryPool<byte>.Shared.Rent(_fixture.Config.PacketSize);
        var written = await receiveStream.ReadAsync(readBuffer.Memory, _fixture.Cancel.Token);
        var result = Encoding.UTF8.GetString(readBuffer.Memory.Span.Slice(0, written));

        Assert.Equal(message, result);
    }

    [Fact]
    public async Task WhenSendingMultipleMessages_ItShouldReceiveMultipleMessages()
    {
        await _fixture.StartLocalForwarderAsync();
        await _fixture.StartRemoteListenerAsync();

        var receiveStreamTask = _fixture.AcceptListenerConnectionAsync();
        await _client.ConnectAsync(IPAddress.Loopback, _fixture.Config.Port, _fixture.Cancel.Token);
        await using var receiveStream = await receiveStreamTask;

        var stream = _client.GetStream();

        using var buffer = MemoryPool<byte>.Shared.Rent(_fixture.Config.PacketSize);
        using var readBuffer = MemoryPool<byte>.Shared.Rent(_fixture.Config.PacketSize);

        var message = "First message.";
        Encoding.UTF8.GetBytes(message).CopyTo(buffer.Memory);
        await stream.WriteAsync(buffer.Memory.Slice(0, message.Length), _fixture.Cancel.Token);

        var written = await receiveStream.ReadAsync(readBuffer.Memory, _fixture.Cancel.Token);
        var result = Encoding.UTF8.GetString(readBuffer.Memory.Slice(0, written).Span);

        Assert.Equal(message, result);

        message = "Second message.";
        Encoding.UTF8.GetBytes(message).CopyTo(buffer.Memory);
        await stream.WriteAsync(buffer.Memory.Slice(0, message.Length), _fixture.Cancel.Token);

        written = await receiveStream.ReadAsync(readBuffer.Memory, _fixture.Cancel.Token);
        result = Encoding.UTF8.GetString(readBuffer.Memory.Slice(0, written).Span);

        Assert.Equal(message, result);

        message = "Third message.";
        Encoding.UTF8.GetBytes(message).CopyTo(buffer.Memory);
        await stream.WriteAsync(buffer.Memory.Slice(0, message.Length), _fixture.Cancel.Token);

        written = await receiveStream.ReadAsync(readBuffer.Memory, _fixture.Cancel.Token);
        result = Encoding.UTF8.GetString(readBuffer.Memory.Slice(0, written).Span);

        Assert.Equal(message, result);
    }
}