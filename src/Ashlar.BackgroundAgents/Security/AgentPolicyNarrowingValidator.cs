using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Registry;

namespace Ashlar.BackgroundAgents.Security;

/// <summary>
/// Enforces child ⊆ creator exfiltration/sensitivity envelope at machine-origin registration.
/// Authored registrations are trust roots and skip validation.
/// </summary>
public static class AgentPolicyNarrowingValidator
{
    public static void ValidateOrThrow(
        BackgroundAgentConfig child,
        AgentRegistrationOrigin origin,
        Func<string, BackgroundAgentInstance?> resolveParent,
        IDataSensitivityRegistry sensitivityRegistry)
    {
        if (origin == AgentRegistrationOrigin.Authored)
            return;

        if (string.IsNullOrWhiteSpace(child.ParentId))
        {
            throw new InvalidOperationException(
                $"Refusing machine-origin agent '{child.Id}': ParentId is required.");
        }

        if (sensitivityRegistry is null)
        {
            throw new InvalidOperationException(
                $"Refusing machine-origin agent '{child.Id}': sensitivity registry unavailable to verify parent envelope.");
        }

        var parentInstance = resolveParent(child.ParentId);
        if (parentInstance is null)
        {
            throw new InvalidOperationException(
                $"Refusing machine-origin agent '{child.Id}': parent '{child.ParentId}' is not registered.");
        }

        var parent = parentInstance.Config;
        var parentPolicy = parent.ExfiltrationPolicy ?? new ExfiltrationPolicy();
        var childPolicy = child.ExfiltrationPolicy ?? new ExfiltrationPolicy();

        if (parentPolicy.BlockExternalLLMs && !childPolicy.BlockExternalLLMs)
        {
            throw new InvalidOperationException(
                $"Refusing machine-origin agent '{child.Id}': BlockExternalLLMs cannot be relaxed below parent '{parent.Id}'.");
        }

        if (parentPolicy.BlockWebSearch && !childPolicy.BlockWebSearch)
        {
            throw new InvalidOperationException(
                $"Refusing machine-origin agent '{child.Id}': BlockWebSearch cannot be relaxed below parent '{parent.Id}'.");
        }

        if (parentPolicy.BlockNetworkExports && !childPolicy.BlockNetworkExports)
        {
            throw new InvalidOperationException(
                $"Refusing machine-origin agent '{child.Id}': BlockNetworkExports cannot be relaxed below parent '{parent.Id}'.");
        }

        if (parentPolicy.RequireLocalOnly && !childPolicy.RequireLocalOnly)
        {
            throw new InvalidOperationException(
                $"Refusing machine-origin agent '{child.Id}': RequireLocalOnly cannot be relaxed below parent '{parent.Id}'.");
        }

        EnsureLevelNotBroader(
            child.Id,
            parent.Id,
            "MaxAllowedLevel",
            parentPolicy.MaxAllowedLevel ?? parent.MaxDataSensitivity,
            childPolicy.MaxAllowedLevel ?? child.MaxDataSensitivity,
            sensitivityRegistry);

        EnsureLevelNotBroader(
            child.Id,
            parent.Id,
            "MaxDataSensitivity",
            parent.MaxDataSensitivity,
            child.MaxDataSensitivity,
            sensitivityRegistry);

        if (child.AllowedDataSensitivityLevels is { Count: > 0 })
        {
            var parentAllowed = parent.AllowedDataSensitivityLevels is { Count: > 0 }
                ? new HashSet<string>(parent.AllowedDataSensitivityLevels, StringComparer.OrdinalIgnoreCase)
                : null;

            foreach (var levelName in child.AllowedDataSensitivityLevels)
            {
                if (parentAllowed is not null && !parentAllowed.Contains(levelName))
                {
                    throw new InvalidOperationException(
                        $"Refusing machine-origin agent '{child.Id}': AllowedDataSensitivityLevels contains '{levelName}' outside parent '{parent.Id}' allow-list.");
                }

                EnsureLevelNotBroader(
                    child.Id,
                    parent.Id,
                    "AllowedDataSensitivityLevels",
                    parentPolicy.MaxAllowedLevel ?? parent.MaxDataSensitivity,
                    levelName,
                    sensitivityRegistry);
            }
        }
    }

    private static void EnsureLevelNotBroader(
        string childId,
        string parentId,
        string field,
        string parentLevelName,
        string childLevelName,
        IDataSensitivityRegistry sensitivityRegistry)
    {
        var parentLevel = SensitivityLimits.Resolve(sensitivityRegistry, parentLevelName);
        var childLevel = SensitivityLimits.Resolve(sensitivityRegistry, childLevelName);
        if (parentLevel is null || childLevel is null)
        {
            throw new InvalidOperationException(
                $"Refusing machine-origin agent '{childId}': unable to resolve {field} sensitivity levels for parent/child comparison.");
        }

        // No custom numeric rank is equivalent to the canonical SystemHigh limit.
        if (parentLevelName != "SystemHigh"
            && (childLevelName == "SystemHigh" || childLevel.SensitivityValue > parentLevel.SensitivityValue))
        {
            throw new InvalidOperationException(
                $"Refusing machine-origin agent '{childId}': {field} '{childLevelName}' exceeds parent '{parentId}' envelope '{parentLevelName}'.");
        }
    }
}
