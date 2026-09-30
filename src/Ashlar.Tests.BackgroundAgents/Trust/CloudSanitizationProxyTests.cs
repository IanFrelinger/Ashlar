using FluentAssertions;
using Ashlar.BackgroundAgents.Trust;
using Ashlar.BackgroundAgents.WebSearch;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Trust;

/// <summary>Tests for cloud sanitization proxy.</summary>
public class CloudSanitizationProxyTests
{
    [Fact]
    public void SanitizeForCloud_WhenAirGapped_AllowsWithoutFiltering()
    {
        var proxy = new CloudSanitizationProxy(new SensitiveContentFilter());

        var context = new OutgoingContext
        {
            SystemPrompt = "System",
            UserPrompt = "user@example.com",
            IsAirGapped = true,
        };

        var result = proxy.SanitizeForCloud(context);

        result.Allowed.Should().BeTrue();
        result.SanitizedContext!.UserPrompt.Should().Be("user@example.com");
    }

    [Fact]
    public void SanitizeForCloud_WhenPiiInPrompt_Blocks()
    {
        var proxy = new CloudSanitizationProxy(new SensitiveContentFilter());

        var context = new OutgoingContext
        {
            SystemPrompt = "System",
            UserPrompt = "contact user@example.com for help",
            IsAirGapped = false,
        };

        var result = proxy.SanitizeForCloud(context);

        result.Allowed.Should().BeFalse();
        result.BlockReason.Should().Contain("PII");
    }

    [Fact]
    public void SanitizeForCloud_WhenNoPii_AllowsWithRedaction()
    {
        var proxy = new CloudSanitizationProxy(new SensitiveContentFilter());
        var auditLog = new InMemorySanitizationAuditLog();
        proxy = new CloudSanitizationProxy(new SensitiveContentFilter(), null, auditLog);

        var context = new OutgoingContext
        {
            SystemPrompt = "System",
            UserPrompt = "What is the weather?",
            IsAirGapped = false,
        };

        var result = proxy.SanitizeForCloud(context);

        result.Allowed.Should().BeTrue();
        result.SanitizedContext!.UserPrompt.Should().Be("What is the weather?");
    }

    // INVERTED. This used to be SanitizeForCloud_WhenNoFilter_Allows and pinned the pass-through as
    // the contract -- with a prompt that carries an email address. A sanitizer that cannot inspect
    // content must block, and say why, rather than wave the prompt through unaudited.
    [Fact]
    public void SanitizeForCloud_WhenNoFilter_BlocksAndAuditsTheBlock()
    {
        var auditLog = new InMemorySanitizationAuditLog();
        var proxy = new CloudSanitizationProxy(contentFilter: null, taxonomy: null, auditLog: auditLog);

        var context = new OutgoingContext
        {
            SystemPrompt = "S",
            UserPrompt = "user@test.com",
            IsAirGapped = false,
        };

        var result = proxy.SanitizeForCloud(context);

        result.Allowed.Should().BeFalse();
        result.SanitizedContext.Should().BeNull();
        result.BlockReason.Should().Contain("No sensitive-content filter");
        auditLog.GetRecent(10).Should().ContainSingle()
            .Which.Disposition.Should().Be("blocked");
    }

    // A prompt with no PII at all is blocked too: without a filter nothing can be shown clean. This
    // is what distinguishes "no filter blocks" from "the PII check blocked".
    [Fact]
    public void SanitizeForCloud_WhenNoFilter_BlocksEvenAPromptWithNoPii()
    {
        var proxy = new CloudSanitizationProxy(contentFilter: null);

        var result = proxy.SanitizeForCloud(new OutgoingContext
        {
            SystemPrompt = "System",
            UserPrompt = "What is the weather?",
            IsAirGapped = false,
        });

        result.Allowed.Should().BeFalse();
        result.BlockReason.Should().Contain("No sensitive-content filter");
    }

    // POSITIVE CONTROL for the two above: an air-gapped context never leaves the machine, so it is
    // still allowed with no filter.
    [Fact]
    public void SanitizeForCloud_WhenNoFilterButAirGapped_StillAllows()
    {
        var proxy = new CloudSanitizationProxy(contentFilter: null);

        var result = proxy.SanitizeForCloud(new OutgoingContext
        {
            SystemPrompt = "System",
            UserPrompt = "user@test.com",
            IsAirGapped = true,
        });

        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void SanitizeForCloud_WhenPiiRedacted_LogsRedaction()
    {
        var filter = new SensitiveContentFilter(blockQueriesWithPii: false);
        var auditLog = new InMemorySanitizationAuditLog();
        var proxy = new CloudSanitizationProxy(filter, null, auditLog);

        var context = new OutgoingContext
        {
            SystemPrompt = "Analyze",
            UserPrompt = "Email: user@example.com",
            IsAirGapped = false,
        };

        var result = proxy.SanitizeForCloud(context);

        result.Allowed.Should().BeTrue();
        result.SanitizedContext!.UserPrompt.Should().Contain("[REDACTED]");
        var entries = auditLog.GetRecent(10);
        // Exactly one: the redaction happened once. A second loop used to log every redaction
        // again, so the trail showed two for each one that happened.
        entries.Should().ContainSingle().Which.Disposition.Should().Be("redacted");
        result.Redactions.Should().ContainSingle();
    }
}
