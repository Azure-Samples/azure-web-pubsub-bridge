using System.Net;
using System.Net.Sockets;
using System.Text;
using AzureWebPubSubBridge.Messages;
using AzureWebPubSubBridge.Protocols;
using Xunit.Abstractions;

namespace AzureWebPubSubBridge.Tests;

public class RemoteForwarderTests : IClassFixture<DirectBridgeFixture>, IDisposable
{
    private readonly BridgeConnect _bridge;
    private readonly DirectBridgeFixture _fixture;
    private readonly TcpListener _remote;
    private readonly TunnelConnect _tunnelConnect;

    public RemoteForwarderTests(DirectBridgeFixture fixture, ITestOutputHelper testOutputHelper)
    {
        _fixture = fixture;
        _fixture.TestOutputHelper = testOutputHelper;
        _tunnelConnect = new TunnelConnect
        {
            IpAddress = IPAddress.Loopback.ToString(),
            Port = 65435
        };
        _bridge = new BridgeConnect(_fixture.Config.Connect.ServerId, _tunnelConnect);
        _remote = new TcpListener(IPAddress.Loopback, _tunnelConnect.Port);
        _remote.Start();
        _fixture.StartRemoteForwarderAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            _remote.Stop();
            _fixture.StopRemoteForwarderAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // ignored
        }
    }

    [Fact]
    public async Task WhenClosingLocalTunnel_ItShouldSendTunnelDisconnect()
    {
        var remoteTask = Task.Run(async () => await _remote.AcceptTcpClientAsync(_fixture.Cancel.Token));

        await using var localStream =
            await _fixture.Client.ConnectAsync(_bridge, cancellationToken: _fixture.Cancel.Token);

        var remoteClient = await remoteTask;
        var remoteStream = remoteClient.GetStream();

        var data = new byte[1024];

        await remoteStream.DisposeAsync();
        remoteClient.Close();
        try
        {
            var read = await localStream.ReadAsync(data.AsMemory(), _fixture.Cancel.Token);
        }
        catch (Exception e)
        {
            // Check for any OperationCanceledException type (TaskCanceledException).
            Assert.True(e.GetType().IsAssignableTo(typeof(OperationCanceledException)));
        }
    }

    [Fact]
    public async Task WhenClosingTcpConnection_ItShouldSendTunnelDisconnect()
    {
        var remoteTask = Task.Run(async () => await _remote.AcceptTcpClientAsync(_fixture.Cancel.Token));

        var localStream = await _fixture.Client.ConnectAsync(_bridge, cancellationToken: _fixture.Cancel.Token);

        var remoteClient = await remoteTask;
        var remoteStream = remoteClient.GetStream();

        var data = new byte[1024];

        await localStream.DisposeAsync();

        try
        {
            var read = await remoteStream.ReadAsync(data.AsMemory(), _fixture.Cancel.Token);
            Assert.Equal(0, read);
        }
        catch (Exception e)
        {
            // Check for any OperationCanceledException type (TaskCanceledException).
            Assert.True(e.GetType().IsAssignableTo(typeof(OperationCanceledException))
                        || e is IOException);
        }
    }

    [Fact]
    public async Task WhenClosingTcpConnectionAndReopening_ItShouldConnectAndReceiveMessage()
    {
        var remoteTask = Task.Run(async () => await _remote.AcceptTcpClientAsync(_fixture.Cancel.Token));

        var localStream = await _fixture.Client.ConnectAsync(_bridge, cancellationToken: _fixture.Cancel.Token);

        var remoteClient = await remoteTask;
        var remoteStream = remoteClient.GetStream();

        var data = new byte[1024];

        await remoteStream.DisposeAsync();
        remoteClient.Close();
        var read = 0;
        try
        {
            read = await localStream.ReadAsync(data.AsMemory(), _fixture.Cancel.Token);
        }
        catch (Exception e)
        {
            // Check for any OperationCanceledException type (TaskCanceledException).
            Assert.True(e.GetType().IsAssignableTo(typeof(OperationCanceledException)));
        }

        await localStream.DisposeAsync();

        remoteTask = Task.Run(async () => await _remote.AcceptTcpClientAsync(_fixture.Cancel.Token));

        localStream = await _fixture.Client.ConnectAsync(_bridge, cancellationToken: _fixture.Cancel.Token);

        remoteClient = await remoteTask;
        remoteStream = remoteClient.GetStream();

        var message = "This is a message.";
        Encoding.UTF8.GetBytes(message).AsSpan().CopyTo(data.AsSpan());
        await localStream.WriteAsync(data.AsMemory().Slice(0, message.Length), _fixture.Cancel.Token);

        data.AsSpan().Clear();

        read = await remoteStream.ReadAsync(data.AsMemory(), _fixture.Cancel.Token);

        var actual = Encoding.UTF8.GetString(data.AsSpan().Slice(0, read));
        Assert.Equal(message, actual);
    }

    [Fact]
    public async Task WhenSendingMessageFromLocal_ItShouldReceiveMessageOnRemote()
    {
        var remoteTask = Task.Run(async () => await _remote.AcceptTcpClientAsync(_fixture.Cancel.Token));

        await using var localStream = await _fixture.Client.ConnectAsync(_bridge, clientId: "test-client",
            cancellationToken: _fixture.Cancel.Token);

        var remoteClient = await remoteTask;
        var remoteStream = remoteClient.GetStream();

        var data = new byte[1024];

        var message = "This is a message.";
        Encoding.UTF8.GetBytes(message).AsSpan().CopyTo(data.AsSpan());
        await localStream.WriteAsync(data.AsMemory().Slice(0, message.Length), _fixture.Cancel.Token);

        data.AsSpan().Clear();

        var read = await remoteStream.ReadAsync(data.AsMemory(), _fixture.Cancel.Token);

        var actual = Encoding.UTF8.GetString(data.AsSpan().Slice(0, read));
        Assert.Equal(message, actual);
    }

    [Fact]
    public async Task WhenSendingMessageFromRemote_ItShouldReceiveMessageOnLocal()
    {
        var remoteTask = Task.Run(async () => await _remote.AcceptTcpClientAsync(_fixture.Cancel.Token));

        await using var localStream =
            await _fixture.Client.ConnectAsync(_bridge, cancellationToken: _fixture.Cancel.Token);

        var remoteClient = await remoteTask;
        var remoteStream = remoteClient.GetStream();

        var data = new byte[1024];

        var message = "This is a response message.";
        Encoding.UTF8.GetBytes(message).AsSpan().CopyTo(data.AsSpan());
        await remoteStream.WriteAsync(data.AsMemory().Slice(0, message.Length), _fixture.Cancel.Token);

        data.AsSpan().Clear();

        var read = await localStream.ReadAsync(data.AsMemory(), _fixture.Cancel.Token);

        var actual = Encoding.UTF8.GetString(data.AsSpan().Slice(0, read));
        Assert.Equal(message, actual);
    }
}