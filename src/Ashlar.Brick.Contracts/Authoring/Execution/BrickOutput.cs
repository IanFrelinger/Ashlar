namespace Ashlar.Core.Domain.Execution;

/// <summary>
/// Output data from a brick execution.
/// </summary>
public class BrickOutput
{
    private readonly Dictionary<string, object> _data = new();

    /// <summary>Human-readable summary of the execution outcome.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets an output field by name.</summary>
    /// <param name="key">Output field name.</param>
    public object this[string key]
    {
        get => _data[key];
        set => _data[key] = value;
    }

    /// <summary>Sets or replaces a named output value.</summary>
    /// <param name="key">Output field name.</param>
    /// <param name="value">Value to store.</param>
    public void Set(string key, object value)
    {
        _data[key] = value;
    }

    /// <summary>Gets a required output value cast to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">Output field name.</param>
    /// <exception cref="KeyNotFoundException">When the key is absent.</exception>
    /// <exception cref="InvalidCastException">When the stored value is not assignable to <typeparamref name="T"/>.</exception>
    public T Get<T>(string key)
    {
        if (!_data.TryGetValue(key, out var value))
            throw new KeyNotFoundException($"Output key '{key}' not found");

        if (value is T typedValue)
            return typedValue;

        throw new InvalidCastException($"Output key '{key}' is not of type {typeof(T).Name}");
    }

    /// <summary>Gets an output value cast to <typeparamref name="T"/>, or a default when missing or incompatible.</summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">Output field name.</param>
    /// <param name="defaultValue">Value returned when the key is missing or not assignable.</param>
    public T? Get<T>(string key, T? defaultValue)
    {
        if (!_data.TryGetValue(key, out var value))
            return defaultValue;

        if (value is T typedValue)
            return typedValue;

        return defaultValue;
    }

    /// <summary>Tries to get an output value cast to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">Output field name.</param>
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

    /// <summary>Returns a read-only view of all output key-value pairs.</summary>
    public IReadOnlyDictionary<string, object> ToDictionary() => _data;
}
