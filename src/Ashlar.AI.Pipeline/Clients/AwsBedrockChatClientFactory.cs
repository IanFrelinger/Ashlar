using Amazon;
using Amazon.BedrockRuntime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ashlar.AI.Pipeline.Clients;

/// <summary>
/// Creates Bedrock-backed <see cref="IChatClient"/> instances without registering
/// <see cref="IAmazonBedrockRuntime"/> / <see cref="AmazonBedrockRuntimeClient"/> in DI.
/// Uses the same default AWS credential/region chain as DynamoDB SMS ingress.
/// </summary>
public interface IBedrockChatClientFactory
{
    /// <summary>Creates an <see cref="IChatClient"/> for the given Bedrock model id.</summary>
    IChatClient Create(string modelId);
}

/// <summary>
/// Production factory over AWSSDK Bedrock MEAI adapter.
/// </summary>
public sealed class AwsBedrockChatClientFactory : IBedrockChatClientFactory
{
    private readonly BedrockMeaiOptions _options;

    /// <summary>Creates the factory from pipeline options.</summary>
    public AwsBedrockChatClientFactory(IOptions<MeaiPipelineOptions> options)
    {
        _options = options.Value.Bedrock;
    }

    /// <inheritdoc />
    public IChatClient Create(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        // Construct privately — never AddSingleton<IAmazonBedrockRuntime>.
        IAmazonBedrockRuntime runtime = new AmazonBedrockRuntimeClient(RuntimeConfig(_options.Region));
        return runtime.AsIChatClient(modelId);
    }

    /// <summary>
    /// The runtime client's configuration: the configured region when there is one (otherwise the SDK's own default
    /// chain), and never following a redirect (SPEC-007 PR 4.3). The SDK owns its HTTP, below every handler Ashlar can
    /// place, so a redirect it followed would reach a host no decision names.
    /// </summary>
    internal static AmazonBedrockRuntimeConfig RuntimeConfig(string? region)
    {
        var config = new AmazonBedrockRuntimeConfig { AllowAutoRedirect = false };
        if (!string.IsNullOrWhiteSpace(region))
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region.Trim());
        return config;
    }
}
