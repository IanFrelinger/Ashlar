using System.CommandLine;
using System.CommandLine.Invocation;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Signing;

namespace Ashlar.CLI.Commands;

/// <summary>
/// <c>ashlar keys</c> — the operator's local signing identity (SPEC-006 v1).
///
/// <para><c>keys init</c> generates the single Ed25519 operator keypair; <c>keys show</c>
/// prints its fingerprint, or says plainly there is none. Signing is <strong>presence-
/// activated</strong>: once a key exists, the gate signs every verdict it records; with no
/// key the system stays honestly unsigned and zero-setup still works. The private seed lives
/// only under the key directory (<c>ASHLAR_KEY_DIR</c> or <c>~/.ashlar/keys</c>) and never
/// enters a project, a bundle, or any command output — this verb prints the PUBLIC fingerprint
/// and nothing more.</para>
/// </summary>
public sealed class KeysCommand : Command
{
    /// <summary>Creates a new KeysCommand instance.</summary>
    public KeysCommand() : base("keys", "Manage the local operator signing key (SPEC-006).")
    {
        AddCommand(BuildInit());
        AddCommand(BuildShow());
        AddCommand(BuildTrust());
        AddCommand(BuildUntrust());
        AddCommand(BuildPeers());
    }

    private static Option<string?> KeyDirOption() => new(
        name: "--key-dir",
        description: "Key directory (defaults to $ASHLAR_KEY_DIR, else ~/.ashlar/keys).");

    private static Command BuildInit()
    {
        var rotateOpt = new Option<bool>(
            name: "--rotate",
            description: "Replace an existing key. The old PUBLIC key is retained under trusted/ so records it "
                       + "already signed keep verifying.");
        var keyDirOpt = KeyDirOption();
        var pathOpt = new Option<DirectoryInfo>(
            name: "--path",
            description: "Project directory (defaults to current). When it holds a .ashlar root, signed gate records are activated there.",
            getDefaultValue: () => new DirectoryInfo(Environment.CurrentDirectory));
        var cmd = new Command("init", "Generate the operator signing keypair.") { rotateOpt, keyDirOpt, pathOpt };
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            ctx.ExitCode = await InitAsync(
                ctx.ParseResult.GetValueForOption(rotateOpt),
                ctx.ParseResult.GetValueForOption(keyDirOpt),
                ctx.ParseResult.GetValueForOption(pathOpt) ?? new DirectoryInfo(Environment.CurrentDirectory));
        });
        return cmd;
    }

    private static async Task<int> InitAsync(bool rotate, string? keyDir, DirectoryInfo project)
    {
        var dir = string.IsNullOrWhiteSpace(keyDir) ? OperatorKey.ResolveKeyDir() : Path.GetFullPath(keyDir);
        SigningIdentity id;
        try
        {
            id = OperatorKey.Generate(dir, rotate);
        }
        catch (InvalidOperationException ex)
        {
            // The key already exists and --rotate was not given: the kernel's refusal, verbatim.
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        Console.WriteLine($"  {Gold(rotate ? "✓ operator key rotated" : "✓ operator key ready")}");
        Console.WriteLine($"  {Dim($"fingerprint {id.Fingerprint}")}");
        Console.WriteLine($"  {Dim($"stored in {dir}")}");
        Console.WriteLine();
        Console.WriteLine($"  {Dim("gate decisions on this machine are now signed. keep the private key here — never commit it.")}");

        // SPEC-006 S-6: activate signed gate records in the project this was run in, so the instant
        // the key came into existence is the instant after which an unsigned verdict there is a
        // stripped signature. Everything already recorded is grandfathered. NEVER on --rotate: the
        // marker is written once, and a rotation must not be able to re-open the grace window.
        var stateRoot = Path.Combine(project.FullName, ".ashlar");
        if (rotate || !Directory.Exists(stateRoot))
        {
            return 0;
        }
        try
        {
            var outcome = await new GateStore(stateRoot, id).ActivateSigningAsync(DateTimeOffset.UtcNow);
            if (outcome.ReplacedUnvouchedMarker)
            {
                // Its own sentence, not folded into the line below. A marker signed by a key this
                // machine does not vouch for was in force over this store and has just been
                // overwritten; an operator who reads past that has been told nothing.
                Console.WriteLine($"  {Dim("a signing marker under a key this machine does not vouch for was in force here and has been replaced with yours. if you did not expect that, find out who wrote it before trusting this store.")}");
            }

            // The count BEFORE the reassurance. `keys init` is the command an operator is told to
            // run, so it is the likeliest place for a planted record to be blessed unnoticed.
            var grandfathered = outcome.Marker.Grandfathered?.Count ?? 0;
            if (grandfathered > 0)
            {
                Console.WriteLine(
                    $"  {Dim($"grandfathering {grandfathered} unsigned record(s), {outcome.GrandfatheredAdmitted} of them admitted — trusted as-is, and NOT signed.")}");
                Console.WriteLine(
                    $"  {Dim("if that is more than you decided yourself, stop: an admitted record spends self-extension budget. inspect with: ashlar gates")}");
            }

            Console.WriteLine($"  {Dim($"gate records under {stateRoot} are signed from {outcome.Marker.ActivatedAt:u}; earlier unsigned records stay readable.")}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // The key is ready; the activation is not. Say so rather than look like both landed.
            Console.Error.WriteLine($"  the key is ready but signing could not be activated for {project.FullName}: {ex.Message}");
            Console.Error.WriteLine("  activate it later with:  ashlar gates sign-activate");
            return 1;
        }
    }

    private static Command BuildShow()
    {
        var keyDirOpt = KeyDirOption();
        var cmd = new Command("show", "Print the operator key fingerprint, or note that none exists.") { keyDirOpt };
        cmd.SetHandler((InvocationContext ctx) =>
        {
            ctx.ExitCode = Show(ctx.ParseResult.GetValueForOption(keyDirOpt));
        });
        return cmd;
    }

    private static int Show(string? keyDir)
    {
        var dir = string.IsNullOrWhiteSpace(keyDir) ? OperatorKey.ResolveKeyDir() : Path.GetFullPath(keyDir);
        SigningIdentity? id;
        try
        {
            id = OperatorKey.TryLoad(dir);
        }
        catch (InvalidOperationException ex)
        {
            // A corrupt key pair (e.g. a crash mid-rotation) must not be papered over: signing
            // with a mismatched identity is exactly what poisons the store. Surface it and stop.
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        if (id is null)
        {
            // S-3: never print a fingerprint for a key that is not there. Say plainly there is none.
            Console.WriteLine($"  {Dim("no operator key — gate decisions are recorded unsigned.")}");
            Console.WriteLine($"  {Dim("create one with:  ashlar keys init")}");
            return 0;
        }

        Console.WriteLine($"  {Gold("operator key")}  {Dim(id.Fingerprint)}");
        Console.WriteLine($"  {Dim($"stored in {dir}")}");
        return 0;
    }

    private static Command BuildTrust()
    {
        var fpArg = new Argument<string>("fingerprint", "Operator fingerprint to trust (ed25519:… — read it off the origin box's `keys show`).");
        var keyDirOpt = KeyDirOption();
        var cmd = new Command("trust", "Trust a signer's packages on this machine.") { fpArg, keyDirOpt };
        cmd.SetHandler((InvocationContext ctx) =>
            ctx.ExitCode = Trust(ctx.ParseResult.GetValueForArgument(fpArg), ctx.ParseResult.GetValueForOption(keyDirOpt)));
        return cmd;
    }

    private static int Trust(string fingerprint, string? keyDir)
    {
        var dir = string.IsNullOrWhiteSpace(keyDir) ? OperatorKey.ResolveKeyDir() : Path.GetFullPath(keyDir);
        if (!OperatorKey.IsValidFingerprint(fingerprint))
        {
            Console.Error.WriteLine($"REJECTED: '{fingerprint}' is not a valid operator fingerprint (expected ed25519: + 16 hex).");
            Console.Error.WriteLine("  Read it off the ORIGIN box with `ashlar keys show`, then type it here — sourcing the");
            Console.Error.WriteLine("  pin from the artifact you are authorizing is consent-by-fatigue, not a control.");
            return 1;
        }
        OperatorKey.Trust(fingerprint, dir);
        Console.WriteLine($"  {Gold("✓ trusted")}  {Dim(fingerprint)}");
        Console.WriteLine($"  {Dim("its sealed packages may now be admitted at this machine's gates. remove with `keys untrust`.")}");
        return 0;
    }

    private static Command BuildUntrust()
    {
        var fpArg = new Argument<string>("fingerprint", "Operator fingerprint to stop trusting.");
        var keyDirOpt = KeyDirOption();
        var cmd = new Command("untrust", "Stop trusting a signer's packages.") { fpArg, keyDirOpt };
        cmd.SetHandler((InvocationContext ctx) =>
            ctx.ExitCode = Untrust(ctx.ParseResult.GetValueForArgument(fpArg), ctx.ParseResult.GetValueForOption(keyDirOpt)));
        return cmd;
    }

    private static int Untrust(string fingerprint, string? keyDir)
    {
        var dir = string.IsNullOrWhiteSpace(keyDir) ? OperatorKey.ResolveKeyDir() : Path.GetFullPath(keyDir);
        if (OperatorKey.Untrust(fingerprint, dir))
        {
            Console.WriteLine($"  {Gold("✓ untrusted")}  {Dim(fingerprint)}");
            return 0;
        }
        Console.Error.WriteLine($"  {Dim($"{fingerprint} was not in the trust keychain — nothing to remove.")}");
        return 1;
    }

    private static Command BuildPeers()
    {
        var keyDirOpt = KeyDirOption();
        var cmd = new Command("peers", "List trusted signer fingerprints and the trust-set digest.") { keyDirOpt };
        cmd.SetHandler((InvocationContext ctx) =>
            ctx.ExitCode = Peers(ctx.ParseResult.GetValueForOption(keyDirOpt)));
        return cmd;
    }

    private static int Peers(string? keyDir)
    {
        var dir = string.IsNullOrWhiteSpace(keyDir) ? OperatorKey.ResolveKeyDir() : Path.GetFullPath(keyDir);
        var trusted = OperatorKey.ListTrusted(dir);
        if (trusted.Count == 0)
        {
            Console.WriteLine($"  {Dim("no trusted signers — imported packages are refused until you `keys trust <fp>`.")}");
            Console.WriteLine($"  {Dim($"trust-set digest {OperatorKey.TrustSetDigest(trusted)}")}");
            return 0;
        }
        Console.WriteLine($"  {Gold("trusted signers")}");
        foreach (var fp in trusted)
        {
            Console.WriteLine($"    {Dim(fp)}");
        }
        Console.WriteLine();
        Console.WriteLine($"  {Dim($"trust-set digest {OperatorKey.TrustSetDigest(trusted)}")}");
        Console.WriteLine($"  {Dim("compare this across boxes; a box off during a revocation still trusts the removed key.")}");
        return 0;
    }

    // Same colour discipline as the other verbs: gold marks the identity, dim is detail.
    private static readonly bool Color =
        Environment.GetEnvironmentVariable("NO_COLOR") is null && !Console.IsOutputRedirected;
    private static string Paint(string ansi, string t) => Color ? $"\x1b[{ansi}m{t}\x1b[0m" : t;
    private static string Gold(string t) => Paint("33", t);
    private static string Dim(string t) => Paint("90", t);
}
