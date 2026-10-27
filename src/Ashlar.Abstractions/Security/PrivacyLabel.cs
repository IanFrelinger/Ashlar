using System.Diagnostics.CodeAnalysis;

namespace Ashlar.Abstractions.Security;

/// <summary>
/// A privacy label: a kind of destination that data carrying it must not reach (SPEC-010 §3.1). Version 1 has
/// exactly these four, and the set is closed. Data carries a <see cref="PrivacyLabelSet"/> of zero or more of them.
/// </summary>
/// <remarks>
/// <para>Labels have no order and nothing ranks one above another. Each one forbids its own destinations, and a
/// send is decided by <see cref="Egress.PrivacyRule"/>.</para>
/// <para><b>Text.</b> Each label has one text form, <c>local-only</c>, <c>no-external-model</c>, <c>no-web</c> or
/// <c>no-network-export</c>, which <see cref="PrivacyLabelSet"/> prints and parses. The member names are not the
/// text form.</para>
/// <para><b>Values.</b> The numeric values are stable and are not the order of the text form. The zero value is
/// <see cref="LocalOnly"/>, so an uninitialised label is the most restrictive one: unlabelled data fails closed to
/// <c>local-only</c>.</para>
/// </remarks>
[Experimental(SecurityExperimental.DiagnosticId, UrlFormat = SecurityExperimental.UrlFormat)]
public enum PrivacyLabel
{
    /// <summary>
    /// <c>local-only</c>: the data must not reach anything outside the host boundary. Unlabelled data, an unknown
    /// label name and label text that does not parse are all treated as this label.
    /// </summary>
    LocalOnly = 0,

    /// <summary>
    /// <c>no-external-model</c>: the data must not reach a model that is not on this machine
    /// (<see cref="Egress.EgressDestinationClass.ExternalModel"/>), an in-house model server included.
    /// </summary>
    NoExternalModel = 1,

    /// <summary><c>no-web</c>: the data must not reach web search (<see cref="Egress.EgressDestinationClass.WebSearch"/>).</summary>
    NoWeb = 2,

    /// <summary>
    /// <c>no-network-export</c>: the data must not reach network, file-export and process destinations
    /// (<see cref="Egress.EgressDestinationClass.NetworkExport"/>).
    /// </summary>
    NoNetworkExport = 3,
}
