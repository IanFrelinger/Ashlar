using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// B5: hermetic Mono.Cecil scan of the certifier. <c>Assembly.Load</c> /
/// <c>LoadFrom</c> / <c>LoadFromAssemblyPath</c> / <c>Activator.CreateInstance</c> may appear only on
/// the frozen inventory. A new call site is a regression, not a silent exception.
/// </summary>
[Trait("Category", "Certification")]
public sealed class CertifierBoundaryScanTests
{
    private static readonly string InventoryPath = Path.Combine(
        FindRepoRoot(),
        "ci",
        "certifier-boundary-inventory.tsv");

    private static readonly HashSet<string> WatchedApis = new(StringComparer.Ordinal)
    {
        "System.Reflection.Assembly::LoadFrom",
        "System.Reflection.Assembly::LoadFile",
        "System.Reflection.Assembly::Load",
        "System.Runtime.Loader.AssemblyLoadContext::LoadFromAssemblyPath",
        "System.Activator::CreateInstance"
    };

    [Fact]
    public void CertifierAssemblies_LoadAndCreateInstance_StayInsideFrozenInventory()
    {
        File.Exists(InventoryPath).Should().BeTrue("ci/certifier-boundary-inventory.tsv is the B5 freeze");
        var allowed = File.ReadAllLines(InventoryPath)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .Select(ParseInventoryRow)
            .ToHashSet(StringComparer.Ordinal);

        var hits = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in CertifierAssemblies())
        {
            using var module = ModuleDefinition.ReadModule(path);
            foreach (var type in module.Types.SelectMany(Flatten))
            {
                if (!type.FullName.StartsWith("Ashlar.Infrastructure.Certification", StringComparison.Ordinal)
                    && !type.FullName.StartsWith("Program", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is not MethodReference callee)
                            continue;
                        var api = callee.DeclaringType.FullName + "::" + callee.Name;
                        if (!WatchedApis.Contains(api))
                            continue;
                        hits.Add($"{NormalizeSite(type.FullName, method.Name)}\t{api}");
                    }
                }
            }
        }

        var unexpected = hits.Where(h => !allowed.Contains(h)).ToArray();
        var stale = allowed.Where(a => !hits.Contains(a)).ToArray();
        unexpected.Should().BeEmpty(
            "new certifier LoadFrom/CreateInstance sites must be added to ci/certifier-boundary-inventory.tsv with a reason. Unexpected:\n"
            + string.Join("\n", unexpected));
        stale.Should().BeEmpty(
            "inventory lists sites that no longer exist; shrink the list, do not keep ghosts:\n"
            + string.Join("\n", stale));
    }

    /// <summary>
    /// Maps a compiler-generated method back to the source method that wrote it, so inventory rows
    /// name something a human can find and edit. Three shapes, all of which occur in this assembly:
    /// async/iterator state machines (<c>Type/&lt;Method&gt;d__N::MoveNext</c>), lambdas
    /// (<c>Type/&lt;&gt;c::&lt;Method&gt;b__N_M</c>, or a display class when the lambda captures),
    /// and local functions (<c>Type::&lt;Method&gt;g__Name|N_M</c>).
    ///
    /// <para>The last two were added when a scan over this same assembly reported
    /// <c>RoslynCodeAnalysisService/&lt;&gt;c::&lt;BuildReferenceSet&gt;b__5_0</c> — a real site,
    /// under a name no inventory would ever have been written with. A freeze whose rows cannot be
    /// spelled by hand is a freeze nobody maintains.</para>
    /// </summary>
    internal static string NormalizeSite(string typeFullName, string methodName)
    {
        // A lambda or local function carries its source method inside its own name.
        if (methodName.Length > 2 && methodName[0] == '<')
        {
            var close = methodName.IndexOf('>', 1);
            if (close > 1)
                return StripClosureNesting(typeFullName) + "::" + methodName[1..close];
        }

        var generated = typeFullName.IndexOf("/<", StringComparison.Ordinal);
        if (generated >= 0 && methodName == "MoveNext")
        {
            var start = generated + 2;
            var end = typeFullName.IndexOf('>', start);
            if (end > start)
                return typeFullName[..generated] + "::" + typeFullName[start..end];
        }

        return typeFullName + "::" + methodName;
    }

    /// <summary>Drops the <c>/&lt;&gt;c</c> or <c>/&lt;&gt;c__DisplayClassN_M</c> the compiler nests a lambda in.</summary>
    private static string StripClosureNesting(string typeFullName)
    {
        var nested = typeFullName.IndexOf("/<>", StringComparison.Ordinal);
        return nested >= 0 ? typeFullName[..nested] : typeFullName;
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) =>
        type.NestedTypes.SelectMany(Flatten).Prepend(type);

    private static string ParseInventoryRow(string line)
    {
        var parts = line.Split('\t');
        parts.Length.Should().BeGreaterThanOrEqualTo(2, $"inventory row must be site<TAB>api<TAB>reason: {line}");
        return parts[0] + "\t" + parts[1];
    }

    private static IEnumerable<string> CertifierAssemblies()
    {
        var dir = AppContext.BaseDirectory;
        foreach (var name in new[] { "Ashlar.Infrastructure.dll" })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
                yield return path;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ashlar.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
