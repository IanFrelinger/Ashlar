using System.Globalization;

namespace Ashlar.Manifest.Admission;

/// <summary>
/// Decides what an evaluated proposal's outcome means under the policy — the mode semantics
/// of SPEC-004, as pure logic.
///
/// <para>Order of the rules matters and is deliberate: the envelope is checked before the
/// mode, because a kind outside <c>mayAdd</c> must be rejected even in self-extending mode
/// with budget to spare; and a failed course rejects before anything else is consulted,
/// because no mode admits unverified work. There is no rule that yields
/// <see cref="ProposalState.Admitted"/> from anything but a full pass — no administrative
/// path into admitted, including for the vendor.</para>
/// </summary>
public static class AdmissionGate
{
    /// <summary>
    /// Applies the policy's mode semantics to an evaluated proposal.
    /// </summary>
    /// <param name="policy">The operator-owned envelope.</param>
    /// <param name="proposal">The proposal, courses already evaluated.</param>
    /// <param name="admittedInWindow">How many extensions were already admitted in the
    /// current budget window. The caller derives this from the store; this layer only
    /// compares it to the budget.</param>
    public static AdmissionOutcome Decide(AshlarPolicy policy, ExtensionProposal proposal, int admittedInWindow)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(proposal);

        // 1 — sealed short-circuits everything: the write edge refuses, the attempt is recorded.
        if (policy.SelfExtend.Mode == SelfExtendMode.Sealed)
        {
            return new AdmissionOutcome
            {
                State = ProposalState.Rejected,
                Reason = "mode is sealed: nothing changes after deploy. Raise the dial deliberately: ashlar policy set self_extend proposing",
            };
        }

        // 2 — the envelope: a kind outside mayAdd is never admissible, in any mode, with any
        // budget. Adding a brick is capability inside the envelope; anything else widens it.
        if (!policy.SelfExtend.MayAdd.Contains(proposal.Kind, StringComparer.Ordinal))
        {
            return new AdmissionOutcome
            {
                State = ProposalState.Rejected,
                Reason = $"kind '{proposal.Kind}' is outside the envelope (mayAdd: [{string.Join(", ", policy.SelfExtend.MayAdd)}]). Widening the envelope is the operator's act, in the policy file, never the application's.",
            };
        }

        // 3 — every required gate must have run and passed. A missing course is a failure,
        // not an exemption: fail closed.
        foreach (var required in policy.SelfExtend.GatesRequired)
        {
            var course = proposal.Courses.FirstOrDefault(c => string.Equals(c.Name, required, StringComparison.Ordinal));
            if (course is null)
            {
                return new AdmissionOutcome
                {
                    State = ProposalState.Rejected,
                    Reason = $"required gate '{required}' did not run. A gate that did not run did not pass.",
                };
            }
            if (!course.Passed)
            {
                return new AdmissionOutcome
                {
                    State = ProposalState.Rejected,
                    Reason = $"gate '{required}' failed: {course.Detail}",
                };
            }
        }

        // 4 — mode semantics on a full pass.
        if (policy.SelfExtend.Mode == SelfExtendMode.Proposing)
        {
            return new AdmissionOutcome
            {
                State = ProposalState.Held,
                Reason = "all gates passed; mode is proposing — a person seats the stone.",
            };
        }

        // Programmatic callers can bypass PolicyLoader. Invalid controls must not admit.
        if (!TryValidateBudget(policy.SelfExtend.Budget, out var budgetReason))
        {
            return new AdmissionOutcome
            {
                State = ProposalState.Held,
                Reason = budgetReason + " Failing closed to a human decision.",
            };
        }

        // self-extending: admit within budget; budget exhaustion DEGRADES to held, never to
        // admit and never to silent drop.
        if (admittedInWindow >= policy.SelfExtend.Budget.Extensions)
        {
            return new AdmissionOutcome
            {
                State = ProposalState.Held,
                Reason = $"all gates passed, but the budget ({policy.SelfExtend.Budget.Extensions} per {policy.SelfExtend.Budget.Window}) is spent — held for a person instead.",
            };
        }

        return new AdmissionOutcome
        {
            State = ProposalState.Admitted,
            Reason = $"all gates passed; mode is self-extending; budget {admittedInWindow + 1} of {policy.SelfExtend.Budget.Extensions} in {policy.SelfExtend.Budget.Window}.",
        };
    }

    /// <summary>Validates the same budget at policy load and at the admission boundary.</summary>
    internal static bool TryValidateBudget(PolicyBudget? budget, out string reason)
    {
        if (budget is null)
        {
            reason = "selfExtend.budget must be a mapping, not null.";
            return false;
        }
        if (budget.Extensions < 0)
        {
            reason = "selfExtend.budget.extensions must be nonnegative; 0 disables automatic admission.";
            return false;
        }
        if (!TryParseWindow(budget.Window, out _))
        {
            reason = "selfExtend.budget.window must be a positive integer followed by m, h or d, within the supported duration range.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Parses ASCII integer minutes, hours or days, without signs or whitespace. The amount
    /// fits an Int32 and the duration fits a TimeSpan. Invalid or overflowing text never throws.
    /// </summary>
    public static bool TryParseWindow(string? window, out TimeSpan span)
    {
        span = default;
        // Ten digits plus one unit bounds both parsing work and the accepted integer spelling.
        if (window is null || window.Length < 2 || window.Length > 11)
        {
            return false;
        }
        foreach (var digit in window.AsSpan(0, window.Length - 1))
        {
            if (!char.IsAsciiDigit(digit))
            {
                return false;
            }
        }
        var ticksPerUnit = window[^1] switch
        {
            'm' => TimeSpan.TicksPerMinute,
            'h' => TimeSpan.TicksPerHour,
            'd' => TimeSpan.TicksPerDay,
            _ => 0L,
        };
        if (ticksPerUnit == 0
            || !int.TryParse(window.AsSpan(0, window.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0
            || amount > TimeSpan.MaxValue.Ticks / ticksPerUnit)
        {
            return false;
        }
        span = TimeSpan.FromTicks(amount * ticksPerUnit);
        return true;
    }
}
