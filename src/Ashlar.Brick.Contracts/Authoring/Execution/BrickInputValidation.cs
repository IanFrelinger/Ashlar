using Ashlar.Core.Domain.Bricks;

namespace Ashlar.Core.Domain.Execution;

/// <summary>
/// Validation helpers for brick input parameters.
/// </summary>
public static class BrickInputValidation
{
    /// <summary>
    /// Validates that all required inputs defined in the brick interface are present.
    /// </summary>
    /// <param name="input">The input bag to validate.</param>
    /// <param name="brickInterface">The brick interface definition with input requirements.</param>
    /// <exception cref="ArgumentNullException">When input or brickInterface is null.</exception>
    /// <exception cref="BrickInputValidationException">When one or more required inputs are missing.</exception>
    public static void ValidateRequiredInputs(this BrickInput input, BrickInterface brickInterface)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(brickInterface);

        var missingInputs = new List<string>();
        var inputDict = input.ToDictionary();

        foreach (var inputDef in brickInterface.Inputs)
        {
            if (inputDef.Required && !inputDict.ContainsKey(inputDef.Name))
            {
                missingInputs.Add(inputDef.Name);
            }
        }

        if (missingInputs.Count > 0)
        {
            throw new BrickInputValidationException(
                $"Missing required input(s): {string.Join(", ", missingInputs)}",
                missingInputs);
        }
    }

    /// <summary>
    /// Validates that all required inputs are present and returns whether validation passed.
    /// </summary>
    /// <param name="input">The input bag to validate.</param>
    /// <param name="brickInterface">The brick interface definition with input requirements.</param>
    /// <param name="missingInputs">Outputs the list of missing required input names.</param>
    /// <returns>True if all required inputs are present; otherwise false.</returns>
    public static bool TryValidateRequiredInputs(
        this BrickInput input,
        BrickInterface brickInterface,
        out IReadOnlyList<string> missingInputs)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(brickInterface);

        var missing = new List<string>();
        var inputDict = input.ToDictionary();

        foreach (var inputDef in brickInterface.Inputs)
        {
            if (inputDef.Required && !inputDict.ContainsKey(inputDef.Name))
            {
                missing.Add(inputDef.Name);
            }
        }

        missingInputs = missing;
        return missing.Count == 0;
    }

    /// <summary>
    /// Applies default values for any optional inputs that are missing.
    /// </summary>
    /// <param name="input">The input bag to populate with defaults.</param>
    /// <param name="brickInterface">The brick interface definition with input defaults.</param>
    public static void ApplyDefaults(this BrickInput input, BrickInterface brickInterface)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(brickInterface);

        var inputDict = input.ToDictionary();

        foreach (var inputDef in brickInterface.Inputs)
        {
            if (!inputDef.Required && !inputDict.ContainsKey(inputDef.Name) && inputDef.Default != null)
            {
                input.Set(inputDef.Name, inputDef.Default);
            }
        }
    }
}

/// <summary>
/// Exception thrown when brick input validation fails.
/// </summary>
public class BrickInputValidationException : Exception
{
    /// <summary>Names of the missing required inputs.</summary>
    public IReadOnlyList<string> MissingInputs { get; }

    /// <summary>Initializes a new validation exception.</summary>
    /// <param name="message">Error message.</param>
    /// <param name="missingInputs">Names of missing required inputs.</param>
    public BrickInputValidationException(string message, IReadOnlyList<string> missingInputs)
        : base(message)
    {
        MissingInputs = missingInputs;
    }
}
