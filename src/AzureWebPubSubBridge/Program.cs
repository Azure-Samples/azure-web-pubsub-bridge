using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Hosting;

namespace AzureWebPubSubBridge;

public static class Program
{
    private static readonly RootCommand RootCommand =
        new("An application to bridge TCP connections using Azure Web PubSub.");

    private static readonly Option<int?> PortOption = new("--port", () => null, "The port to listen on.");

    private static readonly Option<Uri?> PubSubEndpointOption =
        new("--pubsub-endpoint", () => null, "The Azure Web PubSub endpoint.");

    private static readonly Option<string?> PubSubKey = new("--pubsub-key", () => null, "The Azure Web PubSub Key.");

    private static readonly Option<string?> HubOption = new("--pubsub-hub-name", () => null,
        "The Azure Web PubSub hub name.");

    private static readonly Option<bool?> ReliableOption =
        new("--reliable", () => false, "Use reliable websockets (preview).");

    private static readonly Option<int?> PacketSizeOption = new("--packet-size", () => null,
        "The maximum size of the packet in bytes sent through Azure Web PubSub.");

    private static readonly Option<int?> DefaultRequestTimeoutSecondsOption = new("--request-timeout", () => null,
        "The amount of time before a TCP connection is closed.");

    private static readonly Option<string?> ServerIdOption = new("--server-id", () => null,
        "The server ID registered with the Azure Web PubSub" +
        " that will receive the TCP traffic.");

    private static readonly Option<bool?> StopOnErrorOption =
        new("--stop-on-error", () => null, "Stop when a bridge closes due to an unhandled exception.");

    private static readonly Option<string?> IpOption = new("--ip", () => null, "The IP Address to bridge.");
    private static readonly Option<string?> DomainOption = new("--domain", () => null, "The Domain Address to bridge.");
    private static readonly Option<int?> DirectPortOption = new("--direct-port", () => null, "The port to bridge.");

    public static async Task Main(string[] args)
    {
        Command localCommand = new("local", "Start a local bridge, listening for TCP traffic on a bound port.")
        {
            StopOnErrorOption,
            ReliableOption,
            PacketSizeOption,
            PortOption,
            PubSubEndpointOption,
            PubSubKey,
            HubOption
        };
        Command remoteCommand = new("remote", "Start a remote bridge, listening for TCP traffic on a bound port.")
        {
            StopOnErrorOption,
            ServerIdOption,
            ReliableOption,
            PacketSizeOption,
            PubSubEndpointOption,
            PubSubKey,
            HubOption
        };

        Command directCommand = new("direct", "Start a direct local bridge, connecting to only one IP/Domain.")
        {
            ServerIdOption,
            IpOption,
            DomainOption,
            DirectPortOption
        };

        Command socksCommand = new("socks",
            "Start a SOCKS5 local bridge, accepts multiple connections to multiple remote servers.");

        localCommand.AddCommand(socksCommand);
        localCommand.AddCommand(directCommand);

        RootCommand.AddCommand(localCommand);
        RootCommand.AddCommand(remoteCommand);

        remoteCommand.SetHandler(async invokeContext =>
        {
            var host = BridgeHost.CreateRemote(c => UpdateConfiguration(c, invokeContext),
                GetValue(invokeContext, ReliableOption) ?? false);
            await host.RunAsync();
        });

        directCommand.SetHandler(async invokeContext =>
        {
            var host = BridgeHost.CreateLocalDirect(c => UpdateConfiguration(c, invokeContext),
                GetValue(invokeContext, ReliableOption) ?? false);
            await host.RunAsync();
        });

        socksCommand.SetHandler(async invokeContext =>
        {
            var host = BridgeHost.CreateLocalSocks(c => UpdateConfiguration(c, invokeContext),
                GetValue(invokeContext, ReliableOption) ?? false);
            await host.RunAsync();
        });

        await RootCommand.InvokeAsync(args);
    }

    private static TValue? GetValue<TValue>(InvocationContext context, Option<TValue> option)
    {
        return context.ParseResult.GetValueForOption(option);
    }

    private static void UpdateConfiguration(BridgeConfiguration configuration, InvocationContext invocationContext)
    {
        configuration.Port = GetValue(invocationContext, PortOption) ?? configuration.Port;
        configuration.PubSubEndpoint =
            GetValue(invocationContext, PubSubEndpointOption) ?? configuration.PubSubEndpoint;
        configuration.PubSubKey =
            GetValue(invocationContext, PubSubKey) ?? configuration.PubSubKey;
        configuration.Hub = GetValue(invocationContext, HubOption) ?? configuration.Hub;
        configuration.PacketSize = GetValue(invocationContext, PacketSizeOption) ?? configuration.PacketSize;
        configuration.DefaultRequestTimeoutSeconds =
            GetValue(invocationContext, DefaultRequestTimeoutSecondsOption) ??
            configuration.DefaultRequestTimeoutSeconds;
        configuration.StopOnError = GetValue(invocationContext, StopOnErrorOption) ?? configuration.StopOnError;
        configuration.Connect.ServerId =
            GetValue(invocationContext, ServerIdOption) ?? configuration.Connect.ServerId;
    }
}