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
        // AllowAutoRedirect is ClientConfig's; the SDK client follows unless it is off. No Ashlar follower runs inside the SDK.
        IAmazonBedrockRuntime runtime = string.IsNullOrWhiteSpace(_options.Region)
            ? new AmazonBedrockRuntimeClient(new AmazonBedrockRuntimeConfig { AllowAutoRedirect = false })
            : new AmazonBedrockRuntimeClient(new AmazonBedrockRuntimeConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(_options.Region.Trim()),
                AllowAutoRedirect = false,
            });

        return runtime.AsIChatClient(modelId);
    }
}
