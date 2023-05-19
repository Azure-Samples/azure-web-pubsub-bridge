using System.Collections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace AzureWebPubSubBridge;

public static class BridgeHost
{
    public static IHost CreateLocalDirect(Action<BridgeConfiguration> configurationBuilder,
        bool useReliableWebSockets = false)
    {
        var host = CreateDefault();

        host.ConfigureServices((context, services) =>
        {
            var options =
                context.Configuration.GetSection(BridgeConfiguration.Local).Get<BridgeConfiguration>()
                ?? new BridgeConfiguration();
            configurationBuilder(options);
            services.AddSingleton(options);
            services.AddDirectProtocol();
            services.AddLocalForwarder(useReliableWebSockets, options);
        });
        return host.Build();
    }

    public static IHost CreateLocalSocks(Action<BridgeConfiguration> configurationBuilder,
        bool useReliableWebSockets = false)
    {
        var host = CreateDefault();

        host.ConfigureServices((context, services) =>
        {
            var options =
                context.Configuration.GetSection(BridgeConfiguration.Local).Get<BridgeConfiguration>()
                ?? new BridgeConfiguration();
            configurationBuilder(options);
            services.AddSingleton(options);
            services.AddSocksProtocol();
            services.AddLocalForwarder(useReliableWebSockets, options);
        });
        return host.Build();
    }

    public static IHost CreateRemote(Action<BridgeConfiguration> configurationBuilder,
        bool useReliableWebSockets = false)
    {
        var host = CreateDefault();

        host.ConfigureServices((context, services) =>
        {
            var options =
                context.Configuration.GetSection(BridgeConfiguration.Remote).Get<BridgeConfiguration>()
                ?? new BridgeConfiguration();
            configurationBuilder(options);
            services.AddSingleton(options);
            services.AddRemoteForwarder(useReliableWebSockets, options);
        });
        return host.Build();
    }

    private static IHostBuilder CreateDefault()
    {
        Console.WriteLine("Starting new host");
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) Console.WriteLine(e.Key + "=" + e.Value);
        var host = Host.CreateDefaultBuilder();
        host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(theme: AnsiConsoleTheme.Code));
        return host;
    }
}