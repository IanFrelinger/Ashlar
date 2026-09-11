using FluentAssertions;
using Ashlar.Core.Application.Persistence;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Persistence;

/// <summary>
/// The one place a LiteDB connection mode is chosen.
/// </summary>
/// <remarks>
/// Every store delegates here, so the cases below are the whole contract: a bare path becomes a
/// Shared connection string, an already-formed one is completed rather than rebuilt, and a mode the
/// caller stated is never overruled. That last one is the deliberate hole — a deployment binding
/// <c>;Connection=Direct</c> from configuration gets Direct — and it is pinned here so the exemption
/// stays a decision rather than drifting into an accident.
/// </remarks>
public sealed class LiteDbConnectionStringTests
{
    [Fact]
    public void Bare_path_becomes_a_shared_connection_string()
    {
        LiteDbConnectionString.ForSharedAccess("/var/state/patterns.db")
            .Should().Be("Filename=/var/state/patterns.db;Connection=Shared");
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        LiteDbConnectionString.ForSharedAccess("  patterns.db  ")
            .Should().Be("Filename=patterns.db;Connection=Shared");
    }

    [Fact]
    public void Existing_filename_prefix_is_kept_and_completed()
    {
        LiteDbConnectionString.ForSharedAccess("Filename=/tmp/custom.db")
            .Should().Be("Filename=/tmp/custom.db;Connection=Shared");
    }

    [Theory]
    [InlineData("Filename=/tmp/custom.db;Connection=Direct")]
    [InlineData("Filename=/tmp/custom.db;connection=direct")]
    public void An_explicit_mode_supplied_by_the_caller_is_left_alone(string connectionString)
    {
        LiteDbConnectionString.ForSharedAccess(connectionString).Should().Be(connectionString);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_input_is_rejected(string value)
    {
        var act = () => LiteDbConnectionString.ForSharedAccess(value);

        act.Should().Throw<ArgumentException>().WithParameterName("pathOrConnectionString");
    }

    /// <summary>
    /// A ';' in a bare path would be read as an option separator and silently truncate the filename,
    /// which is a store quietly writing somewhere else. Loud beats silent.
    /// </summary>
    [Fact]
    public void A_bare_path_containing_the_option_separator_is_rejected()
    {
        var act = () => LiteDbConnectionString.ForSharedAccess("/var/weird;dir/state.db");

        act.Should().Throw<ArgumentException>().WithParameterName("pathOrConnectionString");
    }

    /// <summary>The store's own parameter name is what a caller sees, not this method's.</summary>
    [Fact]
    public void The_caller_chooses_the_reported_parameter_name()
    {
        var act = () => LiteDbConnectionString.ForSharedAccess(" ", "databasePath");

        act.Should().Throw<ArgumentException>().WithParameterName("databasePath");
    }
}
