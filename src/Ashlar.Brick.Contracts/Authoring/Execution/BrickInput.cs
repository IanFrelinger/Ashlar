namespace Ashlar.Core.Domain.Execution;

/// <summary>
/// Input data for a brick execution.
/// </summary>
public class BrickInput
{
    private readonly Dictionary<string, object> _data = new();

    /// <summary>Creates an empty input bag.</summary>
    public BrickInput()
    {
    }

    /// <summary>Creates an input bag seeded from an existing dictionary.</summary>
    /// <param name="data">Initial key-value pairs.</param>
    public BrickInput(IReadOnlyDictionary<string, object> data)
    {
        foreach (var kvp in data)
        {
            _data[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>Sets or replaces a named input value.</summary>
    /// <param name="key">Input parameter name.</param>
    /// <param name="value">Value to store.</param>
    public void Set(string key, object value)
    {
        _data[key] = value;
    }

    /// <summary>Gets a required input value cast to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">Input parameter name.</param>
    /// <exception cref="KeyNotFoundException">When the key is absent.</exception>
    /// <exception cref="InvalidCastException">When the stored value is not assignable to <typeparamref name="T"/>.</exception>
    public T Get<T>(string key)
    {
        if (!_data.TryGetValue(key, out var value))
            throw new KeyNotFoundException($"Input key '{key}' not found");

        if (value is T typedValue)
            return typedValue;

        throw new InvalidCastException($"Input key '{key}' is not of type {typeof(T).Name}");
    }

    /// <summary>Gets an input value cast to <typeparamref name="T"/>, or a default when missing or incompatible.</summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">Input parameter name.</param>
    /// <param name="defaultValue">Value returned when the key is missing or not assignable.</param>
    public T? Get<T>(string key, T? defaultValue)
    {
        if (!_data.TryGetValue(key, out var value))
            return defaultValue;

        if (value is T typedValue)
            return typedValue;

        return defaultValue;
    }

    /// <summary>Tries to get an input value cast to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">Input parameter name.</param>
    /// <param name="value">The output parameter to receive the value if found and compatible.</param>
    /// <returns>True if the key exists and the value is assignable to <typeparamref name="T"/>; otherwise false.</returns>
    public bool TryGet<T>(string key, out T? value)
    {
        if (_data.TryGetValue(key, out var obj) && obj is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Returns a read-only view of all input key-value pairs.</summary>
    public IReadOnlyDictionary<string, object> ToDictionary() => _data;
}
