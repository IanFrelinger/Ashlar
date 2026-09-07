using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.CLI.Commands;
using Ashlar.CLI.Commands.BackgroundAgent;
using Ashlar.CLI.Formatting;

namespace Ashlar.CLI;

/// <summary>Program.</summary>
static partial class Program
{
    /// <summary>
    /// Builds the CLI root command tree. Internal so Ashlar.Tests.CLI can assert the
    /// registered top-level commands without spawning the host (guards against a
    /// built-but-never-added command, which is how `trust` went missing in #162).
    /// </summary>
    internal static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("Ashlar command-line interface")
        {
            TreatUnmatchedTokensAsErrors = true
        };

        // Global options
        var jsonOpt = new Option<bool>(
            name: "--format-json",
            description: "Emit machine-readable JSON (stderr still carries logs)."
        );

        root.AddGlobalOption(jsonOpt);

        var verboseOpt = new Option<bool>(
            name: "--verbose",
            description: "Enable verbose logging and progress output."
        );
        root.AddGlobalOption(verboseOpt);

        // ashlar analyze (resolves from host lazily - host built only when heavy command runs)
        var analyzeCmd = new Command("analyze", "Run code/assembly analyzers and policies")
        {
            new Option<DirectoryInfo>(
                name: "--path",
                description: "Root path to analyze (defaults to current)",
                getDefaultValue: () => new DirectoryInfo(Environment.CurrentDirectory)
            )
        };
        analyzeCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="path">Path.</param>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (DirectoryInfo path, bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<AnalyzeCommand>();
                var exitCode = await cmd.ExecuteAsync(path, json, verbose);
                Environment.Exit(exitCode);
            },
            analyzeCmd.Options[0] as Option<DirectoryInfo> ?? throw new InvalidOperationException(),
            jsonOpt,
            verboseOpt);
        analyzeCmd.AddCommand(new AnalyzeBricksCommand());

        // ashlar validate
        var validateCmd = new Command("validate", "Run architecture tests/contract checks quickly")
        {
            new Option<string?>("--filter", "Optional test filter (Category/Trait)")
        };
        validateCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="filter">Filter.</param>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (string? filter, bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<ValidateCommand>();
                var exitCode = await cmd.ExecuteAsync(filter, json, verbose);
                Environment.Exit(exitCode);
            },
            validateCmd.Options[0] as Option<string?> ?? throw new InvalidOperationException(),
            jsonOpt,
            verboseOpt);

        // ashlar agent
        var agentCmd = new Command("agent", "Run an agent action")
        {
            new Option<string>("--name", "Agent name") { IsRequired = true },
            new Option<FileInfo?>("--input", "Optional input file")
        };
        agentCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="name">Name.</param>
            /// <param name="input">Input.</param>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (string name, FileInfo? input, bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<AgentCommand>();
                var exitCode = await cmd.ExecuteAsync(name, input, json, verbose);
                Environment.Exit(exitCode);
            },
            agentCmd.Options[0] as Option<string> ?? throw new InvalidOperationException(),
            agentCmd.Options[1] as Option<FileInfo?> ?? throw new InvalidOperationException(),
            jsonOpt,
            verboseOpt);

        // ashlar agent list
        var listAgentsCmd = new Command("list", "List available agents");
        listAgentsCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<ListAgentsCommand>();
                var exitCode = await cmd.ExecuteAsync(json, verbose);
                Environment.Exit(exitCode);
            },
            jsonOpt,
            verboseOpt);
        agentCmd.AddCommand(listAgentsCmd);

        // ashlar config
        var configCmd = new Command("config", "View or manage configuration");
        var configShowCmd = new Command("show", "Show current configuration");
        configShowCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<ConfigCommand>();
                var exitCode = await cmd.ExecuteAsync(json, verbose);
                Environment.Exit(exitCode);
            },
            jsonOpt,
            verboseOpt);
        configCmd.AddCommand(configShowCmd);
        var configValidateCmd = new Command("validate", "Validate configuration");
        configValidateCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<ConfigCommand>();
                var exitCode = await cmd.ValidateAsync(json, verbose);
                Environment.Exit(exitCode);
            },
            jsonOpt,
            verboseOpt);
        configCmd.AddCommand(configValidateCmd);
        var configExportPathOpt = new Option<FileInfo>("--path", "Output file path") { IsRequired = true };
        var configExportCmd = new Command("export", "Export configuration to file") { configExportPathOpt };
        configExportCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="path">Path.</param>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (FileInfo path, bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<ConfigCommand>();
                var exitCode = await cmd.ExportAsync(path.FullName, json, verbose);
                Environment.Exit(exitCode);
            },
            configExportPathOpt,
            jsonOpt,
            verboseOpt);
        configCmd.AddCommand(configExportCmd);
        var configImportPathOpt = new Option<FileInfo>("--path", "Input file path") { IsRequired = true };
        var configImportCmd = new Command("import", "Import and validate configuration from file") { configImportPathOpt };
        configImportCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="path">Path.</param>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (FileInfo path, bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<ConfigCommand>();
                var exitCode = await cmd.ImportAsync(path.FullName, json, verbose);
                Environment.Exit(exitCode);
            },
            configImportPathOpt,
            jsonOpt,
            verboseOpt);
        configCmd.AddCommand(configImportCmd);
        var configSetModeStepArg = new Argument<string>("step-id", "Step ID");
        var configSetModeModeArg = new Argument<string>("mode", "Execution mode: deterministic | agentic");
        var configSetModeCmd = new Command("set-mode", "Set execution mode for a step (hot-swap)") { configSetModeStepArg, configSetModeModeArg };
        configSetModeCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="stepId">Step id.</param>
            /// <param name="modeStr">Mode str.</param>
            async (string stepId, string modeStr) =>
            {
                var mode = modeStr.Trim().ToLowerInvariant() switch
                {
                    "deterministic" => Ashlar.Core.Application.Execution.Models.ExecutionMode.Deterministic,
                    "agentic" => Ashlar.Core.Application.Execution.Models.ExecutionMode.Agentic,
                    _ => throw new ArgumentException($"Mode must be 'deterministic' or 'agentic', got: {modeStr}")
                };
                var cmd = ServiceProvider.GetRequiredService<ConfigCommand>();
                var exitCode = await cmd.SetModeAsync(stepId, mode);
                Environment.Exit(exitCode);
            },
            configSetModeStepArg,
            configSetModeModeArg);
        configCmd.AddCommand(configSetModeCmd);
        configCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<ConfigCommand>();
                var exitCode = await cmd.ExecuteAsync(json, verbose);
                Environment.Exit(exitCode);
            },
            jsonOpt,
            verboseOpt);

        var backgroundAgentCmd = BuildBackgroundAgentCommand(jsonOpt);


        var trustCmd = BuildTrustCommand(jsonOpt);


        // ashlar test - Multi-platform test execution
        var testCmd = new Command("test", "Run tests across multiple platforms")
        {
            new Option<string[]>("--platforms", "Platforms to test (ubuntu, alpine, debian, android, ios, windows, macos)")
            {
                AllowMultipleArgumentsPerToken = true
            },
            new Option<string?>("--project", "Test project to run"),
            new Option<string?>("--filter", "Test filter (xUnit filter syntax)"),
            new Option<string>("--dotnet-version", () => "8.0", ".NET version to use"),
            new Option<string>("--execution-platform", () => "docker", "Execution platform (docker, rancher, kubernetes)"),
            new Option<DirectoryInfo>("--output-dir", () => new DirectoryInfo("test-results"), "Directory for test results")
        };
        var platformsOpt = testCmd.Options[0] as Option<string[]> ?? throw new InvalidOperationException();
        var projectOpt = testCmd.Options[1] as Option<string?> ?? throw new InvalidOperationException();
        var filterOpt = testCmd.Options[2] as Option<string?> ?? throw new InvalidOperationException();
        var dotnetVersionOpt = testCmd.Options[3] as Option<string> ?? throw new InvalidOperationException();
        var executionPlatformOpt = testCmd.Options[4] as Option<string> ?? throw new InvalidOperationException();
        var outputDirOpt = testCmd.Options[5] as Option<DirectoryInfo> ?? throw new InvalidOperationException();
        
        var coverageOpt = new Option<bool>("--coverage", () => false, "Enable code coverage collection");
        var stressOpt = new Option<bool>("--stress", () => false, "Run stress tests (multiple iterations)");
        var visualOpt = new Option<bool>("--visual", () => false, "Run visual validation tests (requires Ollama)");
        var ephemeralOpt = new Option<bool>("--ephemeral", () => false, "Run tests in ephemeral containers; no volume mounts, results discarded when container is removed");
        testCmd.AddOption(coverageOpt);
        testCmd.AddOption(stressOpt);
        testCmd.AddOption(visualOpt);
        testCmd.AddOption(ephemeralOpt);

        testCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var platforms = ctx.ParseResult.GetValueForOption(platformsOpt) ?? Array.Empty<string>();
            var project = ctx.ParseResult.GetValueForOption(projectOpt);
            var filter = ctx.ParseResult.GetValueForOption(filterOpt);
            var dotnetVersion = ctx.ParseResult.GetValueForOption(dotnetVersionOpt) ?? "8.0";
            var executionPlatform = ctx.ParseResult.GetValueForOption(executionPlatformOpt) ?? "docker";
            var outputDir = ctx.ParseResult.GetValueForOption(outputDirOpt) ?? new DirectoryInfo("test-results");
            var coverage = ctx.ParseResult.GetValueForOption(coverageOpt);
            var stress = ctx.ParseResult.GetValueForOption(stressOpt);
            var visual = ctx.ParseResult.GetValueForOption(visualOpt);
            var ephemeral = ctx.ParseResult.GetValueForOption(ephemeralOpt) || string.Equals(Environment.GetEnvironmentVariable("ASHLAR_TEST_EPHEMERAL"), "1", StringComparison.OrdinalIgnoreCase);
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            var verbose = ctx.ParseResult.GetValueForOption(verboseOpt);
            
            var exitCode = await MultiPlatformTestCommand.ExecuteAsync(
                platforms, project, filter, dotnetVersion, executionPlatform, outputDir, coverage, stress, visual, ephemeral, json, verbose, ServiceProvider);
            Environment.Exit(exitCode);
        });
        // ashlar test local - Run tests locally (replaces test-local.sh)
        var testLocalCmd = new Command("local", "Run tests locally using framework test runner")
        {
            new Option<string?>("--filter", "Filter tests by name or category")
        };
        testLocalCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="filter">Filter.</param>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (string? filter, bool json, bool verbose) =>
            {
                var testCommand = ServiceProvider.GetRequiredService<TestCommand>();
                var exitCode = await testCommand.ExecuteAsync(filter, json, verbose);
                Environment.Exit(exitCode);
            },
            testLocalCmd.Options[0] as Option<string?> ?? throw new InvalidOperationException(),
            jsonOpt,
            verboseOpt);
        testCmd.AddCommand(testLocalCmd);
        testCmd.AddCommand(TestPortableCommand.CreateCommand());
        testCmd.AddCommand(TestMultiEnvCommand.CreateCommand());
        testCmd.AddCommand(TestParallelCommand.CreateCommand());

        // ashlar orchestrate (resolves lazily - host built only when invoked)
        var orchestrateCmd = new Command("orchestrate", "Orchestrate agent execution for a request");
        orchestrateCmd.AddArgument(new Argument<string>("request", "The request to orchestrate"));

        var runtimeSpecOpt = new Option<FileInfo?>(
            name: "--runtime-spec",
            description: "Runtime spec JSON file (model routing per domain/agent)");
        var runtimeSpecJsonOpt = new Option<string?>(
            name: "--runtime-spec-json",
            description: "Runtime spec JSON string (model routing per domain/agent)");
        var preferModelOpt = new Option<string?>(
            name: "--prefer-model",
            description: "Override model preference: agentic|deterministic|auto");
        var providerOpt = new Option<string?>(
            name: "--provider",
            description: "Override model provider (openai/azure/ollama/offline/mock-json/...)");
        var barrierOpt = new Option<string?>(
            name: "--barrier",
            description: "Barrier level set at request boundary.");
        var jwtOpt = new Option<string?>(
            name: "--jwt",
            description: "Raw bearer JWT used for identity-bound barrier resolution.");
        var headerOpt = new Option<string[]>(
            name: "--header",
            description: "Additional request headers in 'name:value' form.")
        {
            AllowMultipleArgumentsPerToken = true
        };
        var preferredRegionOpt = new Option<string?>(
            name: "--preferred-region",
            description: "Preferred routing region (soft affinity).");

        var orchestrateEphemeralOpt = new Option<bool>("--ephemeral", () => false, "Use ephemeral Ollama container; discarded when command exits");
        orchestrateCmd.AddOption(runtimeSpecOpt);
        orchestrateCmd.AddOption(runtimeSpecJsonOpt);
        orchestrateCmd.AddOption(preferModelOpt);
        orchestrateCmd.AddOption(providerOpt);
        orchestrateCmd.AddOption(barrierOpt);
        orchestrateCmd.AddOption(jwtOpt);
        orchestrateCmd.AddOption(headerOpt);
        orchestrateCmd.AddOption(preferredRegionOpt);
        orchestrateCmd.AddOption(orchestrateEphemeralOpt);

        orchestrateCmd.SetHandler(async context =>
        {
            var request = context.ParseResult.GetValueForArgument(
                orchestrateCmd.Arguments[0] as Argument<string> ?? throw new InvalidOperationException());
            var runtimeSpec = context.ParseResult.GetValueForOption(runtimeSpecOpt);
            var runtimeSpecJson = context.ParseResult.GetValueForOption(runtimeSpecJsonOpt);
            var preferModel = context.ParseResult.GetValueForOption(preferModelOpt);
            var provider = context.ParseResult.GetValueForOption(providerOpt);
            var barrier = context.ParseResult.GetValueForOption(barrierOpt);
            var jwt = context.ParseResult.GetValueForOption(jwtOpt);
            var rawHeaders = context.ParseResult.GetValueForOption(headerOpt) ?? Array.Empty<string>();
            var preferredRegion = context.ParseResult.GetValueForOption(preferredRegionOpt);
            var ephemeral = context.ParseResult.GetValueForOption(orchestrateEphemeralOpt);
            var json = context.ParseResult.GetValueForOption(jsonOpt);
            var verbose = context.ParseResult.GetValueForOption(verboseOpt);

            if (ephemeral || string.Equals(Environment.GetEnvironmentVariable("ASHLAR_EPHEMERAL"), "1", StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("ASHLAR_EPHEMERAL_MODELS", "1");

            using var scope = ServiceProvider.CreateScope();
            var orchestrateCommand = scope.ServiceProvider.GetRequiredService<OrchestrateCommand>();
            var headers = ParseHeaders(rawHeaders);
            var exitCode = await orchestrateCommand.ExecuteAsync(
                request,
                runtimeSpec?.FullName,
                runtimeSpecJson,
                preferModel,
                provider,
                barrier,
                preferredRegion,
                json,
                verbose,
                jwt,
                headers);
            Environment.Exit(exitCode);
        });
        root.AddCommand(orchestrateCmd);

        // ashlar pipeline
        var pipelineCmd = new Command("pipeline", "Validate and run pipeline templates.");

        var pipelineValidateCmd = new Command("validate", "Validate a pipeline template file.");
        var pipelineValidateTemplateOpt = new Option<FileInfo>("--template", "Pipeline template JSON file") { IsRequired = true };
        pipelineValidateCmd.AddOption(pipelineValidateTemplateOpt);
        pipelineValidateCmd.SetHandler(
            (FileInfo template, bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<PipelineCommand>();
                var exitCode = cmd.Validate(template.FullName, json, verbose);
                Environment.Exit(exitCode);
            },
            pipelineValidateTemplateOpt,
            jsonOpt,
            verboseOpt);
        pipelineCmd.AddCommand(pipelineValidateCmd);

        var pipelineRunCmd = new Command("run", "Run a pipeline template file.");
        var pipelineRunTemplateOpt = new Option<FileInfo>("--template", "Pipeline template JSON file") { IsRequired = true };
        var pipelineRunIdOpt = new Option<string?>("--run-id", "Optional explicit run id");
        var pipelineResumeRunIdOpt = new Option<string?>("--resume-run-id", "Optional prior run id to resume from");
        var pipelineResumeFailedOpt = new Option<bool>("--resume-failed-stages", () => false, "Resume only failed stages from prior run");
        var pipelineInputOpt = new Option<string[]>("--input", "Key/value input in key=value format")
        {
            AllowMultipleArgumentsPerToken = true
        };
        pipelineRunCmd.AddOption(pipelineRunTemplateOpt);
        pipelineRunCmd.AddOption(pipelineRunIdOpt);
        pipelineRunCmd.AddOption(pipelineResumeRunIdOpt);
        pipelineRunCmd.AddOption(pipelineResumeFailedOpt);
        pipelineRunCmd.AddOption(pipelineInputOpt);
        pipelineRunCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="template">Template.</param>
            /// <param name="runId">Run id.</param>
            /// <param name="resumeRunId">Resume run id.</param>
            /// <param name="resumeFailedStages">Resume failed stages.</param>
            /// <param name="rawInputs">Raw inputs.</param>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (FileInfo template, string? runId, string? resumeRunId, bool resumeFailedStages, string[] rawInputs, bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<PipelineCommand>();
                var inputs = ParsePipelineInputs(rawInputs);
                var exitCode = await cmd.RunAsync(
                    templatePath: template.FullName,
                    json: json,
                    verbose: verbose,
                    runId: runId,
                    resumeRunId: resumeRunId,
                    resumeFailedStages: resumeFailedStages,
                    inputs: inputs,
                    cancellationToken: CancellationToken.None);
                Environment.Exit(exitCode);
            },
            pipelineRunTemplateOpt,
            pipelineRunIdOpt,
            pipelineResumeRunIdOpt,
            pipelineResumeFailedOpt,
            pipelineInputOpt,
            jsonOpt,
            verboseOpt);
        pipelineCmd.AddCommand(pipelineRunCmd);

        var pipelineDiagnosticsCmd = new Command("diagnostics", "Show resolved pipeline runtime configuration.");
        pipelineDiagnosticsCmd.SetHandler(
            (bool json) =>
            {
                var cmd = ServiceProvider.GetRequiredService<PipelineCommand>();
                var exitCode = cmd.Diagnostics(json);
                Environment.Exit(exitCode);
            },
            jsonOpt);
        pipelineCmd.AddCommand(pipelineDiagnosticsCmd);
        root.AddCommand(pipelineCmd);

        // ashlar escalate (resolves lazily)
        var escalateCmd = new Command("escalate", "Manage escalations and conflicts");
        
        // ashlar escalate list
        var escalateListCmd = new Command("list", "List all pending escalations");
        escalateListCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<EscalateCommand>();
                var exitCode = await cmd.ListAsync(json, verbose);
                Environment.Exit(exitCode);
            },
            jsonOpt,
            verboseOpt);
        escalateCmd.AddCommand(escalateListCmd);

        // ashlar escalate show
        var escalateShowCmd = new Command("show", "Show details for a specific escalation");
        escalateShowCmd.AddArgument(new Argument<string>("id", "Escalation ID"));
        escalateShowCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="id">Id.</param>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (string id, bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<EscalateCommand>();
                var exitCode = await cmd.ShowAsync(id, json, verbose);
                Environment.Exit(exitCode);
            },
            escalateShowCmd.Arguments[0] as Argument<string> ?? throw new InvalidOperationException(),
            jsonOpt,
            verboseOpt);
        escalateCmd.AddCommand(escalateShowCmd);

        // ashlar escalate resolve
        var escalateResolveCmd = new Command("resolve", "Resolve an escalation");
        escalateResolveCmd.AddArgument(new Argument<string>("id", "Escalation ID"));
        escalateResolveCmd.AddOption(new Option<string?>("--resolution", "Resolution description"));
        escalateResolveCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="id">Id.</param>
            /// <param name="resolution">Resolution.</param>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (string id, string? resolution, bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<EscalateCommand>();
                var exitCode = await cmd.ResolveAsync(id, resolution, json, verbose);
                Environment.Exit(exitCode);
            },
            escalateResolveCmd.Arguments[0] as Argument<string> ?? throw new InvalidOperationException(),
            escalateResolveCmd.Options[0] as Option<string?> ?? throw new InvalidOperationException(),
            jsonOpt,
            verboseOpt);
        escalateCmd.AddCommand(escalateResolveCmd);

        // ashlar escalate dismiss
        var escalateDismissCmd = new Command("dismiss", "Dismiss an escalation");
        escalateDismissCmd.AddArgument(new Argument<string>("id", "Escalation ID"));
        escalateDismissCmd.AddOption(new Option<string?>("--reason", "Dismissal reason"));
        escalateDismissCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="id">Id.</param>
            /// <param name="reason">Reason.</param>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (string id, string? reason, bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<EscalateCommand>();
                var exitCode = await cmd.DismissAsync(id, reason, json, verbose);
                Environment.Exit(exitCode);
            },
            escalateDismissCmd.Arguments[0] as Argument<string> ?? throw new InvalidOperationException(),
            escalateDismissCmd.Options[0] as Option<string?> ?? throw new InvalidOperationException(),
            jsonOpt,
            verboseOpt);
        escalateCmd.AddCommand(escalateDismissCmd);

        // ashlar escalate list-by-severity
        var escalateListBySeverityCmd = new Command("list-by-severity", "List escalations filtered by severity");
        escalateListBySeverityCmd.AddArgument(new Argument<string>("severity", "Severity level (Low, Medium, High, Critical)"));
        escalateListBySeverityCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="severity">Severity.</param>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (string severity, bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<EscalateCommand>();
                var exitCode = await cmd.ListBySeverityAsync(severity, json, verbose);
                Environment.Exit(exitCode);
            },
            escalateListBySeverityCmd.Arguments[0] as Argument<string> ?? throw new InvalidOperationException(),
            jsonOpt,
            verboseOpt);
        escalateCmd.AddCommand(escalateListBySeverityCmd);

        var metricsCmd = BuildMetricsCommand(jsonOpt, verboseOpt);


        // ashlar docker (generic build/run/clean for multi-platform testing)
        var dockerCmd = new DockerCommand();
        root.AddCommand(dockerCmd);

        // ashlar maintenance clean
        var maintenanceCmd = new Command("maintenance", "Artifact cleanup and maintenance");
        var maintenanceCleanCmd = new Command("clean", "Clean disk artifacts (test-artifacts, incomplete-blobs)");
        var strategyOpt = new Option<string?>("--strategy", "Strategy ID (test-artifacts, incomplete-blobs); omit for all");
        var repoOpt = new Option<string?>("--repo", "Repository root for context");
        maintenanceCleanCmd.AddOption(strategyOpt);
        maintenanceCleanCmd.AddOption(repoOpt);
        maintenanceCleanCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="strategy">Strategy.</param>
            /// <param name="repo">Repo.</param>
            /// <param name="json">Json.</param>
            async (string? strategy, string? repo, bool json) =>
            {
                var cmd = ServiceProvider.GetRequiredService<MaintenanceCommand>();
                var exitCode = await cmd.ExecuteAsync(strategy, repo, json);
                Environment.Exit(exitCode);
            },
            strategyOpt,
            repoOpt,
            jsonOpt);
        maintenanceCmd.AddCommand(maintenanceCleanCmd);
        root.AddCommand(maintenanceCmd);

        root.AddCommand(new CiCommand());
        root.AddCommand(new ReleaseCommand());
        root.AddCommand(analyzeCmd);
        root.AddCommand(validateCmd);
        root.AddCommand(agentCmd);
        root.AddCommand(configCmd);
        root.AddCommand(new DogfoodCommand());
        root.AddCommand(new BootstrapCommand());
        root.AddCommand(new DoctorCommand());
        root.AddCommand(new NewCommand());
        root.AddCommand(new RuntimeCommand());
        root.AddCommand(new WorkflowCommand(() => ServiceProvider.CreateScope()));
        root.AddCommand(new RuntimeStudioCommand());
        root.AddCommand(new ChatCommand(() => ServiceProvider.GetRequiredService<OrchestrateCommand>()));
        root.AddCommand(new SelfExtendCommand(
            () => ServiceProvider.GetRequiredService<Ashlar.BackgroundAgents.HostRunners.SelfExtendRunnerAdapter>()));
        root.AddCommand(new ObserveCommand());
        root.AddCommand(new AdaptCommand());
        root.AddCommand(new ImproveCommand());
        root.AddCommand(new IngestFailuresCommand());
        root.AddCommand(new SelfContextCommand());
        root.AddCommand(new ChangelogCommand());
        root.AddCommand(new InitCommand());
        root.AddCommand(new VerifyCommand());
        root.AddCommand(new GatesCommand());
        root.AddCommand(new KeysCommand());
        // `ashlar ledger reanchor` is the command the kernel's two lossy ledger refusals name.
        // Without it those refusals could only name a C# method, and a refusal whose fix cannot be
        // typed is the defect the whole ledger message rewrite exists to remove.
        root.AddCommand(new LedgerCommand());
        root.AddCommand(new PolicyCommand());
        root.AddCommand(new PkgCommand());
        root.AddCommand(new ExportCommand());
        root.AddCommand(new RunCommand(() => ServiceProvider.GetRequiredService<OrchestrateCommand>()));
        root.AddCommand(new RollbackCommand());
        root.AddCommand(new ComposeCommand());
        var meshCmd = new MeshCommand();
        root.AddCommand(meshCmd);
        root.AddCommand(backgroundAgentCmd);
        root.AddCommand(trustCmd);
        root.AddCommand(testCmd);
        root.AddCommand(escalateCmd);
        root.AddCommand(metricsCmd);

        return root;
    }
}
