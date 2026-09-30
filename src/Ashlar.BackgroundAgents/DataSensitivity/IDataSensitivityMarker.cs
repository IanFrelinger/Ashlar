using System.Collections.Concurrent;

namespace Ashlar.BackgroundAgents.DataSensitivity;

/// <summary>
/// Marks and tracks data sensitivity levels.
/// 
/// Provides:
/// - Marking data objects with sensitivity levels
/// - Retrieving sensitivity levels for data
/// - Checking if an agent can access specific data
/// 
/// Thread-safe implementation using concurrent collections.
/// </summary>
public interface IDataSensitivityMarker
{
    /// <summary>
    /// Get the sensitivity level for a data object.
    /// </summary>
    /// <param name="data">The data object.</param>
    /// <returns>The sensitivity level, or the most restrictive level the registry knows if not marked
    /// (see <see cref="DataSensitivityFallbacks.MostRestrictive"/>).</returns>
    IDataSensitivityLevel GetSensitivityLevel(object data);

    /// <summary>
    /// Mark a data object with a sensitivity level.
    /// </summary>
    /// <param name="data">The data object to mark.</param>
    /// <param name="level">The sensitivity level.</param>
    void MarkSensitivity(object data, IDataSensitivityLevel level);

    /// <summary>
    /// Mark a data object with a sensitivity level by name.
    /// </summary>
    /// <param name="data">The data object to mark.</param>
    /// <param name="levelName">The sensitivity level name.</param>
    /// <exception cref="ArgumentException">Thrown if level name is not found.</exception>
    void MarkSensitivity(object data, string levelName);

    /// <summary>
    /// Check if an agent with the given sensitivity level can access the data.
    /// </summary>
    /// <param name="agentLevel">The agent's maximum sensitivity level.</param>
    /// <param name="data">The data object to check.</param>
    /// <returns>True if agent can access data, false otherwise.</returns>
    bool CanAccess(IDataSensitivityLevel agentLevel, object data);
}
