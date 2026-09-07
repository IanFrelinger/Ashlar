using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.RegularExpressions;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;

namespace Ashlar.CLI.Commands;

/// <summary>
/// <c>ashlar policy</c> — read and safely raise/lower the one policy dial an operator turns after
/// deploy: the self-extend mode. New projects ship <c>sealed</c> (nothing changes after deploy). The
/// staged auto-apply opt-in is <c>ashlar policy set self_extend proposing</c> (propose &amp; hold for
/// review) or <c>self-extending</c> (auto-admit within budget, gated by the post-apply canary) — the
/// exact command the gate's own refusal and the project scaffold already tell you to run.
///
/// <para>This is the only supported way to edit the operator-owned policy after <c>init</c>, and it is
/// deliberately narrow: it changes <b>only</b> <c>selfExtend.mode</c>, never the governance floor
/// (<c>never</c>, <c>sandbox</c>, <c>trustedSigners</c>). It edits in place — preserving every other
/// field and comment — and validates the RESULT before writing, so a flip that would produce an
/// invalid policy (e.g. <c>self-extending</c> with no <c>gatesRequired</c>) is refused with nothing
/// written. The daemon re-reads the policy each cycle, so the change takes effect without a restart.</para>
/// </summary>
public sealed class PolicyCommand : Command
{
    /// <summary>Creates a new PolicyCommand instance.</summary>
    public PolicyCommand() : base("policy", "Inspect and set the project's self-extend policy dial.")
    {
        AddCommand(BuildShow());
        AddCommand(BuildSet());
    }

    private static Option<string> PathOption() => new(
        name: "--path",
        getDefaultValue: () => ".",
        description: "Project directory containing ashlar.policy.yaml (defaults to the current directory).");

    private static Command BuildShow()
    {
        var pathOpt = PathOption();
        var cmd = new Command("show", "Print the project's self-extend posture (mode, budget, gates, trust).") { pathOpt };
        cmd.SetHandler((InvocationContext ctx) =>
            // `--format-json` is global, so it parses here even though this command only ever prints
            // prose. Refuse it rather than hand a caller's parser eight lines of text under exit 0.
            ctx.ExitCode = CommandExecutionSupport.RefuseJsonFormat(ctx.ParseResult, "policy show", Console.Error)
                ?? Show(ctx.ParseResult.GetValueForOption(pathOpt)!, Console.Out, Console.Error));
        return cmd;
    }

    private static Command BuildSet()
    {
        // `policy set self_extend <mode>` — matches the phantom command the gate refusal and the
        // scaffold already print, so that guidance now works verbatim.
        var keyArg = new Argument<string>("key", "The dial to set. Only 'self_extend' is supported.");
        var valueArg = new Argument<string>("value", "New value: sealed | proposing | self-extending.");
        var pathOpt = PathOption();
        var cmd = new Command("set", "Set a policy dial. Only `set self_extend <mode>` is supported.")
        {
            keyArg, valueArg, pathOpt
        };
        cmd.SetHandler((InvocationContext ctx) =>
            // Same as `show`, and the worse half of the defect: this one WRITES. An operator's script
            // reads exit 0 and believes it parsed a result, while the dial moved underneath it.
            // Refusing before Set() also guarantees the policy is left exactly as it was.
            ctx.ExitCode = CommandExecutionSupport.RefuseJsonFormat(ctx.ParseResult, "policy set", Console.Error) ?? Set(
                ctx.ParseResult.GetValueForArgument(keyArg),
                ctx.ParseResult.GetValueForArgument(valueArg),
                ctx.ParseResult.GetValueForOption(pathOpt)!,
                Console.Out, Console.Error));
        return cmd;
    }

    /// <summary>Read-only view of the self-extend posture. Testable via injected writers.</summary>
    public static int Show(string projectDir, TextWriter stdout, TextWriter stderr)
    {
        var policyPath = Path.Combine(projectDir, "ashlar.policy.yaml");
        if (!File.Exists(policyPath))
        {
            stderr.WriteLine($"not an ashlar project (no ashlar.policy.yaml at {projectDir})");
            return 1;
        }
        if (!PolicyLoader.TryLoad(File.ReadAllText(policyPath), out var policy, out var reason))
        {
            stderr.WriteLine($"policy is invalid: {reason}");
            return 1;
        }
        var se = policy!.SelfExtend;
        stdout.WriteLine($"self-extend mode : {se.Mode}");
        stdout.WriteLine($"  budget         : {se.Budget.Extensions} per {se.Budget.Window}");
        stdout.WriteLine($"  gatesRequired  : [{string.Join(", ", se.GatesRequired)}]");
        stdout.WriteLine($"  mayAdd         : [{string.Join(", ", se.MayAdd)}]");
        stdout.WriteLine($"  trustedSigners : {se.TrustedSigners.Count}");
        stdout.WriteLine();
        stdout.WriteLine(se.Mode switch
        {
            SelfExtendMode.Sealed => "sealed: nothing changes after deploy. Raise with `ashlar policy set self_extend proposing`.",
            SelfExtendMode.Proposing => "proposing: self-extend cycles are HELD for review (`ashlar gates`). No auto-apply.",
            SelfExtendMode.SelfExtending => "self-extending: admitted cycles AUTO-APPLY within budget, gated by the post-apply canary.",
            _ => string.Empty,
        });
        return 0;
    }

    /// <summary>
    /// Sets the self-extend mode in place. Testable via injected writers. Only the <c>self_extend</c>
    /// key is accepted; the value must be a valid mode; the edited policy must still load, or nothing
    /// is written.
    /// </summary>
    public static int Set(string key, string value, string projectDir, TextWriter stdout, TextWriter stderr)
    {
        var normalizedKey = key.Replace('-', '_').ToLowerInvariant();
        if (normalizedKey is not ("self_extend" or "selfextend" or "mode"))
        {
            stderr.WriteLine($"unsupported key '{key}'. Only `self_extend` (the mode) can be set; the governance "
                + "floor (never, sandbox, trustedSigners) is not editable through this command — edit ashlar.policy.yaml directly for those.");
            return 1;
        }

        var mode = value.Trim();
        if (!SelfExtendMode.All.Contains(mode, StringComparer.Ordinal))
        {
            stderr.WriteLine($"unknown mode '{value}'; expected one of {string.Join(", ", SelfExtendMode.All)}.");
            return 1;
        }

        var policyPath = Path.Combine(projectDir, "ashlar.policy.yaml");
        if (!File.Exists(policyPath))
        {
            stderr.WriteLine($"not an ashlar project (no ashlar.policy.yaml at {projectDir})");
            return 1;
        }

        var original = File.ReadAllText(policyPath);
        // Refuse to edit a policy that does not already load — never turn a broken policy into a
        // different broken policy; the operator must fix it first.
        if (!PolicyLoader.TryLoad(original, out var current, out var currentReason))
        {
            stderr.WriteLine($"current policy is invalid: {currentReason}. Fix it before setting a mode.");
            return 1;
        }

        if (string.Equals(current!.SelfExtend.Mode, mode, StringComparison.Ordinal))
        {
            stdout.WriteLine($"self-extend mode is already '{mode}' — nothing changed.");
            return 0;
        }

        if (!TryReplaceSelfExtendMode(original, mode, out var edited))
        {
            stderr.WriteLine("could not locate selfExtend.mode in ashlar.policy.yaml; leave the file's structure intact "
                + "or edit it by hand. Nothing was written.");
            return 1;
        }

        // Validate the RESULT before writing. Fail-closed: a flip that would produce an invalid policy
        // (self-extending / proposing with no gatesRequired) is refused with nothing written.
        if (!PolicyLoader.TryLoad(edited, out var reparsed, out var newReason))
        {
            stderr.WriteLine($"setting mode to '{mode}' would make the policy invalid: {newReason} Nothing was written.");
            // A refusal on the ARMING command has to name the missing step, or the operator is
            // told the only documented path off `sealed` is closed and given nowhere to go. This
            // is the case a project scaffolded before the terms were pre-filled lands in.
            foreach (var line in MissingArmingTerms(current))
            {
                stderr.WriteLine(line);
            }
            return 1;
        }

        // Trust the OUTCOME, not the textual edit. If the effective mode after the edit is not what was
        // asked — the classic cause is a duplicate selfExtend.mode entry, which YAML resolves last-wins
        // while the edit changed a different occurrence — refuse. A `disarm` that silently left a node
        // armed (or an arm that silently did nothing) is the worst failure this command could have.
        if (!string.Equals(reparsed!.SelfExtend.Mode, mode, StringComparison.Ordinal))
        {
            stderr.WriteLine($"the edit did not take effect — the effective mode is still '{reparsed.SelfExtend.Mode}'. "
                + "ashlar.policy.yaml likely has a duplicate selfExtend.mode entry; fix it by hand. Nothing was written. "
                + "(To stop a running node now regardless, use `ashlar background-agent disarm`.)");
            return 1;
        }

        File.WriteAllText(policyPath, edited);

        stdout.WriteLine($"self-extend mode: {current.SelfExtend.Mode} → {mode}");
        stdout.WriteLine(mode switch
        {
            SelfExtendMode.SelfExtending =>
                "  ARMED: this node will now AUTO-APPLY admitted self-extend cycles within budget "
                + $"({current.SelfExtend.Budget.Extensions} per {current.SelfExtend.Budget.Window}), gated by the post-apply "
                + "canary (a change that fails verification is rolled back). Disarm with `ashlar policy set self_extend proposing` "
                + "or stop everything now with `ashlar background-agent disarm`."
                + (current.SelfExtend.Budget.Extensions == 0
                    ? "\n  NOTE: budget is 0 per window — nothing will auto-admit until you raise selfExtend.budget.extensions."
                    : string.Empty),
            SelfExtendMode.Proposing =>
                "  Self-extend cycles are now HELD for review (`ashlar gates`); no auto-apply."
                // Mirror the self-extending budget-0 note above (SelfExtendMode.SelfExtending case): a
                // budget of 0 admits nothing, and `ashlar verify` fails the envelope course on exactly
                // this state — so the documented staged path (`set self_extend proposing`) lands on a
                // red wall unless the operator funds the budget. Warn loudly; keep the write.
                + (current.SelfExtend.Budget.Extensions == 0
                    ? "\n  WARNING: budget is 0 per window — nothing will be admitted, and `ashlar verify` "
                      + "flags this state. Fund it (raise selfExtend.budget.extensions) or expect verify to fail."
                    : string.Empty),
            SelfExtendMode.Sealed =>
                "  Sealed: no self-extension will be admitted at all.",
            _ => string.Empty,
        });
        stdout.WriteLine("  (The daemon re-reads the policy each cycle — no restart needed.)");
        return 0;
    }

    /// <summary>
    /// The terms a policy must carry before the dial can be raised off <c>sealed</c>, listed as the
    /// exact YAML to add.
    ///
    /// <para>Why this exists: <c>ashlar policy set self_extend proposing</c> is the arming step the
    /// gate's own refusal, <c>policy show</c> and the scaffolded policy all recommend — and on a
    /// project scaffolded with <c>gatesRequired: []</c> it is refused, because a mode that admits
    /// must say what it admits against. The refusal was correct and led nowhere: it named a rule,
    /// not a step. There has to be a supported path from <c>init</c> to an armed node, and where the
    /// documents do not yet allow one the CLI must say precisely what to write.</para>
    /// </summary>
    public static IReadOnlyList<string> MissingArmingTerms(AshlarPolicy policy)
    {
        var lines = new List<string>();
        var edits = new List<string>();

        if (policy.SelfExtend.GatesRequired.Count == 0)
        {
            edits.Add("    gatesRequired: [tests]   # every gate here must have RUN and PASSED before anything is admitted");
        }
        if (policy.SelfExtend.MayAdd.Count == 0)
        {
            edits.Add("    mayAdd: [brick]          # the only kind an application may ever add to itself");
        }
        if (policy.SelfExtend.Budget.Extensions < 1)
        {
            edits.Add("    budget:");
            edits.Add("      extensions: 1          # 0 admits nothing, and `ashlar verify` fails an unsealed policy that can never admit");
            edits.Add($"      window: {policy.SelfExtend.Budget.Window}");
        }

        if (edits.Count == 0)
        {
            return lines;
        }

        lines.Add(string.Empty);
        lines.Add("The missing step is in ashlar.policy.yaml, under selfExtend: — these are the TERMS the dial");
        lines.Add("turns on. While the mode is sealed they permit nothing, so filling them in arms nothing by");
        lines.Add("itself; the mode is the only thing that arms. Add (or raise) these, then run the set again:");
        lines.Add(string.Empty);
        lines.Add("  selfExtend:");
        lines.AddRange(edits);
        lines.Add(string.Empty);
        lines.Add("Projects scaffolded by a newer `ashlar init` already carry them.");
        return lines;
    }

    /// <summary>
    /// Replaces the value of the <c>mode:</c> line WITHIN the <c>selfExtend:</c> block, preserving
    /// every other line, comment, indentation, trailing comment, and the file's newline style. Scoped
    /// to the selfExtend block so a <c>mode:</c> under any other key is never touched. Returns false if
    /// the block or its mode line cannot be found (caller then writes nothing).
    /// </summary>
    public static bool TryReplaceSelfExtendMode(string text, string newMode, out string result)
    {
        result = text;
        // Rejoin with the file's dominant newline, but SPLIT on any ending so a mixed CRLF/LF file
        // (which still loads) is scanned correctly rather than spuriously refused.
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = Regex.Split(text, @"\r\n|\r|\n");

        int selfExtendIdx = -1, selfExtendIndent = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^(\s*)selfExtend:\s*(#.*)?$");
            if (m.Success)
            {
                selfExtendIdx = i;
                selfExtendIndent = m.Groups[1].Value.Length;
                break;
            }
        }
        if (selfExtendIdx < 0)
        {
            return false;   // no selfExtend block
        }

        for (var i = selfExtendIdx + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;   // blank / comment lines belong to whatever block they sit in
            }
            var indent = line.Length - line.TrimStart().Length;
            if (indent <= selfExtendIndent)
            {
                return false;   // reached a sibling/parent key before finding mode — block has no mode line
            }
            var mm = Regex.Match(line, @"^(\s*mode:\s*)(\S+)(.*)$");
            if (mm.Success)
            {
                lines[i] = mm.Groups[1].Value + newMode + mm.Groups[3].Value;
                result = string.Join(newline, lines);
                return true;
            }
        }
        return false;   // block ended (EOF) without a mode line
    }
}
