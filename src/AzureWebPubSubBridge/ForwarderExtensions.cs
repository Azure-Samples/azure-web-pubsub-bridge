using Azure;
using Azure.Identity;
using AzureWebPubSubBridge.Protocols;
using AzureWebPubSubBridge.Protocols.Socks;
using AzureWebPubSubBridge.Tunnels;
using AzureWebPubSubBridge.Tunnels.Connections;
using AzureWebPubSubBridge.Tunnels.PubSub;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;

namespace AzureWebPubSubBridge;

public static class ForwarderExtensions
{
    public static IServiceCollection AddDirectLocalForwarder(this IServiceCollection services,
        bool? useReliableWebSockets, BridgeConfiguration configuration)
    {
        services.AddLocalForwarder(useReliableWebSockets, configuration);
        services.AddScoped<IBridgeProtocol, DirectProtocol>();

        return services;
    }

    public static IServiceCollection AddDirectProtocol(this IServiceCollection services)
    {
        services.AddScoped<IBridgeProtocol, DirectProtocol>();

        return services;
    }

    public static IServiceCollection AddDirectRemoteForwarder(this IServiceCollection services,
        bool? useReliableWebSockets, BridgeConfiguration configuration)
    {
        services.AddRemoteForwarder(useReliableWebSockets, configuration);
        services.AddScoped<IBridgeProtocol, DirectProtocol>();

        return services;
    }

    public static IServiceCollection AddLocalForwarder(this IServiceCollection services, bool? useReliableWebSockets,
        BridgeConfiguration configuration)
    {
        services.AddForwarder(useReliableWebSockets, configuration);

        services.AddScoped<PubSubTunnelClient>();

        services.AddHostedService<LocalForwarder>();

        return services;
    }

    public static IServiceCollection AddRemoteForwarder(this IServiceCollection services, bool? useReliableWebSockets,
        BridgeConfiguration configuration)
    {
        services.AddForwarder(useReliableWebSockets, configuration);

        services.AddScoped<PubSubTunnelListener>();

        services.AddHostedService<RemoteForwarder>();

        return services;
    }

    public static IServiceCollection AddSocksLocalForwarder(this IServiceCollection services,
        bool? useReliableWebSockets, BridgeConfiguration configuration)
    {
        services.AddLocalForwarder(useReliableWebSockets, configuration);
        services.AddScoped<IBridgeProtocol, SocksProtocol>();

        return services;
    }

    public static IServiceCollection AddSocksProtocol(this IServiceCollection services)
    {
        services.AddScoped<IBridgeProtocol, SocksProtocol>();

        return services;
    }

    public static IServiceCollection AddSocksRemoteForwarder(this IServiceCollection services,
        bool? useReliableWebSockets, BridgeConfiguration configuration)
    {
        services.AddRemoteForwarder(useReliableWebSockets, configuration);
        services.AddScoped<IBridgeProtocol, SocksProtocol>();

        return services;
    }

    private static IServiceCollection AddForwarder(this IServiceCollection services, bool? useReliableWebSockets,
        BridgeConfiguration configuration)
    {
        services.AddAzureClients(b =>
        {
            if (string.IsNullOrEmpty(configuration.PubSubKey))
                b.AddWebPubSubServiceClient(configuration.PubSubEndpoint, configuration.Hub,
                    new DefaultAzureCredential());
            else
                b.AddWebPubSubServiceClient(configuration.PubSubEndpoint, configuration.Hub,
                    new AzureKeyCredential(configuration.PubSubKey));
        });

        if (useReliableWebSockets ?? false)
            services.AddTransient<PubSubWebSocketConnection, ReliablePubSubWebSocketConnection>();
        else
            services.AddTransient<PubSubWebSocketConnection>();
        services.AddTransient<PubSubTunnelMessageReceiver>();
        services.AddTransient<PubSubTunnelMessageSender>();
        services.AddScoped<IPubSubTunnelFactory, PubSubTunnelFactory>();
        services.AddTransient<PubSubTunnel>();

        return services;
    }
}