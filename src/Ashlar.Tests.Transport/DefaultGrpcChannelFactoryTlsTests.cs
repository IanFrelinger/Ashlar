using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ashlar.Transport.Grpc;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ashlar.Tests.Transport;

public sealed class DefaultGrpcChannelFactoryTlsTests
{
    [Theory]
    [InlineData("server.test", true, true)]
    [InlineData("different.test", true, false)]
    [InlineData("server.test", false, false)]
    public async Task Custom_ca_preserves_hostname_and_chain_validation_during_tls_handshake(
        string certificateHost, bool trustIssuer, bool shouldAccept)
    {
        using var rootKey = RSA.Create(2048);
        using var root = CreateRoot(rootKey);
        using var unrelatedRootKey = RSA.Create(2048);
        using var unrelatedRoot = CreateRoot(unrelatedRootKey);
        using var serverKey = RSA.Create(2048);
        using var serverCertificate = CreateServerCertificate(root, serverKey, certificateHost);
        var caPath = Path.GetTempFileName();
        File.WriteAllText(caPath, (trustIssuer ? root : unrelatedRoot).ExportCertificatePem());

        try
        {
            var factory = new DefaultGrpcChannelFactory(
                Options.Create(new GrpcTransportOptions { CaCertPath = caPath }),
                NullLogger<DefaultGrpcChannelFactory>.Instance);
            using var handler = new HttpClientHandler();
            // Use the factory's actual callback, but drive it with TLS-produced policy errors,
            // rather than synthesizing a name-mismatch flag in a direct callback test.
            var configure = typeof(DefaultGrpcChannelFactory).GetMethod(
                "ConfigureHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
            configure.Invoke(factory, new object[] { handler });
            var validate = handler.ServerCertificateCustomValidationCallback!;
            validate.Should().NotBeNull();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            var accept = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await client.ConnectAsync(IPAddress.Loopback,
                ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var server = await accept;
            using var serverStream = new SslStream(server.GetStream());
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://server.test/");
            SslPolicyErrors? observedErrors = null;
            using var clientStream = new SslStream(client.GetStream(), false,
                (_, certificate, chain, errors) =>
                {
                    observedErrors = errors;
                    using var certificate2 = certificate == null ? null : new X509Certificate2(certificate);
                    return validate(request, certificate2, chain, errors);
                });

            var serverHandshake = Record.ExceptionAsync(() => serverStream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    EnabledSslProtocols = SslProtocols.Tls12,
                }, timeout.Token));
            var clientError = await Record.ExceptionAsync(() => clientStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "server.test",
                    EnabledSslProtocols = SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, timeout.Token));
            // Closing the client also releases the server if the client rejected the certificate.
            clientStream.Dispose();
            var serverError = await serverHandshake;

            observedErrors.Should().NotBeNull("the real TLS validation callback must run");
            observedErrors!.Value.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue(
                "the ephemeral CA is not in the operating system trust store");
            observedErrors.Value.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch).Should()
                .Be(certificateHost != "server.test");
            if (shouldAccept)
            {
                clientError.Should().BeNull("a matching hostname signed by the configured CA is trusted");
                serverError.Should().BeNull();
            }
            else
            {
                clientError.Should().BeOfType<AuthenticationException>(
                    "a wrong hostname or an untrusted issuer must fail TLS authentication");
            }
        }
        finally
        {
            File.Delete(caPath);
        }
    }

    private static X509Certificate2 CreateRoot(RSA key)
    {
        var request = new CertificateRequest("CN=Ashlar TLS test root " + Guid.NewGuid().ToString("N"),
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 CreateServerCertificate(X509Certificate2 issuer, RSA key, string host)
    {
        var request = new CertificateRequest("CN=" + host, key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.Create(issuer, DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }
}
