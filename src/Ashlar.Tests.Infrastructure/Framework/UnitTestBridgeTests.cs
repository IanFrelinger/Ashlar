using System.Reflection;
using Ashlar.Infrastructure.Testing;
using Xunit;
using Ashlar.Tests.Infrastructure.Helpers;

namespace Ashlar.Tests.Infrastructure.Framework;

/// <summary>
/// Bridges <c>UnitTestBase</c> suites in this assembly to xUnit / VSTest via <see cref="UnitTestFrameworkBridge"/>.
/// Excludes <c>SimpleTestForRunner</c> (helper exercised only from <c>TestRunnerAdapterTests</c>).
///
/// <para>In <c>ProcessCwd</c> because two of the suites it executes in-process flip the working
/// directory: <c>AgentExecutorAdapterTests</c> and <c>ValidationServiceAdapterTests</c>. The
/// attribute has to live here — those suites are not xUnit classes, so a <c>[Collection]</c> on
/// them would be ignored; this theory is what actually runs them. See
/// <see cref="Helpers.ProcessCwdCollection"/>.</para>
/// </summary>
[Trait("Category", "ProdStyle")]
[Collection("ProcessCwd")]
public sealed class UnitTestBridgeTests
{
    public static TheoryData<Type> UnitTestTypes { get; } = BuildTheoryData();

    private static TheoryData<Type> BuildTheoryData()
    {
        var data = new TheoryData<Type>();
        foreach (var t in UnitTestFrameworkBridge.DiscoverUnitTestTypesFromAssembly(
                     Assembly.GetExecutingAssembly()))
        {
            data.Add(t);
        }

        return data;
    }

    [Theory(Timeout = TestTimeouts.HostTouching)]
    [MemberData(nameof(UnitTestTypes))]
    public async Task Framework_unit_test_passes(Type testType)
    {
        await UnitTestFrameworkBridge.ExecuteUnitTestAsync(testType);
    }
}
