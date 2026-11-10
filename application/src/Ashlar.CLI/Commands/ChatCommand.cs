using System.CommandLine;
using System.CommandLine.Invocation;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Interactive CLI chat loop (Claude Code-style).
/// Wraps orchestrate command execution and offers setup/trust convenience commands.
/// </summary>
public sealed class ChatCommand : Command
{
    private readonly Func<OrchestrateCommand> _orchestrateFactory;

    /// <summary>Creates the interactive chat command with orchestration and bootstrap options.</summary>
    public ChatCommand(Func<OrchestrateCommand> orchestrateFactory)
        : base("chat", "Interactive chat mode for coding workflows (bootstrap aware)")
    {
        _orchestrateFactory = orchestrateFactory ?? throw new ArgumentNullException(nameof(orchestrateFactory));

        var providerOpt = new Option<string?>("--provider", "Provider override for orchestration turns.");
        var preferModelOpt = new Option<string?>("--prefer-model", "Model preference override (agentic|deterministic|auto).");
        var ephemeralOpt = new Option<bool>("--ephemeral", () => false, "Use ephemeral models while this chat session runs.");
        var promptOpt = new Option<string?>("--prompt", "Run a single prompt and exit (non-interactive).");
        var skipBootstrapOpt = new Option<bool>("--skip-bootstrap-check", () => false, "Skip startup bootstrap readiness check.");
        var bootstrapApplyOpt = new Option<bool>("--bootstrap-apply", () => false, "Attempt dependency installation during startup.");
        var includeOptionalOpt = new Option<bool>("--include-optional-deps", () => false, "Include optional dependencies in bootstrap checks/install.");
        var yesOpt = new Option<bool>("--yes", () => false, "Auto-approve bootstrap apply.");
        var demoLocalOpt = new Option<bool>(
            "--demo-local",
            () => true,
            "When provider is not set, run chat in local demo mode (enables ASHLAR_ALLOW_MOCK=1 and provider=mock-json).");

        AddOption(providerOpt);
        AddOption(preferModelOpt);
        AddOption(ephemeralOpt);
        AddOption(promptOpt);
        AddOption(skipBootstrapOpt);
        AddOption(bootstrapApplyOpt);
        AddOption(includeOptionalOpt);
        AddOption(yesOpt);
        AddOption(demoLocalOpt);

        this.SetHandler(async (InvocationContext ctx) =>
        {
            var provider = ctx.ParseResult.GetValueForOption(providerOpt);
            var preferModel = ctx.ParseResult.GetValueForOption(preferModelOpt);
            var ephemeral = ctx.ParseResult.GetValueForOption(ephemeralOpt);
            var prompt = ctx.ParseResult.GetValueForOption(promptOpt);
            var skipBootstrap = ctx.ParseResult.GetValueForOption(skipBootstrapOpt);
            var bootstrapApply = ctx.ParseResult.GetValueForOption(bootstrapApplyOpt);
            var includeOptional = ctx.ParseResult.GetValueForOption(includeOptionalOpt);
            var yes = ctx.ParseResult.GetValueForOption(yesOpt);
            var demoLocal = ctx.ParseResult.GetValueForOption(demoLocalOpt);
            var exitCode = await ExecuteAsync(
                provider,
                preferModel,
                ephemeral,
                prompt,
                skipBootstrap,
                bootstrapApply,
                includeOptional,
                yes,
                demoLocal,
                ctx.GetCancellationToken()).ConfigureAwait(false);
            ctx.ExitCode = exitCode;
        });
    }

    private async Task<int> ExecuteAsync(
        string? provider,
        string? preferModel,
        bool ephemeral,
        string? prompt,
        bool skipBootstrapCheck,
        bool bootstrapApply,
        bool includeOptionalDeps,
        bool yes,
        bool demoLocal,
        CancellationToken ct)
    {
        var state = new ChatState(provider, preferModel, ephemeral);
        if (state.Ephemeral)
            Environment.SetEnvironmentVariable("ASHLAR_EPHEMERAL_MODELS", "1");

        ApplyDemoLocalDefaults(state, demoLocal);

        if (!skipBootstrapCheck)
        {
            await BootstrapCommand.RunCheckAsync("demo", includeOptionalDeps, false, ct).ConfigureAwait(false);
            if (bootstrapApply)
            {
                var applyExit = await BootstrapCommand.RunApplyAsync("demo", includeOptionalDeps, yes, false, false, ct).ConfigureAwait(false);
                if (applyExit != 0)
                    Console.WriteLine("Bootstrap apply returned a non-zero code; chat session will continue.");
            }
        }

        if (!string.IsNullOrWhiteSpace(prompt))
            return await ExecuteTurnAsync(prompt, state, ct).ConfigureAwait(false);

        RenderWelcome(state);
        while (!ct.IsCancellationRequested)
        {
            Console.Write("ashlar> ");
            var line = Console.ReadLine();
            if (line == null)
                break;

            var input = line.Trim();
            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (input.StartsWith('/'))
            {
                var slashExit = await HandleSlashAsync(input, state, ct).ConfigureAwait(false);
                if (slashExit.HasValue)
                    return slashExit.Value;
                continue;
            }

            await ExecuteTurnAsync(input, state, ct).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int?> HandleSlashAsync(string input, ChatState state, CancellationToken ct)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();

        switch (cmd)
        {
            case "/help":
                Console.WriteLine("Slash commands:");
                Console.WriteLine("  /help                         Show this help");
                Console.WriteLine("  /exit | /quit                 Exit chat");
                Console.WriteLine("  /status                       Show current chat settings");
                Console.WriteLine("  /setup [--apply] [--include-optional] [--yes]");
                Console.WriteLine("  /provider <name>              Set provider for subsequent turns");
                Console.WriteLine("  /model <preference>           Set prefer-model (agentic|deterministic|auto)");
                Console.WriteLine("  /trust on|off                 Toggle trust env for this process");
                return null;

            case "/exit":
            case "/quit":
                return 0;

            case "/status":
                Console.WriteLine($"provider={state.Provider ?? "(default)"}");
                Console.WriteLine($"prefer-model={state.PreferModel ?? "(default)"}");
                Console.WriteLine($"ephemeral={(state.Ephemeral ? "on" : "off")}");
                Console.WriteLine($"trust={Environment.GetEnvironmentVariable("ASHLAR_TRUST_ENABLED") ?? "unset"}");
                return null;

            case "/setup":
            case "/doctor":
            {
                var apply = parts.Any(p => string.Equals(p, "--apply", StringComparison.OrdinalIgnoreCase));
                var includeOptional = parts.Any(p => string.Equals(p, "--include-optional", StringComparison.OrdinalIgnoreCase));
                var yes = parts.Any(p => string.Equals(p, "--yes", StringComparison.OrdinalIgnoreCase));
                await BootstrapCommand.RunCheckAsync("demo", includeOptional, false, ct).ConfigureAwait(false);
                if (apply)
                {
                    var applyExit = await BootstrapCommand.RunApplyAsync("demo", includeOptional, yes, false, false, ct).ConfigureAwait(false);
                    if (applyExit != 0)
                        Console.WriteLine("Setup apply failed; see logs above.");
                }
                return null;
            }

            case "/provider":
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: /provider <name>");
                    return null;
                }
                state.Provider = parts[1];
                Console.WriteLine($"Provider set to: {state.Provider}");
                return null;

            case "/model":
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: /model <agentic|deterministic|auto>");
                    return null;
                }
                state.PreferModel = parts[1];
                Console.WriteLine($"Prefer-model set to: {state.PreferModel}");
                return null;

            case "/trust":
                if (parts.Length < 2 || (parts[1] != "on" && parts[1] != "off"))
                {
                    Console.WriteLine("Usage: /trust on|off");
                    return null;
                }
                Environment.SetEnvironmentVariable("ASHLAR_TRUST_ENABLED", parts[1] == "on" ? "1" : "0");
                Console.WriteLine($"Trust set to: {parts[1]}");
                return null;

            default:
                Console.WriteLine($"Unknown slash command: {parts[0]} (use /help)");
                return null;
        }
    }

    private async Task<int> ExecuteTurnAsync(string request, ChatState state, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return 130;

        var orchestrate = _orchestrateFactory();
        return await orchestrate.ExecuteAsync(
            request,
            runtimeSpecPath: null,
            runtimeSpecJson: null,
            preferModel: state.PreferModel,
            provider: state.Provider,
            barrierLevel: null,
            preferredRegion: null,
            json: false,
            verbose: false).ConfigureAwait(false);
    }

    private static void RenderWelcome(ChatState state)
    {
        Console.WriteLine("Ashlar Chat");
        Console.WriteLine("Type your request and press enter. Use /help for chat commands.");
        Console.WriteLine($"provider={state.Provider ?? "(default)"}, prefer-model={state.PreferModel ?? "(default)"}");
    }

    /// <summary>Mutable chat session settings shared across interactive turns.</summary>
    private sealed class ChatState
    {
        /// <summary>Initializes chat session state from command-line defaults.</summary>
        public ChatState(string? provider, string? preferModel, bool ephemeral)
        {
            Provider = provider;
            PreferModel = preferModel;
            Ephemeral = ephemeral;
        }

        /// <summary>Execution provider override for subsequent orchestration turns.</summary>
        public string? Provider { get; set; }

        /// <summary>Model preference override (agentic, deterministic, auto).</summary>
        public string? PreferModel { get; set; }

        /// <summary>When true, ephemeral model lifecycle is enabled for this session.</summary>
        public bool Ephemeral { get; }
    }

    private static void ApplyDemoLocalDefaults(ChatState state, bool demoLocal)
    {
        if (!demoLocal)
            return;

        if (!string.IsNullOrWhiteSpace(state.Provider))
            return;

        Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", "1");
        state.Provider = "mock-json";
        if (string.IsNullOrWhiteSpace(state.PreferModel))
            state.PreferModel = "deterministic";

        Console.WriteLine("Chat demo-local mode enabled (provider=mock-json, ASHLAR_ALLOW_MOCK=1).");
    }
}
