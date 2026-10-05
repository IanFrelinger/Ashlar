using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Ashlar.Abstractions.Security.Egress;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ashlar.Transport.Grpc;

/// <summary>
/// Default thread-safe implementation of <see cref="IGrpcChannelFactory"/>.
/// </summary>
public sealed class DefaultGrpcChannelFactory : IGrpcChannelFactory
{
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly GrpcTransportOptions _options;
    private readonly ILogger<DefaultGrpcChannelFactory> _logger;
    private readonly bool _isDevelopment;

    public DefaultGrpcChannelFactory(
        IOptions<GrpcTransportOptions> options,
        ILogger<DefaultGrpcChannelFactory> logger)
        : this(options?.Value ?? new GrpcTransportOptions(), logger, IsDevelopmentEnvironment())
    {
    }

    internal DefaultGrpcChannelFactory(
        GrpcTransportOptions options,
        ILogger<DefaultGrpcChannelFactory> logger,
        bool isDevelopment)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isDevelopment = isDevelopment;
        _options.Validate(_isDevelopment);
    }

    public GrpcChannel GetOrCreate(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("Endpoint is required.", nameof(endpoint));
        }

        return _channels.GetOrAdd(endpoint, static (ep, state) => state.CreateChannel(ep), this);
    }

    public void DisposeAll()
    {
        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }
        _channels.Clear();
    }

    private GrpcChannel CreateChannel(string endpoint)
    {
        if (_options.AllowInsecure)
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        }

        var handler = BuildHandler();
        _logger.LogInformation("Creating gRPC channel for {Endpoint}", endpoint);
        return GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
        {
            HttpHandler = handler
        });
    }

    // The guard handler goes in front of the configured HttpClientHandler. Grpc.Net.Client finds the handler type
    // through DelegatingHandler.InnerHandler, so the channel still sees an HttpClientHandler.
    private HttpMessageHandler BuildHandler() =>
        EgressHttp.Wrap(ConfigureHandler(new HttpClientHandler()), EgressFamilies.Grpc, "EG-XPT-03");

    private HttpClientHandler ConfigureHandler(HttpClientHandler handler)
    {
        if (_options.AllowInsecure)
        {
            // Development-only insecure mode: allow unencrypted HTTP/2 and bypass cert validation.
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        ConfigureClientCertificate(handler);
        ConfigureCustomCa(handler);
        return handler;
    }

    private void ConfigureClientCertificate(HttpClientHandler handler)
    {
        if (string.IsNullOrWhiteSpace(_options.ClientCertPath))
        {
            return;
        }

        X509Certificate2 certificate;
        if (!string.IsNullOrWhiteSpace(_options.ClientCertKeyPath))
        {
            certificate = X509Certificate2.CreateFromPemFile(_options.ClientCertPath!, _options.ClientCertKeyPath!);
        }
        else
        {
            // SYSLIB0057 (.NET 9+): the file-path constructor is obsolete, but it is the only loader
            // that auto-detects PFX vs DER/PEM, which this option relies on. Kept deliberately.
#pragma warning disable SYSLIB0057
            certificate = new X509Certificate2(_options.ClientCertPath!);
#pragma warning restore SYSLIB0057
        }

        handler.ClientCertificates.Add(certificate);
    }

    private void ConfigureCustomCa(HttpClientHandler handler)
    {
        if (string.IsNullOrWhiteSpace(_options.CaCertPath))
        {
            return;
        }

        // SYSLIB0057 (.NET 9+): see ConfigureClientCertificate — same auto-detecting file loader.
#pragma warning disable SYSLIB0057
        var caCertificate = new X509Certificate2(_options.CaCertPath!);
#pragma warning restore SYSLIB0057
        handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, sslPolicyErrors) =>
        {
            if (certificate == null)
            {
                return false;
            }

            var localChain = new X509Chain();
            localChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            localChain.ChainPolicy.CustomTrustStore.Add(caCertificate);
            localChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

            return localChain.Build(new X509Certificate2(certificate));
        };
    }

    private static bool IsDevelopmentEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase);
    }
}
