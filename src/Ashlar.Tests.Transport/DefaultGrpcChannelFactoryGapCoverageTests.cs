using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ashlar.Transport.Grpc;
using Xunit;

namespace Ashlar.Tests.Transport;

/// <summary>Tests for default grpc channel factory gap coverage.</summary>
[Collection("GrpcTransportEnvironment")]
public sealed class DefaultGrpcChannelFactoryGapCoverageTests
{
    [Fact]
    public void GetOrCreate_reuses_channel_for_same_endpoint()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var factory = new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions { AllowInsecure = true }),
            NullLogger<DefaultGrpcChannelFactory>.Instance);

        var first = factory.GetOrCreate("http://127.0.0.1:9");
        var second = factory.GetOrCreate("http://127.0.0.1:9");

        ReferenceEquals(first, second).Should().BeTrue();
        factory.DisposeAll();
    }

    [Fact]
    public void CreateChannel_applies_custom_ca_certificate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ashlar-grpc-ca-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var (_, _, caPem, _) = CreateTempCertificates(dir);

        try
        {
            using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
            var factory = new DefaultGrpcChannelFactory(
                Options.Create(new GrpcTransportOptions
                {
                    AllowInsecure = true,
                    CaCertPath = caPem,
                }),
                NullLogger<DefaultGrpcChannelFactory>.Instance);

            factory.GetOrCreate("http://127.0.0.1:9").Should().NotBeNull();
            factory.DisposeAll();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CreateChannel_loads_client_certificate_from_pem_pair()
    {
        if (OperatingSystem.IsMacOS())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "ashlar-grpc-mtls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var (certPem, _, caPem, _) = CreateTempCertificates(dir);
        var keyPkcs8 = Path.Combine(dir, "key.pk8.pem");

        try
        {
            using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
            var factory = new DefaultGrpcChannelFactory(
                Options.Create(new GrpcTransportOptions
                {
                    AllowInsecure = true,
                    ClientCertPath = certPem,
                    ClientCertKeyPath = keyPkcs8,
                    CaCertPath = caPem,
                }),
                NullLogger<DefaultGrpcChannelFactory>.Instance);

            factory.GetOrCreate("http://127.0.0.1:9").Should().NotBeNull();
            factory.DisposeAll();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CreateChannel_custom_ca_validation_callback_rejects_null_certificate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ashlar-grpc-ca-callback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var (_, _, caPem, _) = CreateTempCertificates(dir);

        try
        {
            using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
            var factory = new DefaultGrpcChannelFactory(
                Options.Create(new GrpcTransportOptions
                {
                    AllowInsecure = true,
                    CaCertPath = caPem,
                }),
                NullLogger<DefaultGrpcChannelFactory>.Instance);

            factory.GetOrCreate("http://127.0.0.1:9").Should().NotBeNull();

            var handlerField = typeof(DefaultGrpcChannelFactory).GetField("_options", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var options = (GrpcTransportOptions)handlerField.GetValue(factory)!;
            options.CaCertPath.Should().Be(caPem);

            var buildHandler = typeof(DefaultGrpcChannelFactory).GetMethod("BuildHandler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var guarded = (DelegatingHandler)buildHandler.Invoke(factory, null)!;
            guarded.GetType().FullName.Should().Be("Ashlar.Abstractions.Security.Egress.EgressGuardHandler", "SPEC-007: the channel handler is wrapped by the egress guard");
            var httpHandler = PrimaryHttpClientHandler((DelegatingHandler)buildHandler.Invoke(factory, null)!);
            httpHandler.ServerCertificateCustomValidationCallback.Should().NotBeNull();
            httpHandler.ServerCertificateCustomValidationCallback!(null!, null, null, System.Net.Security.SslPolicyErrors.None)
                .Should().BeFalse();

            factory.DisposeAll();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CreateChannel_custom_ca_validation_callback_accepts_certificate_signed_by_custom_root()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ashlar-grpc-ca-valid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var (certPem, _, caPem, _) = CreateTempCertificates(dir);

        try
        {
            using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
            var factory = new DefaultGrpcChannelFactory(
                Options.Create(new GrpcTransportOptions
                {
                    AllowInsecure = true,
                    CaCertPath = caPem,
                }),
                NullLogger<DefaultGrpcChannelFactory>.Instance);

            factory.GetOrCreate("http://127.0.0.1:9").Should().NotBeNull();

            var buildHandler = typeof(DefaultGrpcChannelFactory).GetMethod("BuildHandler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var httpHandler = PrimaryHttpClientHandler((DelegatingHandler)buildHandler.Invoke(factory, null)!);
            var serverCertificate = new X509Certificate2(caPem);
            httpHandler.ServerCertificateCustomValidationCallback!(
                    null!,
                    serverCertificate,
                    null,
                    System.Net.Security.SslPolicyErrors.None)
                .Should().BeTrue();

            factory.DisposeAll();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CreateChannel_loads_client_certificate_without_separate_key_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ashlar-grpc-pfx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var (_, _, _, pfxPath) = CreateTempCertificates(dir);

        try
        {
            using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
            var factory = new DefaultGrpcChannelFactory(
                Options.Create(new GrpcTransportOptions
                {
                    AllowInsecure = true,
                    ClientCertPath = pfxPath,
                }),
                NullLogger<DefaultGrpcChannelFactory>.Instance);

            factory.GetOrCreate("http://127.0.0.1:9").Should().NotBeNull();
            factory.DisposeAll();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Constructor_null_logger_throws()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        var act = () => new DefaultGrpcChannelFactory(
            Options.Create(new GrpcTransportOptions()),
            null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static HttpClientHandler PrimaryHttpClientHandler(DelegatingHandler guarded)
    {
        HttpMessageHandler? handler = guarded;
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler;
        return (HttpClientHandler)handler!;
    }

    private static (string certPem, string keyPem, string caPem, string pfxPath) CreateTempCertificates(string dir)
    {
        // Generated in-process (no openssl): the shell-out variant was PATH-dependent, capped at
        // 10 s per invocation and flaked on the Windows readiness lane. Same shape as before:
        // cert.pem, PKCS#1 key.pem, PKCS#8 key.pk8.pem, ca.pem (= cert), passwordless client.pfx.
        var certPem = Path.Combine(dir, "cert.pem");
        var keyPem = Path.Combine(dir, "key.pem");
        var keyPkcs8 = Path.Combine(dir, "key.pk8.pem");
        var caPem = Path.Combine(dir, "ca.pem");
        var pfxPath = Path.Combine(dir, "client.pfx");

        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var cert = request.CreateSelfSigned(notBefore, notBefore.AddDays(1));

        File.WriteAllText(certPem, cert.ExportCertificatePem());
        File.WriteAllText(keyPem, rsa.ExportRSAPrivateKeyPem());
        File.WriteAllText(keyPkcs8, rsa.ExportPkcs8PrivateKeyPem());
        File.Copy(certPem, caPem, overwrite: true);
        File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx));

        return (certPem, keyPem, caPem, pfxPath);
    }

    /// <summary>Environment variable scope.</summary>
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _key;
        private readonly string? _priorValue;

        public EnvironmentVariableScope(string key, string? value)
        {
            _key = key;
            _priorValue = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        /// <summary>Dispose.</summary>
        public void Dispose() => Environment.SetEnvironmentVariable(_key, _priorValue);
    }
}
