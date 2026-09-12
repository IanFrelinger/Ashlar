using System.Reflection;

namespace Ashlar.Samples.CertificationPresetProbe;

/// <summary>
/// Reads the verification presets out of whatever version of
/// <c>Ashlar.Certification.Contracts</c> was restored, entirely by reflection, and prints them.
///
/// <para>Reflection rather than the typed API on purpose. The typed consumer next door
/// (<c>CertificationTrustConsumer</c>) is the gate, and it is built from the same commit as the
/// package it tests, so its compile-time surface always matches. This probe has the opposite job:
/// point it at an OLD published version and it still runs, because a member that no longer exists —
/// or did not exist yet — is a line of output rather than a compile error.</para>
///
/// <para>That is what makes it usable for the question "which published versions were fail-open?",
/// which is not a question a build against the source tree can answer.</para>
/// </summary>
internal static class Program
{
    private static readonly string[] Presets = ["Default", "Strict", "Legacy"];

    private static readonly string[] Properties =
    [
        "RequireEd25519Signature",
        "MinimumSchemaVersion",
        "RequireGateEmittedArtifact",
        "RequireCertifierIdentity",
        "IsStrict",
        "PinningEnabled",
    ];

    private static int Main()
    {
        var assembly = LoadContracts();
        if (assembly is null)
        {
            Console.WriteLine("PROBE_RESULT=ASSEMBLY_NOT_FOUND");
            return 2;
        }

        Console.WriteLine($"PROBE_ASSEMBLY={assembly.GetName().Name}");
        Console.WriteLine($"PROBE_ASSEMBLY_VERSION={assembly.GetName().Version}");
        Console.WriteLine($"PROBE_INFORMATIONAL_VERSION={assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "(none)"}");

        var optionsType = assembly.GetType("Ashlar.Certification.Contracts.CertificationVerifyOptions");
        if (optionsType is null)
        {
            Console.WriteLine("PROBE_RESULT=OPTIONS_TYPE_NOT_FOUND");
            return 2;
        }

        // The committed HMAC fallback, read the same way: a version that did not have it, or that
        // renamed it, says so rather than failing to build.
        var signingType = assembly.GetType("Ashlar.Certification.Contracts.CertificationRecordSigning");
        var devKey = signingType?.GetField("DefaultDevKey", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue();
        Console.WriteLine($"PROBE_DEFAULT_DEV_KEY={(devKey is null ? "(absent)" : "\"" + devKey + "\"")}");
        Console.WriteLine();

        foreach (var presetName in Presets)
        {
            var presetProperty = optionsType.GetProperty(presetName, BindingFlags.Public | BindingFlags.Static);
            if (presetProperty is null)
            {
                Console.WriteLine($"PRESET {presetName}=ABSENT");
                continue;
            }

            var preset = presetProperty.GetValue(null);
            if (preset is null)
            {
                Console.WriteLine($"PRESET {presetName}=NULL");
                continue;
            }

            foreach (var propertyName in Properties)
            {
                var property = optionsType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property is null)
                {
                    Console.WriteLine($"PRESET {presetName}.{propertyName}=ABSENT");
                    continue;
                }

                Console.WriteLine($"PRESET {presetName}.{propertyName}={Render(property.GetValue(preset))}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("PROBE_RESULT=OK");
        Console.WriteLine();
        Console.WriteLine("This probe reports; it does not judge. A version whose Default or Strict reports");
        Console.WriteLine("RequireEd25519Signature=False falls back to HMAC, and the HMAC key falls back to the");
        Console.WriteLine("committed constant above when ASHLAR_CERT_DEV_HMAC_KEY is unset.");
        return 0;
    }

    private static string Render(object? value) => value switch
    {
        null => "(null)",
        bool b => b ? "True" : "False",
        System.Collections.ICollection c => $"[{c.Count} entries]",
        _ => value.ToString() ?? "(null)",
    };

    /// <summary>
    /// The package assembly is not referenced by any typed code here, so nothing has triggered a
    /// load by the time Main runs. Load it by name from the app's own folder.
    /// </summary>
    private static Assembly? LoadContracts()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Ashlar.Certification.Contracts.dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
}
