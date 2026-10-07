namespace Ashlar.BackgroundAgents.DataSensitivity;

/// <summary>
/// What a missing or unrecognised sensitivity name means, stated once so every consumer fails
/// closed the same way.
/// </summary>
/// <remarks>
/// <para><b>A clearance and a label fail closed in OPPOSITE directions.</b> A name the registry
/// cannot resolve tells us nothing, and nothing must never widen access:</para>
/// <list type="bullet">
/// <item>a missing or unknown CLEARANCE (what a caller or agent may read) resolves to the
/// <see cref="Floor"/> -- the lowest level the registry knows, which is Public unless a custom
/// level was registered below it;</item>
/// <item>a missing or unknown LABEL (what a record or datum is) resolves to
/// <see cref="MostRestrictive"/> -- the highest level the registry knows, never lower than
/// TopSecret.</item>
/// </list>
/// <para>Before this existed each consumer chose for itself, and the choices were open: the
/// legacy vector stores applied no filter at all to a null or unknown clearance and always
/// returned unmarked documents, and <see cref="DataSensitivityMarker"/> reported unmarked data as
/// Public.</para>
/// </remarks>
public static class DataSensitivityFallbacks
{
    /// <summary>The lowest level <paramref name="registry"/> knows (Public for the primitives).</summary>
    public static IDataSensitivityLevel Floor(this IDataSensitivityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var lowest = DataSensitivityLevels.Public;
        foreach (var level in registry.GetAll() ?? Array.Empty<IDataSensitivityLevel>())
        {
            if (level is not null && level.SensitivityValue < lowest.SensitivityValue)
                lowest = level;
        }

        return lowest;
    }

    /// <summary>The highest level <paramref name="registry"/> knows, and never below TopSecret.</summary>
    public static IDataSensitivityLevel MostRestrictive(this IDataSensitivityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var highest = DataSensitivityLevels.TopSecret;
        foreach (var level in registry.GetAll() ?? Array.Empty<IDataSensitivityLevel>())
        {
            if (level is not null && level.SensitivityValue > highest.SensitivityValue)
                highest = level;
        }

        return highest;
    }

    /// <summary>A CLEARANCE by name: missing or unknown resolves to <see cref="Floor"/>.</summary>
    public static IDataSensitivityLevel ResolveClearance(this IDataSensitivityRegistry registry, string? levelName)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.GetByName(levelName) ?? registry.Floor();
    }

    /// <summary>A LABEL by name: missing or unknown resolves to <see cref="MostRestrictive"/>.</summary>
    public static IDataSensitivityLevel ResolveLabel(this IDataSensitivityRegistry registry, string? levelName)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.GetByName(levelName) ?? registry.MostRestrictive();
    }
}
