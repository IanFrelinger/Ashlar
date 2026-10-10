namespace Ashlar.BackgroundAgents.DataSensitivity;

// Configuration is a requested ceiling, never a read grant. Keep SystemHigh out of the legacy
// registry so a custom numeric level cannot acquire its canonical authority semantics.
internal static class SensitivityLimits
{
    private static readonly IDataSensitivityLevel SystemHigh = new PrimitiveSensitivityLevel(
        "SystemHigh", "System High", int.MaxValue,
        AllowsExternalLLM: false, AllowsWebSearch: false, RequiresLocalOnly: true,
        AllowsNetworkExports: false, "Explicit ceiling for trusted SystemHigh subjects");

    internal static IDataSensitivityLevel? Resolve(IDataSensitivityRegistry registry, string? name) =>
        name == "SystemHigh" ? SystemHigh : DataSensitivityLevels.FromName(name) ?? registry.GetByName(name);
}
