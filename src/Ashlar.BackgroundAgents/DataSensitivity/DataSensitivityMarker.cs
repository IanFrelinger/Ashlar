using System.Collections.Concurrent;

namespace Ashlar.BackgroundAgents.DataSensitivity;

/// <summary>
/// Implementation of IDataSensitivityMarker.
/// </summary>
public sealed class DataSensitivityMarker : IDataSensitivityMarker
{
    private readonly IDataSensitivityRegistry _registry;
    private readonly ConcurrentDictionary<object, IDataSensitivityLevel> _markings = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DataSensitivityMarker"/> class.
    /// </summary>
    /// <param name="registry">The sensitivity level registry.</param>
    public DataSensitivityMarker(IDataSensitivityRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Get the sensitivity level for a data object. Unmarked (or null) data is the MOST
    /// RESTRICTIVE level the registry knows, never Public: nobody classified it, so nothing about
    /// it may be assumed safe. It used to report Public, which made every unmarked object readable
    /// by every agent through <see cref="CanAccess"/>.
    /// </summary>
    public IDataSensitivityLevel GetSensitivityLevel(object data)
    {
        if (data == null)
            return _registry.MostRestrictive();

        return _markings.TryGetValue(data, out var level)
            ? level
            : _registry.MostRestrictive();
    }

    /// <summary>
    /// Mark a data object with a sensitivity level.
    /// </summary>
    public void MarkSensitivity(object data, IDataSensitivityLevel level)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (level == null)
            throw new ArgumentNullException(nameof(level));

        _markings[data] = level;
    }

    /// <summary>
    /// Mark a data object with a sensitivity level by name.
    /// </summary>
    public void MarkSensitivity(object data, string levelName)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (string.IsNullOrWhiteSpace(levelName))
            throw new ArgumentException("Level name cannot be null or empty", nameof(levelName));

        var level = _registry.GetByName(levelName)
            ?? throw new ArgumentException($"Unknown sensitivity level: {levelName}", nameof(levelName));

        MarkSensitivity(data, level);
    }

    /// <summary>
    /// Check if an agent with the given sensitivity level can access the data.
    /// </summary>
    public bool CanAccess(IDataSensitivityLevel agentLevel, object data)
    {
        if (agentLevel == null)
            throw new ArgumentNullException(nameof(agentLevel));

        var dataLevel = GetSensitivityLevel(data);
        return _registry.CanAccess(agentLevel, dataLevel);
    }
}
