using Ashlar.Abstractions.Security.Egress;
using Ashlar.AI.Pipeline.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ashlar.AI.Pipeline.Governance;

/// <summary>
/// Where a governed MEAI target sends its requests, as the report-only <see cref="EgressGuardChatClient"/> records it
/// (SPEC-007 PR 3).
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term><c>local:ollama</c></term><description>the URL <see cref="OllamaEndpointResolver.ResolveBaseUrl"/> gives,
/// which is what the default <see cref="OllamaHttpChatClient"/> dials; site <c>EG-MDL-01</c>.</description></item>
/// <item><term><c>cloud:bedrock:*</c></term><description><c>https://bedrock-runtime.{region}.amazonaws.com</c> for the
/// configured <c>Ashlar:Meai:Bedrock:Region</c>, trimmed, when it is a region name (ASCII letters, digits and
/// <c>-</c>); otherwise the name <c>aws-bedrock</c>. With no region the AWS SDK resolves one itself, and this never
/// probes it. Site <c>EG-MDL-02</c>.</description></item>
/// <item><term><c>local:onnx</c></term><description>none: LLamaSharp runs in process, so nothing is
/// evaluated.</description></item>
/// <item><term>any other key</term><description>the name <c>meai:&lt;key&gt;</c>, site
/// <c>meai:&lt;key&gt;</c>, with every <c>/</c> and <c>\</c> in the key percent-encoded (<c>%2F</c>, <c>%5C</c>):
/// the key <c>//127.0.0.1</c> is recorded as <c>meai:%2F%2F127.0.0.1</c>.</description></item>
/// </list>
/// <para>Never throws: it runs while a keyed client is built, and a failure there would stop the client being built. A
/// failure gives the same <c>meai:&lt;key&gt;</c> name.</para>
/// <para>A <c>meai:&lt;key&gt;</c> name holds no <c>/</c> or <c>\</c>. So it has no URI authority, which needs two
/// slashes after the scheme (System.Uri reads <c>\</c> as <c>/</c> there), and it holds no <c>://</c>, which is the
/// only way the egress classifier reads a name as a URL. It is never classified by a host: the default guard records
/// it as an external model (Internal) whatever the key.</para>
/// </remarks>
internal static class MeaiEgressDestination
{
    /// <summary>The inventory site of the <c>local:ollama</c> target.</summary>
    public const string OllamaSite = "EG-MDL-01";

    /// <summary>The inventory site of the <c>cloud:bedrock:*</c> targets.</summary>
    public const string BedrockSite = "EG-MDL-02";

    /// <summary>
    /// The destination name recorded for Bedrock when the configured region is absent or is not a region name.
    /// </summary>
    public const string BedrockWithoutRegion = "aws-bedrock";

    /// <summary>The prefix of the site and destination name of a target with no table entry.</summary>
    public const string KeyPrefix = "meai:";

    private const string BedrockKeyPrefix = "cloud:bedrock:";

    /// <summary>
    /// The request each call to <paramref name="targetKey"/> is reported as, or <see langword="null"/> when the target
    /// runs in process. Never throws.
    /// </summary>
    /// <param name="targetKey">The governed target key.</param>
    /// <param name="services">The provider the keyed client is built from.</param>
    public static EgressRequest? Resolve(string targetKey, IServiceProvider services)
    {
        try
        {
            return ResolveCore(targetKey, services);
        }
        catch (Exception)
        {
            return Named(targetKey ?? string.Empty);
        }
    }

    private static EgressRequest? ResolveCore(string targetKey, IServiceProvider services)
    {
        if (string.Equals(targetKey, MeaiTargetKeys.LocalOnnx, StringComparison.Ordinal))
            return null;

        if (string.Equals(targetKey, MeaiTargetKeys.LocalOllama, StringComparison.Ordinal))
        {
            var baseUrl = OllamaEndpointResolver.ResolveBaseUrl(Options(services));
            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var ollama)
                ? new EgressRequest(EgressFamilies.ModelMeai, OllamaSite, ollama)
                : new EgressRequest(EgressFamilies.ModelMeai, OllamaSite, baseUrl);
        }

        if (targetKey.StartsWith(BedrockKeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var region = Options(services)?.Bedrock?.Region?.Trim();
            return IsRegionName(region)
                && Uri.TryCreate("https://bedrock-runtime." + region + ".amazonaws.com", UriKind.Absolute, out var bedrock)
                ? new EgressRequest(EgressFamilies.ModelMeai, BedrockSite, bedrock)
                : new EgressRequest(EgressFamilies.ModelMeai, BedrockSite, BedrockWithoutRegion);
        }

        return Named(targetKey);
    }

    // An AWS region name is ASCII letters, digits and '-'. Anything else ('#', '/', '?', '@') could move the host of the
    // composed URI, so the fixed name aws-bedrock is recorded instead, which classifies as an external model.
    private static bool IsRegionName(string? region) =>
        !string.IsNullOrEmpty(region) && region.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // A key's '/' and '\' are percent-encoded: "meai://127.0.0.1" would read as a URI whose host is the loopback
    // address, and classify as Host. Plain string replacement, so this cannot throw.
    private static EgressRequest Named(string targetKey)
    {
        var name = KeyPrefix + targetKey
            .Replace("/", "%2F", StringComparison.Ordinal)
            .Replace("\\", "%5C", StringComparison.Ordinal);
        return new EgressRequest(EgressFamilies.ModelMeai, name, name);
    }

    // GetService, not GetRequiredService: AddAshlarGovernedChatClient works without AddAshlarMeaiPipeline, so the options
    // may not be registered. The resolver accepts null.
    private static MeaiPipelineOptions? Options(IServiceProvider services) =>
        services.GetService<IOptions<MeaiPipelineOptions>>()?.Value;
}
