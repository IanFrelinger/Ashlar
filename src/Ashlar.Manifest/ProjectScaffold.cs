namespace Ashlar.Manifest;

/// <summary>
/// Produces the two documents a new project starts from. Used by <c>ashlar init</c>.
///
/// <para>Templates are authored text rather than serializer output, because the comments ARE
/// part of the contract's teaching surface — the policy header telling a reader the
/// application can never touch the file matters as much as the keys. Every scaffold is
/// round-tripped through <see cref="ManifestLoader"/> and <see cref="PolicyLoader"/> before
/// being returned: init must never emit a project its own loaders reject.</para>
/// </summary>
public static class ProjectScaffold
{
    /// <summary>
    /// Upper bound on a project name. The charset check alone accepts a 100k-character name, which
    /// then lands verbatim in <c>metadata.name</c> and on disk as a directory. A name is an identifier,
    /// not a document — cap it so a pathological input is refused up front rather than scaffolded.
    /// </summary>
    public const int MaxNameLength = 100;

    /// <summary>
    /// Builds the starter <c>ashlar.yaml</c> and <c>ashlar.policy.yaml</c> for a new project.
    /// </summary>
    /// <param name="name">Project name: letters, digits and hyphens, starting with a letter.</param>
    /// <param name="manifestYaml">The project document, when this returns true.</param>
    /// <param name="policyYaml">The policy document, when this returns true.</param>
    /// <param name="reason">Why scaffolding was refused, when this returns false.</param>
    public static bool TryScaffold(
        string? name,
        out string manifestYaml,
        out string policyYaml,
        out string reason)
    {
        manifestYaml = string.Empty;
        policyYaml = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            reason = "REJECTED: a project name is required.";
            return false;
        }

        var trimmed = name!.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            reason = $"REJECTED: project name is {trimmed.Length} characters; the maximum is {MaxNameLength}. It becomes a metadata.name and a directory — keep it short (e.g. invoice-triage).";
            return false;
        }
        if (!IsValidName(trimmed))
        {
            reason = $"REJECTED: '{trimmed}' is not a valid project name. Use letters, digits and hyphens, starting with a letter (e.g. invoice-triage).";
            return false;
        }

        var manifest = BuildManifest(trimmed);
        var policy = BuildPolicy();

        // Dogfood the contract: refuse to hand out documents the loaders would refuse to
        // load. A failure here is a bug in this class, and the caller deserves to know
        // rather than discovering it on first verify.
        if (!ManifestLoader.TryLoad(manifest, out _, out var manifestReason))
        {
            reason = $"REJECTED: scaffold produced a manifest its own loader rejects ({manifestReason}). This is a bug in ProjectScaffold.";
            return false;
        }
        if (!PolicyLoader.TryLoad(policy, out var parsed, out var policyReason))
        {
            reason = $"REJECTED: scaffold produced a policy its own loader rejects ({policyReason}). This is a bug in ProjectScaffold.";
            return false;
        }
        if (parsed!.SelfExtend.Mode != SelfExtendMode.Sealed)
        {
            reason = "REJECTED: scaffold default must be sealed. Self-extension is raised deliberately, by a person, never by a template.";
            return false;
        }

        manifestYaml = manifest;
        policyYaml = policy;
        reason = string.Empty;
        return true;
    }

    private static bool IsValidName(string name)
    {
        if (!char.IsAsciiLetter(name[0]))
        {
            return false;
        }
        return name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    private static string BuildManifest(string name) =>
        $"""
        # {name} — the project contract.
        # This file describes what the application IS. Agents may propose changes to it.
        # What the application may BECOME lives in ashlar.policy.yaml, which it cannot touch.
        apiVersion: ashlar/v1
        kind: Application
        metadata:
          name: {name}
          version: 0.1.0

        agents:
          - id: main
            # mock runs offline with canned responses, so `ashlar run` works before any
            # provider is configured. Point this at a real provider when you have one,
            # with provider: ollama and id: llama3, for example.
            model:
              provider: mock
            # Tools are named grants declared here — never self-addable (only bricks are, and only
            # when the policy allows). An agent cannot define a tool, and never carries a filesystem
            # root — the sandbox comes from the policy.
            tools: []
            gates: [tests]

        bricks: []

        targets:
          - name: local
            platform: native.process
            profile: full
        """.ReplaceLineEndings("\n") + "\n";

    private static string BuildPolicy() =>
        """
        # The envelope — operator only.
        # The application cannot read, propose, or modify this file. The gate can.
        # That asymmetry is the entire safety model: review this before you deploy,
        # because it is the only file the running app can never change.
        apiVersion: ashlar/v1
        kind: Policy

        sandbox:
          root: .
          writable: []

        selfExtend:
          # sealed | proposing | self-extending. New projects start sealed: raise the dial
          # deliberately, after you have watched the thing run.
          #   ashlar policy set self_extend proposing
          #
          # THE DIAL IS THE ONLY THING THAT ARMS. The three keys under it are the TERMS the
          # dial turns on, and while the mode is sealed they permit nothing at all — sealed
          # short-circuits admission before any of them is read. They are filled in here so
          # that the command above works on the project you just scaffolded: a policy with an
          # empty gatesRequired cannot be raised to proposing at all (an extension path with
          # no gates is not a gate), and one with a zero budget raises to a mode that can
          # never admit anything, which `ashlar verify` then fails. Tighten these before you
          # raise the dial, not after.
          mode: sealed
          # How many extensions may be admitted per window once the dial is raised.
          budget:
            extensions: 1
            window: 24h
          # Kinds the application may add to ITSELF. Only 'brick' is ever permitted — a brick
          # adds capability inside the envelope; a tool or a capability would widen it.
          mayAdd: [brick]
          # Every one of these must have RUN and PASSED before anything is admitted. A gate
          # that did not run did not pass.
          gatesRequired: [tests]

        # Mandatory. The loader refuses a policy that omits any of these — they are listed
        # here so the whole envelope is visible in one place, not because they are optional.
        never:
          - modify_gate
          - widen_sandbox
          - access_signing_keys
          - truncate_ledger
          - grant_capability
        """.ReplaceLineEndings("\n") + "\n";
}
