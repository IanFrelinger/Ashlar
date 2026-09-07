using System.CommandLine;
using System.CommandLine.Invocation;
using Ashlar.Manifest;

namespace Ashlar.CLI.Commands;

/// <summary>
/// <c>ashlar run "&lt;request&gt;"</c> — execute a request through the project's configured
/// provider, gated by verification.
///
/// <para>The product rule this verb enforces: <strong>you cannot run what does not
/// verify</strong>. The courses execute first; a failing wall refuses the run at exit 65
/// with the failing course named. Only a verified project reaches the orchestrator.</para>
///
/// <para>v0 semantics, stated honestly: the request runs through the existing orchestration
/// machinery with the provider/model taken from the manifest's first modelled agent (the
/// scaffold ships <c>mock</c>, so a fresh project runs offline with zero setup). Mapping
/// each manifest agent onto its own orchestration role is the M1 integration, not this
/// slice — this slice makes the manifest the thing that decides HOW a run executes.</para>
/// </summary>
public sealed class RunCommand : Command
{
    private readonly Func<OrchestrateCommand> _orchestrate;

    /// <summary>Creates a new RunCommand instance. The orchestrator is factory-injected —
    /// the house pattern for commands that live outside Program's private ServiceProvider.</summary>
    public RunCommand(Func<OrchestrateCommand> orchestrate) : base("run", "Run a request through this project — verified first, then executed.")
    {
        var requestArg = new Argument<string>("request", "What to do, e.g. \"classify the invoices in ./inbox\".");
        var pathOpt = new Option<DirectoryInfo>(
            name: "--path",
            description: "Project directory (defaults to current).",
            getDefaultValue: () => new DirectoryInfo(Environment.CurrentDirectory));

        _orchestrate = orchestrate;
        AddArgument(requestArg);
        AddOption(pathOpt);

        this.SetHandler(async (InvocationContext ctx) =>
        {
            ctx.ExitCode = await ExecuteAsync(
                ctx.ParseResult.GetValueForArgument(requestArg),
                ctx.ParseResult.GetValueForOption(pathOpt)!,
                _orchestrate);
        });
    }

    private static async Task<int> ExecuteAsync(string request, DirectoryInfo directory, Func<OrchestrateCommand> orchestrateFactory)
    {
        var manifestPath = Path.Combine(directory.FullName, "ashlar.yaml");
        var policyPath = Path.Combine(directory.FullName, "ashlar.policy.yaml");
        if (!File.Exists(manifestPath) || !File.Exists(policyPath))
        {
            Console.Error.WriteLine($"not an ashlar project: {directory.FullName}");
            Console.Error.WriteLine("start one with:  ashlar init <name>");
            return 1;
        }

        var manifestYaml = await File.ReadAllTextAsync(manifestPath);
        var verification = ProjectVerifier.Verify(
            manifestYaml,
            await File.ReadAllTextAsync(policyPath),
            directory.FullName);

        if (!verification.Verified)
        {
            var failed = verification.Courses.First(c => !c.Passed);
            Console.Error.WriteLine($"refusing to run: course '{failed.Name}' failed — {failed.Detail}");
            Console.Error.WriteLine("you cannot run what does not verify. fix it, then:  ashlar verify");
            return 65;
        }

        // Verified above, so the manifest loads.
        ManifestLoader.TryLoad(manifestYaml, out var manifest, out _);

        // Provider comes from the manifest, not from flags: the project decides how it runs.
        var model = manifest!.Agents.Select(a => a.Model).FirstOrDefault(m => m is not null);
        var provider = model?.Provider;
        if (string.IsNullOrWhiteSpace(provider))
        {
            provider = "mock";
            Console.WriteLine("  no agent declares a model — running on the offline mock provider.");
            Console.WriteLine("  point an agent at a real one in ashlar.yaml when you have it.");
        }
        // A provider named in ashlar.yaml that this build cannot route to is a contract error, and
        // it is refused here rather than at request time, where the echo fallback would have
        // answered for it and the run would have reported success over a model never called.
        if (!Ashlar.Infrastructure.Execution.ProviderFactory.IsKnownProvider(provider))
        {
            Console.Error.WriteLine(
                $"refusing to run: ashlar.yaml names model provider '{provider}', which this build does not know.");
            Console.Error.WriteLine(
                $"known providers: {Ashlar.Infrastructure.Execution.ProviderFactory.KnownProviderList()}");
            Console.Error.WriteLine(
                "fix the agent's model.provider in ashlar.yaml — use `mock` to run offline on canned responses.");
            return 65;
        }

        if (string.Equals(provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            // The mock provider is explicitly opted into by the manifest; mirror the flag the
            // CI gates use so the provider layer accepts it.
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", "1");
        }

        Console.WriteLine($"  run · {manifest.Metadata.Name} · provider {provider}"
            + (model?.Id is { Length: > 0 } id ? $" · model {id}" : string.Empty));
        Console.WriteLine();

        var orchestrate = orchestrateFactory();
        return await orchestrate.ExecuteAsync(
            request,
            runtimeSpecPath: null,
            runtimeSpecJson: null,
            preferModel: model?.Id,
            provider: provider,
            barrierLevel: null,
            preferredRegion: null,
            json: false,
            verbose: false);
    }
}
