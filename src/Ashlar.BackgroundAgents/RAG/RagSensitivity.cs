using Ashlar.BackgroundAgents.DataSensitivity;

namespace Ashlar.BackgroundAgents.RAG;

/// <summary>
/// The two sensitivity decisions both legacy vector stores make, in one place so they cannot
/// drift apart: may this clearance read this label, and may this label replace that one.
/// </summary>
internal static class RagSensitivity
{
    /// <summary>
    /// Whether a caller at <paramref name="clearance"/> may read a document labelled
    /// <paramref name="documentLabel"/>. An unmarked or unknown label is the most restrictive
    /// level the registry knows (<see cref="DataSensitivityFallbacks.ResolveLabel"/>).
    /// </summary>
    public static bool CanRead(IDataSensitivityRegistry registry, IDataSensitivityLevel clearance, string? documentLabel) =>
        registry.CanAccess(clearance, registry.ResolveLabel(documentLabel));

    /// <summary>
    /// Refuses to replace a stored document with one labelled LOWER. Both labels resolve through
    /// <see cref="DataSensitivityFallbacks.ResolveLabel"/>, so an unmarked stored document counts as
    /// the most restrictive and cannot be relabelled downward by re-indexing either.
    /// </summary>
    /// <exception cref="InvalidOperationException">The incoming label is lower than the stored one.</exception>
    public static void ThrowIfDowngrade(IDataSensitivityRegistry registry, string id, string? storedLabel, string? incomingLabel)
    {
        var stored = registry.ResolveLabel(storedLabel);
        var incoming = registry.ResolveLabel(incomingLabel);
        if (incoming.SensitivityValue < stored.SensitivityValue)
        {
            throw new InvalidOperationException(
                $"Refusing to re-index '{id}' at '{Describe(incomingLabel)}': it is already stored at "
                + $"'{Describe(storedLabel)}' (treated as {stored.Value}), and re-indexing must never lower a "
                + "document's sensitivity. If the lower label is correct, remove the document first and index "
                + "it again -- lowering a label has to be a separate, deliberate act.");
        }
    }

    private static string Describe(string? label) => string.IsNullOrWhiteSpace(label) ? "(unmarked)" : label;
}
