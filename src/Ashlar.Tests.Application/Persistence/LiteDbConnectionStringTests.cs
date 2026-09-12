using FluentAssertions;
using Ashlar.Core.Application.Persistence;
using Xunit;

namespace Ashlar.Tests.Application.Persistence;

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
/// <remarks>
/// Lives in <c>Ashlar.Tests.Application</c> because <c>LiteDbConnectionString</c> lives in
/// <c>Ashlar.Core.Application</c>, and <c>docs/architecture/TestingStrategyTracking-v1.md</c>
/// assigns that source tree to this project. That is not bookkeeping: <c>kernel-coverage</c>
/// measures <c>Ashlar.Core.Application</c> against a line floor using only this project's run, so a
/// test of an application type parked in the infrastructure suite leaves the type it covers
/// counted as uncovered.
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
    [InlineData("Filename=/var/state/Connection=x/custom.db;Connection=Direct")]
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

    /// <summary>
    /// The filename is part of the string the mode is probed for, so an unanchored search would read
    /// a path that merely SPELLS <c>Connection=</c> as a mode the caller had chosen and hand back a
    /// Direct connection — the same silent-wrong-mode failure the ';' rejection above is written
    /// against, one layer further in, and one no convention test can see because the store does call
    /// the helper.
    /// </summary>
    [Theory]
    [InlineData("/var/state/Connection=x/state.db", "Filename=/var/state/Connection=x/state.db;Connection=Shared")]
    [InlineData("Filename=/var/state/Connection=x/state.db", "Filename=/var/state/Connection=x/state.db;Connection=Shared")]
    public void A_path_that_merely_spells_the_option_still_gets_shared_mode(string path, string expected)
    {
        LiteDbConnectionString.ForSharedAccess(path).Should().Be(expected);
    }

    /// <summary>
    /// Options other than <c>Connection</c> do not count as stating a mode, and neither does an
    /// option with no <c>=</c> at all.
    /// </summary>
    /// <remarks>
    /// These are the two paths through the option scan that nothing else reaches: a key that parses
    /// but is not <c>Connection</c>, and a segment the scan has to skip rather than index into. A
    /// helper that answered "a mode is already stated" for either would leave the store on LiteDB's
    /// Direct default, which is the single outcome this class exists to prevent.
    /// </remarks>
    [Theory]
    [InlineData("Filename=/tmp/custom.db;Password=hunter2", "Filename=/tmp/custom.db;Password=hunter2;Connection=Shared")]
    [InlineData("Filename=/tmp/custom.db;ReadOnly", "Filename=/tmp/custom.db;ReadOnly;Connection=Shared")]
    [InlineData("Filename=/tmp/custom.db;ReadOnly;Connection=Direct", "Filename=/tmp/custom.db;ReadOnly;Connection=Direct")]
    public void Other_options_do_not_state_a_connection_mode(string connectionString, string expected)
    {
        LiteDbConnectionString.ForSharedAccess(connectionString).Should().Be(expected);
    }

    /// <summary>The store's own parameter name is what a caller sees, not this method's.</summary>
    [Fact]
    public void The_caller_chooses_the_reported_parameter_name()
    {
        var act = () => LiteDbConnectionString.ForSharedAccess(" ", "databasePath");

        act.Should().Throw<ArgumentException>().WithParameterName("databasePath");
    }
}
