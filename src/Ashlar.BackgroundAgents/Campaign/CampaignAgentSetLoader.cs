using System.Text.Json;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Microsoft.Extensions.Configuration;

namespace Ashlar.BackgroundAgents.Campaign;

/// <summary>
/// Loads the dogfood campaign agent set. The JSON is a real
/// <c>BackgroundAgents:Agents</c> document so the same file can be handed to
/// <c>ashlar background-agent daemon --config</c>.
/// </summary>
public static class CampaignAgentSetLoader
{
    /// <summary>Default in-repo agent-set path, relative to the repository root.</summary>
    public const string DefaultRelativePath = "docs/background-agents/examples/dogfood-campaign.json";

    /// <summary>Load and validate the campaign agent set from <paramref name="path"/>.</summary>
    public static async Task<CampaignAgentSet> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Agent-set path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException($"Campaign agent set not found: {path}", path);

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();

        var loader = new BackgroundAgentConfigLoader(configuration, new DataSensitivityRegistry());
        var configs = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);

        var managers = configs
            .Where(c => string.Equals(c.Role, "release-manager", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (managers.Count != 1)
        {
            throw new InvalidOperationException(
                "Campaign agent set must contain exactly one agent with Role 'release-manager'.");
        }

        var manager = managers[0];
        var specialists = new List<CampaignSpecialistSpec>();
        foreach (var config in configs.Where(c => !string.Equals(c.Id, manager.Id, StringComparison.Ordinal)))
        {
            if (!string.Equals(config.ParentId, manager.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Specialist '{config.Id}' must set ParentId to the release manager '{manager.Id}'.");
            }

            if (!TryResolveLane(config, out var lane))
            {
                throw new InvalidOperationException(
                    $"Specialist '{config.Id}' (role '{config.Role}') does not map to a campaign lane. " +
                    "Set Parameters.Lane to DocsDrift, Regression, or DevTool, or use a known role.");
            }

            specialists.Add(new CampaignSpecialistSpec(
                config.Id,
                config.Name,
                config.Role,
                lane,
                config.ParentId,
                FlattenParameters(config.Parameters)));
        }

        if (specialists.Count == 0)
            throw new InvalidOperationException("Campaign agent set has a release manager but no specialists.");

        var lanes = specialists.Select(s => s.Lane).ToHashSet();
        foreach (var required in Enum.GetValues<CampaignLane>())
        {
            if (!lanes.Contains(required))
            {
                throw new InvalidOperationException(
                    $"Campaign agent set is missing a specialist for lane '{required}'.");
            }
        }

        return new CampaignAgentSet(manager.Id, manager.Name, specialists);
    }

    internal static bool TryResolveLane(BackgroundAgentConfig config, out CampaignLane lane)
    {
        if (config.Parameters != null &&
            config.Parameters.TryGetValue("Lane", out var raw) &&
            raw is not null &&
            Enum.TryParse<CampaignLane>(Convert.ToString(raw), ignoreCase: true, out lane))
        {
            return true;
        }

        if (string.Equals(config.Role, "docs-auditor", StringComparison.OrdinalIgnoreCase))
        {
            lane = CampaignLane.DocsDrift;
            return true;
        }

        if (string.Equals(config.Role, "regression-auditor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(config.Role, "tester", StringComparison.OrdinalIgnoreCase))
        {
            lane = CampaignLane.Regression;
            return true;
        }

        if (string.Equals(config.Role, "dev-tool-auditor", StringComparison.OrdinalIgnoreCase))
        {
            lane = CampaignLane.DevTool;
            return true;
        }

        lane = default;
        return false;
    }

    private static IReadOnlyDictionary<string, string>? FlattenParameters(Dictionary<string, object>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return null;

        var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters)
        {
            if (value is null)
                continue;
            if (value is JsonElement element)
                flat[key] = element.ToString();
            else
                flat[key] = Convert.ToString(value) ?? string.Empty;
        }

        return flat;
    }
}
