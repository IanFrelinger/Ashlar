using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Ashlar.AI.Pipeline.Clients;

if (args.Length != 2)
    throw new ArgumentException("Expected a JSON region and an empty profile file.");

// This test-only process must not consult the user's Windows SDK store or profiles.
// The parent supplies an empty file and clears AWS_* before starting this process.
AWSConfigs.DisableLegacyPersistenceStore = true;
AWSConfigs.AWSProfilesLocation = args[1];
AWSConfigs.AWSProfileName = "ashlar-region-probe";
AWSConfigs.AWSRegion = null;
var defaultRegion = new AmazonBedrockRuntimeConfig().RegionEndpoint?.SystemName;
var seam = typeof(AwsBedrockChatClientFactory)
    .GetMethod("RuntimeConfig", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException("The Bedrock configuration seam is missing.");
var config = (AmazonBedrockRuntimeConfig)seam.Invoke(null, [JsonSerializer.Deserialize<string?>(args[0])])!;
Console.WriteLine(JsonSerializer.Serialize(new
{
    defaultRegion,
    region = config.RegionEndpoint?.SystemName,
    config.AllowAutoRedirect,
    runtimeMajor = Environment.Version.Major,
    factorySha256 = Convert.ToHexString(SHA256.HashData(
        File.ReadAllBytes(typeof(AwsBedrockChatClientFactory).Assembly.Location)))
}));
