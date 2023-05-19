using System.Buffers;
using System.Net.Sockets;
using System.Text;
using AzureWebPubSubBridge.Exceptions;
using AzureWebPubSubBridge.Messages;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge.Protocols.Socks;

/// <summary>
///     SOCKS5 Protocol derived from https://datatracker.ietf.org/doc/html/rfc1928. Utilizes username/password
///     negotiation to determine Server ID for the connection https://datatracker.ietf.org/doc/html/rfc1929.
///     Destination fields are ignored as the connection will be made through the Azure Web PubSub Bridge.
/// </summary>
public class SocksProtocol : IBridgeProtocol
{
    private readonly ILogger<SocksProtocol> _logger;

    public SocksProtocol(ILogger<SocksProtocol> logger)
    {
        _logger = logger;
        _logger.LogInformation("Utilizing a SOCKS5 Protocol connection");
    }

    public async ValueTask<BridgeConnect?> ConnectAsync(NetworkStream stream,
        CancellationToken cancellationToken = new())
    {
        try
        {
            return await Establish(stream, cancellationToken);
        }
        catch (SocksConnectionFailedException e)
        {
            _logger.LogError(e, "Failed establishing a SOCKS5 connection, message: {Message}", e.Message);
        }

        return null;
    }

    private async ValueTask<BridgeConnect> Establish(NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = MemoryPool<byte>.Shared.Rent();

        await HandleClientGreeting(stream, buffer.Memory, cancellationToken);

        var serverId = await HandleServerIdNegotiation(stream, buffer.Memory, cancellationToken);

        var connect = await HandleConnectionRequest(stream, buffer.Memory, cancellationToken);

        return new BridgeConnect(serverId, connect);
    }

    private async Task HandleClientGreeting(NetworkStream stream, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        var enough = false;
        do
        {
            var read = await Receive(stream, buffer.Slice(totalRead), cancellationToken);
            totalRead += read;

            enough = ProcessClientGreeting(buffer.Slice(0, totalRead).Span, out var supportedAuth);
            if (!enough) continue;

            // Require Username and Password as Username will indicate the server ID.
            if (!supportedAuth!.Contains((byte)SocksAuthType.UsernamePassword))
            {
                await SendHandshakeResponse(stream, buffer, 0xFF, cancellationToken);
                throw new SocksConnectionFailedException(
                    "Does not support required auth type, 0x02. Username needed for Server ID.");
            }

            await SendHandshakeResponse(stream, buffer, 0x02, cancellationToken);
        } while (!enough);
    }

    private async ValueTask<TunnelConnect> HandleConnectionRequest(NetworkStream stream, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        var enough = false;
        do
        {
            var read = await Receive(stream, buffer, cancellationToken);
            totalRead += read;

            enough = ProcessConnectionRequest(buffer.Slice(0, totalRead).Span, out var result);
            if (!enough) continue;
            if (result?.command != SocksCommandType.Connect)
            {
                // 0x07 = Command not supported
                await SendConnectionResponse(stream, buffer.Slice(0, read), 0x07, cancellationToken);
                throw new SocksConnectionFailedException("Missing Server ID in Username field.");
            }

            await SendConnectionResponse(stream, buffer.Slice(0, read), 0x00, cancellationToken);
            return result?.connect!;
        } while (!enough);

        throw new InvalidOperationException();
    }

    private async ValueTask<string> HandleServerIdNegotiation(NetworkStream stream, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        var enough = false;
        do
        {
            var read = await Receive(stream, buffer, cancellationToken);
            totalRead += read;
            enough = ProcessUsernamePassword(buffer.Slice(0, totalRead).Span, out var result);
            if (!enough) continue;
            // Password is ignored for now.
            if (string.IsNullOrWhiteSpace(result?.serverId))
            {
                await SendHandshakeResponse(stream, buffer, 0xFF, cancellationToken);
                throw new SocksConnectionFailedException("Missing Server ID in Username field.");
            }

            await SendHandshakeResponse(stream, buffer, 0x00, cancellationToken);

            return result?.serverId!;
        } while (!enough);

        throw new InvalidOperationException();
    }

    private bool ProcessClientGreeting(ReadOnlySpan<byte> buffer, out HashSet<byte>? result)
    {
        result = null;
        if (buffer.Length < 2)
            return false;

        if (buffer[0] != 0x05)
            throw new SocksConnectionFailedException(
                $"Client greeting sent an unsupported version, received: {buffer[0]}, required: 0x05");

        if (buffer.Length < buffer[1] + 2)
            return false;

        result = new HashSet<byte>();
        var numMethods = buffer[1];
        for (var i = 0; i < numMethods; i++) result.Add(buffer[2 + i]);

        return true;
    }

    private bool ProcessConnectionRequest(Span<byte> buffer,
        out (SocksCommandType command, TunnelConnect connect)? result)
    {
        result = null;
        if (buffer.Length < 5)
            return false;

        if (buffer[0] != 0x05)
            throw new SocksConnectionFailedException(
                $"Client request sent an unsupported version, received: {buffer[0]}, required: 0x05");

        var command = (SocksCommandType)buffer[1];

        var connect = new TunnelConnect();
        var addressType = (AddressType)buffer[3];
        if (addressType == AddressType.IPv4)
        {
            if (buffer.Length < 10)
                return false;

            connect.IpAddress = $"{buffer[4]:D1}.{buffer[5]:D1}.{buffer[6]:D1}.{buffer[7]:D1}";
            connect.Port = (buffer[8] << 8) | buffer[9];
        }

        if (addressType == AddressType.IPv6)
        {
            if (buffer.Length < 22)
                return false;

            connect.IpAddress =
                $"{buffer[4]:X2}:{buffer[5]:X2}:{buffer[6]:X2}:{buffer[7]:X2}"
                + $"{buffer[8]:X2}:{buffer[9]:X2}:{buffer[10]:X2}:{buffer[11]:X2}"
                + $"{buffer[12]:X2}:{buffer[13]:X2}:{buffer[14]:X2}:{buffer[15]:X2}"
                + $"{buffer[16]:X2}:{buffer[17]:X2}:{buffer[18]:X2}:{buffer[19]:X2}";
            connect.Port = (buffer[20] << 8) | buffer[21];
        }

        if (addressType == AddressType.DomainName)
        {
            var count = buffer[4];
            if (buffer.Length < 5 + count + 2)
                return false;

            connect.DomainName = Encoding.UTF8.GetString(buffer.Slice(5, count));
            connect.Port = (buffer[5 + count] << 8) | buffer[5 + count + 1];
        }

        result = (command, connect);
        return true;
    }

    private bool ProcessUsernamePassword(Span<byte> buffer, out (string serverId, string password)? result)
    {
        result = null;
        if (buffer.Length < 2)
            return false;

        if (buffer[0] != 0x01)
            throw new SocksConnectionFailedException(
                $"Mismatch version for username/password, received: {buffer[0]}, required: 0x01");

        var usernameCount = buffer[1];
        if (buffer.Length < 2 + usernameCount + 1)
            return false;

        var passwordCount = buffer[2 + usernameCount];
        if (buffer.Length < 2 + usernameCount + 1 + passwordCount)
            return false;

        var serverId = Encoding.UTF8.GetString(buffer.Slice(2, usernameCount));
        var password = Encoding.UTF8.GetString(buffer.Slice(2 + usernameCount + 1, passwordCount));
        result = (serverId, password);
        return true;
    }

    private async ValueTask<int> Receive(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        return await stream.ReadAsync(buffer, cancellationToken);
    }

    private async ValueTask Send(NetworkStream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(buffer, cancellationToken);
    }

    private async ValueTask SendConnectionResponse(NetworkStream stream, Memory<byte> buffer, byte reply,
        CancellationToken cancellationToken)
    {
        buffer.Span[1] = reply;
        await Send(stream, buffer, cancellationToken);
    }

    private async ValueTask SendHandshakeResponse(NetworkStream stream, Memory<byte> buffer, byte selection,
        CancellationToken cancellationToken)
    {
        buffer.Span[1] = selection;
        await Send(stream, buffer.Slice(0, 2), cancellationToken);
    }
}