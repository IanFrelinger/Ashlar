using Ashlar.Abstractions.Security.Egress;
using Ashlar.AI.Pipeline.Clients;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ashlar.AI.Pipeline.Governance;

/// <summary>
/// Where a governed MEAI target sends its requests, as the report-only <see cref="EgressGuardChatClient"/> records it
/// (SPEC-007 PR 3; read from the inner client since PR 4.1).
/// </summary>
/// <remarks>
/// <para>The destination comes from the inner client, not from the key, because a host can register any inner client
/// under any key (<c>AddAshlarMeaiPipeline</c>'s inner factories, <c>AddAshlarGovernedChatClient</c>). The first rule
/// that applies wins:</para>
/// <list type="number">
/// <item>The inner client's <see cref="ChatClientMetadata.ProviderUri"/>, when it is an absolute URI, is the
/// destination. The default <see cref="OllamaHttpChatClient"/> reports its base address, which
/// <see cref="OllamaEndpointResolver.ResolveBaseUrl"/> resolved, so the default <c>local:ollama</c> record is the URL it
/// dials. The site is <c>EG-MDL-01</c> for <c>local:ollama</c>, <c>EG-MDL-02</c> for <c>cloud:bedrock:*</c>, and
/// <c>meai:&lt;key&gt;</c> otherwise.</item>
/// <item><c>local:onnx</c> records nothing only when the inner client is, or delegates to, the in-process
/// <see cref="LlamaSharpChatClient"/>. That is a type check, not the provider name a client reports.</item>
/// <item><c>cloud:bedrock:*</c>: <c>https://bedrock-runtime.{region}.amazonaws.com</c> for the configured
/// <c>Ashlar:Meai:Bedrock:Region</c>, trimmed, when it is a region name (ASCII letters, digits and <c>-</c>); otherwise
/// the name <c>aws-bedrock</c>. With no region the AWS SDK resolves one itself, and this never probes it. Site
/// <c>EG-MDL-02</c>.</item>
/// <item>Anything else, <c>local:ollama</c> or <c>local:onnx</c> with an inner client that names no URI included, fails
/// closed to the name <c>meai:&lt;key&gt;</c>, site <c>meai:&lt;key&gt;</c>: an external model, whatever the key. Every
/// <c>/</c> and <c>\</c> in the key is percent-encoded (<c>%2F</c>, <c>%5C</c>): the key <c>//127.0.0.1</c> is
/// recorded as <c>meai:%2F%2F127.0.0.1</c>.</item>
/// </list>
/// <para>Never throws: it runs while a keyed client is built, and a failure there would stop the client being built. A
/// failure gives the same <c>meai:&lt;key&gt;</c> name.</para>
/// <para>A <c>meai:&lt;key&gt;</c> name holds no <c>/</c> or <c>\</c>. So it has no URI authority, which needs two
/// slashes after the scheme (System.Uri reads <c>\</c> as <c>/</c> there), and it holds no <c>://</c>, which is the
/// only way the egress classifier reads a name as a URL. It is never classified by a host: the default guard records
/// it as an external model (Internal) whatever the key.</para>
/// <para>A <see cref="ChatClientMetadata.ProviderUri"/> names the first hop only. The default
/// <see cref="OllamaHttpChatClient"/> therefore does not follow redirects (PR 4.1).</para>
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
    /// is the in-process LLamaSharp client. Never throws.
    /// </summary>
    /// <param name="targetKey">The governed target key.</param>
    /// <param name="services">The provider the keyed client is built from.</param>
    /// <param name="inner">The client the governance layer wraps; its metadata and type decide the destination.</param>
    public static EgressRequest? Resolve(string targetKey, IServiceProvider services, IChatClient inner)
    {
        try
        {
            return ResolveCore(targetKey, services, inner);
        }
        catch (Exception)
        {
            return Named(targetKey ?? string.Empty);
        }
    }

    private static EgressRequest? ResolveCore(string targetKey, IServiceProvider services, IChatClient inner)
    {
        var isBedrock = targetKey.StartsWith(BedrockKeyPrefix, StringComparison.OrdinalIgnoreCase);

        // 1. Where the inner client says it dials.
        if (inner.GetService(typeof(ChatClientMetadata)) is ChatClientMetadata { ProviderUri: { IsAbsoluteUri: true } dials })
        {
            var site = string.Equals(targetKey, MeaiTargetKeys.LocalOllama, StringComparison.Ordinal) ? OllamaSite
                : isBedrock ? BedrockSite
                : NameOf(targetKey);
            return new EgressRequest(EgressFamilies.ModelMeai, site, dials);
        }

        // 2. The in-process LLamaSharp client sends nothing out of the process.
        if (string.Equals(targetKey, MeaiTargetKeys.LocalOnnx, StringComparison.Ordinal) && InProcessChatClient.IsLlamaSharp(inner))
            return null;

        // 3. Bedrock: the runtime endpoint of the configured region.
        if (isBedrock)
        {
            var region = Options(services)?.Bedrock?.Region?.Trim();
            return IsRegionName(region)
                && Uri.TryCreate("https://bedrock-runtime." + region + ".amazonaws.com", UriKind.Absolute, out var bedrock)
                ? new EgressRequest(EgressFamilies.ModelMeai, BedrockSite, bedrock)
                : new EgressRequest(EgressFamilies.ModelMeai, BedrockSite, BedrockWithoutRegion);
        }

        // 4. Fail closed: the key, an external model.
        return Named(targetKey);
    }

    // An AWS region name is ASCII letters, digits and '-'. Anything else ('#', '/', '?', '@') could move the host of the
    // composed URI, so the fixed name aws-bedrock is recorded instead, which classifies as an external model.
    private static bool IsRegionName(string? region) =>
        !string.IsNullOrEmpty(region) && region.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static EgressRequest Named(string targetKey)
    {
        var name = NameOf(targetKey);
        return new EgressRequest(EgressFamilies.ModelMeai, name, name);
    }

    // A key's '/' and '\' are percent-encoded: "meai://127.0.0.1" would read as a URI whose host is the loopback
    // address, and classify as Host. Plain string replacement, so this cannot throw.
    private static string NameOf(string targetKey) =>
        KeyPrefix + targetKey
            .Replace("/", "%2F", StringComparison.Ordinal)
            .Replace("\\", "%5C", StringComparison.Ordinal);

    // GetService, not GetRequiredService: AddAshlarGovernedChatClient works without AddAshlarMeaiPipeline, so the options
    // may not be registered. A missing region is recorded as aws-bedrock.
    private static MeaiPipelineOptions? Options(IServiceProvider services) =>
        services.GetService<IOptions<MeaiPipelineOptions>>()?.Value;
}
