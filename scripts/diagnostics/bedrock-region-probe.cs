// Diagnostic-only source, temporarily compiled by bedrock-region-reproduction.py.
// No AWS clients, credential resolution, requests, cache reset, or OS isolation tests.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Ashlar.AI.Pipeline.Clients;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Diagnostics;

public sealed class BedrockRegionDiagnosticProbe
{
    private const string InjectedSource = "/repo/src/Ashlar.Tests.Infrastructure/Tests/BedrockRegionDiagnosticProbe.cs";
#if NET8_0
    private const string CompiledTarget = "net8.0";
#elif NET10_0
    private const string CompiledTarget = "net10.0";
#else
#error The diagnostic requires an explicit net8.0 or net10.0 build.
#endif

    [Fact]
    public void Controlled_default_region_and_factory_configuration_are_observed_without_a_client()
    {
        Assert.True(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), "This diagnostic is Linux-only.");
        Assert.Equal("true", Environment.GetEnvironmentVariable("AWS_EC2_METADATA_DISABLED"));
        var output = Environment.GetEnvironmentVariable("ASHLAR_BEDROCK_PROBE_OUTPUT");
        Assert.False(string.IsNullOrWhiteSpace(output));
        var evidenceRoot = Directory.GetParent(Path.GetDirectoryName(output!)!)?.Parent?.Parent;
        Assert.NotNull(evidenceRoot);
        Assert.StartsWith("ashlar-bedrock-region-", evidenceRoot!.Name);
        Assert.Equal("/tmp", evidenceRoot.Parent?.FullName);
        foreach (var (variable, file) in new[] { ("AWS_SHARED_CREDENTIALS_FILE", "credentials"), ("AWS_CONFIG_FILE", "config") })
        {
            var expectedPath = Path.Combine(evidenceRoot.FullName, "synthetic-config", file);
            Assert.Equal(expectedPath, Environment.GetEnvironmentVariable(variable));
            Assert.True(File.Exists(expectedPath), "Synthetic profile files must exist before SDK initialization.");
        }
        var expected = Environment.GetEnvironmentVariable("ASHLAR_BEDROCK_EXPECT_REGION");
        Assert.Contains(expected, new string?[] { "none", "us-west-2", "eu-central-1" });
        var expectedRegion = expected == "none" ? null : expected;
        Assert.True(string.IsNullOrEmpty(AWSConfigs.AWSProfilesLocation), "An SDK profile-path override would invalidate the synthetic files.");
        Assert.True(string.IsNullOrEmpty(AWSConfigs.AWSRegion), "An SDK app-config region would invalidate the controlled environment.");
        var cacheField = typeof(FallbackRegionFactory).GetField("cachedRegion", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(cacheField); // A changed SDK shape is a diagnostic failure, never a guessed provider.
        var before = Observe(cacheField!);
        var defaultConfig = new AmazonBedrockRuntimeConfig();
        var defaultRegion = defaultConfig.RegionEndpoint?.SystemName;
        var afterDefault = Observe(cacheField!);
        var seam = typeof(AwsBedrockChatClientFactory).GetMethod("RuntimeConfig", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(seam);
        var observations = new List<object>();
        var actual = new List<(string? Input, string? Region, bool Redirects)>();
        foreach (var input in new string?[] { "us-west-2", " us-west-2 ", null, "" })
        {
            var config = Assert.IsType<AmazonBedrockRuntimeConfig>(seam!.Invoke(null, new object?[] { input }));
            var region = config.RegionEndpoint?.SystemName;
            actual.Add((input, region, config.AllowAutoRedirect));
            observations.Add(new { input, region, allow_auto_redirect = config.AllowAutoRedirect });
        }
        var afterFactory = Observe(cacheField!);
        var receipt = new
        {
            source_sha = Environment.GetEnvironmentVariable("ASHLAR_BEDROCK_SOURCE_SHA"),
            injected_source_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(InjectedSource))).ToLowerInvariant(),
            mode = expected,
            runtime = new { framework = RuntimeInformation.FrameworkDescription, version = Environment.Version.ToString(), compiled_target = CompiledTarget, entry_target = AppContext.TargetFrameworkName },
            loaded_assemblies = new[] { VersionOf(typeof(RegionEndpoint).Assembly), VersionOf(typeof(AmazonBedrockRuntimeConfig).Assembly) },
            cache_before = before,
            default_region = defaultRegion,
            cache_after_default = afterDefault,
            factory_cases = observations,
            cache_after_factory = afterFactory
        };
        File.WriteAllText(output!, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Null(before.Provider);
        Assert.Null(before.Region);
        Assert.Equal(expectedRegion, defaultRegion);
        Assert.Equal(expectedRegion is null ? null : "EnvironmentVariableAWSRegion", afterDefault.Provider);
        Assert.Equal(expectedRegion, afterDefault.Region);
        Assert.Equal(afterDefault, afterFactory);
        foreach (var item in actual)
        {
            Assert.False(item.Redirects);
            Assert.Equal(string.IsNullOrWhiteSpace(item.Input) ? expectedRegion : "us-west-2", item.Region);
        }
    }

    private sealed record ProviderObservation(string? Provider, string? Region);

    private static ProviderObservation Observe(FieldInfo field)
    {
        var cached = field.GetValue(null);
        if (cached is null)
            return new(null, null);
        var type = cached.GetType();
        Assert.Contains(type.Name, new[] { "AppConfigAWSRegion", "EnvironmentVariableAWSRegion", "ProfileAWSRegion", "InstanceProfileAWSRegion" });
        var property = type.GetProperty("Region", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        var region = Assert.IsType<RegionEndpoint>(property!.GetValue(cached));
        return new(type.Name, region.SystemName); // Never serialize a provider, profile, or exception object.
    }

    private static object VersionOf(Assembly assembly) => new
    {
        name = assembly.GetName().Name,
        assembly_version = assembly.GetName().Version?.ToString(),
        file_version = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version,
        informational_version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    };
}
