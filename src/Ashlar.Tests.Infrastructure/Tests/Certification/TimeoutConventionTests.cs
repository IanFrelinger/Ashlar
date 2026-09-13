using System.Reflection;
using System.Text.RegularExpressions;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure.Validation.Adapters;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Sanity test: Integration and E2E tests must have explicit [Fact(Timeout = N)] to prevent blame-hang.
/// </summary>
public sealed class TimeoutConventionTests
{
    private static bool HasTrait(Type type, string name, string value)
    {
        foreach (var attr in type.GetCustomAttributesData())
        {
            if (attr.AttributeType.Name != "TraitAttribute") continue;
            if (attr.ConstructorArguments.Count >= 2 &&
                attr.ConstructorArguments[0].Value?.ToString() == name &&
                attr.ConstructorArguments[1].Value?.ToString() == value)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Categories whose tests touch real hosts, sockets, processes or containers, and
    /// can therefore block indefinitely rather than fail.
    /// </summary>
    /// <remarks>
    /// ProdStyle was added after a test in that category wedged the entire suite. It
    /// stood up a full API host through WebApplicationFactory, whose Services property
    /// blocks on host.StartAsync(); a hosted service never finished starting, so the
    /// test never finished either. With no timeout there was nothing to fail — the run
    /// simply stopped making progress, and two CI runs burned 30 and 60 minutes before
    /// being cancelled without ever naming a culprit.
    ///
    /// The guard existed at the time and did not catch it, purely because the class was
    /// traited ProdStyle rather than E2E. Both categories carry the same risk, so both
    /// are gated now.
    /// </remarks>
    private static readonly string[] TimeoutRequiredCategories = ["E2E", "ProdStyle"];

    /// <summary>
    /// xunit enforces Timeout by racing the returned Task against a delay, so it can only do it for a
    /// Task-returning test; a void one is rejected at run time with "Tests marked with Timeout are only
    /// supported for async tests". The two halves of the convention would otherwise contradict each other:
    /// the timeout is required, and adding it to a void test turns a passing test into a failing one.
    /// </summary>
    private static bool ReturnsTask(MethodInfo method)
    {
        var returnType = method.ReturnType;
        if (returnType == typeof(Task) || returnType == typeof(ValueTask)) return true;

        return returnType.IsGenericType &&
               (returnType.GetGenericTypeDefinition() == typeof(Task<>) ||
                returnType.GetGenericTypeDefinition() == typeof(ValueTask<>));
    }

    [Fact]
    public void HostTouchingTests_MustHaveExplicitTimeout()
    {
        var assembly = typeof(TimeoutConventionTests).Assembly;
        var violations = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract) continue;

            var category = TimeoutRequiredCategories.FirstOrDefault(c => HasTrait(type, "Category", c));
            if (category is null) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                // TheoryAttribute derives from FactAttribute, so one lookup covers both; the name is
                // only used to quote the right attribute back at whoever has to fix it.
                var fact = method.GetCustomAttribute<FactAttribute>();
                if (fact is null) continue;

                var attribute = fact is TheoryAttribute ? "Theory" : "Fact";

                if (fact.Timeout == 0)
                {
                    violations.Add($"{type.Name}.{method.Name}: {category} test lacks [{attribute}(Timeout = N)]");
                }
                else if (!ReturnsTask(method))
                {
                    violations.Add($"{type.Name}.{method.Name}: {category} test has [{attribute}(Timeout = N)] but returns {method.ReturnType.Name}; make it async Task (xunit only honours Timeout on Task-returning tests and fails the test at run time otherwise)");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Host-touching tests must have explicit Timeout:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// A per-test timeout only ever fires if it is INSIDE the harness window above it. Two lanes
    /// sweep Ashlar.Tests.Infrastructure broadly and both ran a 120s blame window over suites
    /// whose widest per-test net is <c>TestTimeouts.HostTouching</c> at 480s — four times larger.
    /// Those tests therefore could not fail as timeouts there at all: a stall past two minutes
    /// killed the test host, discarded the ~1900 results already recorded, and named the
    /// in-flight test only as one that "may, or may not be the source of the crash". Twice on the
    /// macOS lane, on <c>FileSystemEventSourceTests.SubscribeAsync_FileCreated_EmitsEvent</c>.
    ///
    /// <para>The invariant is what makes a hang diagnosable, and it breaks from either side — by
    /// widening a TestTimeouts constant or by narrowing a lane's window — so it is frozen here
    /// rather than left to whoever edits one of them next.</para>
    /// </summary>
    [Fact]
    public void Every_per_test_timeout_fits_inside_the_broad_sweep_blame_window()
    {
        var windowMs = ValidationServiceAdapter.ValidateBlameHangTimeoutSeconds * 1000;

        var constants = typeof(TestTimeouts)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .Select(f => new { f.Name, Value = (int)f.GetRawConstantValue()! })
            .ToArray();

        // Positive control: reflection that found nothing would satisfy the assertion below.
        constants.Should().HaveCountGreaterThan(
            5,
            "TestTimeouts is the inventory this compares against; an empty one is a fault in the "
            + "check, not a clean result");

        var offenders = constants.Where(c => c.Value >= windowMs).ToArray();

        offenders.Should().BeEmpty(
            $"a per-test timeout at or above the {windowMs} ms blame window can never fire — the "
            + "host is killed first, and a killed host reports no failing test. Either lower the "
            + "constant or widen ValidationServiceAdapter.ValidateBlameHangTimeoutSeconds (and "
            + "the matching window in CiCommand). Offending: "
            + string.Join(", ", offenders.Select(c => $"{c.Name}={c.Value}ms")));
    }

    /// <summary>
    /// The other half: the constant above is the invariant only if it is what the lanes actually
    /// pass to <c>dotnet test</c>. A hard-coded window alongside it would leave the test above
    /// green while a lane ran on a number nobody checked.
    ///
    /// <para>Validate's window is read from its actual argv builder. The remaining command-string
    /// inventory has both facts, per <c>docs/HowGatesGoQuiet.md</c> section 7: a
    /// NEW window in the CLI file fails until it is accounted for,
    /// and a deleted one fails too, so the list cannot rot into a description of what used to be
    /// true. The one narrow window admitted here is the 30s smoke step, which selects only
    /// BaseFrameworkSmokeTests — no HostTouching net is inside it.</para>
    /// </summary>
    [Fact]
    public void The_broad_sweep_lanes_pass_the_checked_window_to_dotnet_test()
    {
        var window = $"{ValidationServiceAdapter.ValidateBlameHangTimeoutSeconds}s";

        var validate = ValidationServiceAdapter.CreateDotnetTestStartInfo("Tests.csproj", null, null, false);
        var indexes = validate.ArgumentList.Select((value, index) => (value, index))
            .Where(item => item.value == "--blame-hang-timeout").Select(item => item.index).ToArray();
        indexes.Should().ContainSingle("validate must pass the checked window exactly once");
        validate.ArgumentList[indexes.Single() + 1].Should().Be(window);

        var expected = new (string RelativePath, string[] Windows)[]
        {
            ("application/src/Ashlar.CLI/Commands/CiCommand.cs", [window, "30s"]),
        };

        foreach (var (relativePath, windows) in expected)
        {
            var path = Path.Combine(RepoPathResolver.FindRepoRoot(), relativePath);
            File.Exists(path).Should().BeTrue(
                $"{relativePath} is an input to this convention; a missing input is a hard "
                + "failure of the check, never a clean result");

            var found = Regex
                .Matches(File.ReadAllText(path), "--blame-hang-timeout ([^ \"]+)")
                .Select(m => m.Groups[1].Value)
                .ToArray();

            found.Should().Equal(
                windows,
                $"{relativePath} must spell every broad-sweep window as the constant this "
                + "convention checks");
        }
    }
}
