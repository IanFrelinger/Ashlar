using System.CommandLine;
using System.CommandLine.Parsing;
using System.CommandLine.Invocation;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Ashlar.CLI.Formatting;
using Ashlar.Core.Application.Common.Models;

namespace Ashlar.CLI.Commands;

/// <summary>Command execution support.</summary>
internal static class CommandExecutionSupport
{
    private static readonly ConditionalWeakTable<Command, object> JsonCommands = new();

    /// <summary>Opt this handler (not its descendants) into the global JSON contract.</summary>
    internal static Command WithJsonOutput(this Command command)
    {
        JsonCommands.GetValue(command, _ => new object());
        return command;
    }

    internal static bool SupportsJson(Command command) => JsonCommands.TryGetValue(command, out _);

    /// <summary>Read parsed values, including response files, explicit booleans and local aliases.</summary>
    internal static bool WantsJson(ParseResult parseResult)
    {
        for (var result = parseResult.CommandResult; result is not null; result = result.Parent as CommandResult)
        {
            foreach (var option in result.Command.Options.OfType<Option<bool>>().Where(o => o.HasAlias("--format-json")))
            {
                if (parseResult.GetValueForOption(option)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Wrap handlers after registration so unknown/new commands refuse JSON before side effects.
    /// Help and parse errors remain System.CommandLine's responsibility and do not execute handlers.
    /// </summary>
    internal static void ApplyJsonOutputContract(Command command)
    {
        if (command.Handler is { } handler)
        {
            command.Handler = new JsonOutputHandler(command, handler);
            if (!SupportsJson(command))
                command.Description += " (--format-json is unsupported and refuses before execution.)";
        }
        foreach (var child in command.Subcommands)
            ApplyJsonOutputContract(child);
    }

    private sealed class JsonOutputHandler(Command command, ICommandHandler handler) : ICommandHandler
    {
        private bool Refused(InvocationContext context)
        {
            // The host is lazy: set logging mode from the parser before any handler resolves it.
            Program.JsonOutputMode = WantsJson(context.ParseResult);
            if (!Program.JsonOutputMode || SupportsJson(command)) return false;
            context.Console.Error.Write($"`{command.Name}` has no JSON rendering, so --format-json cannot be honoured here. "
                + "Refusing rather than printing prose a JSON reader cannot parse." + Environment.NewLine);
            context.ExitCode = 1;
            return true;
        }

        public int Invoke(InvocationContext context) => Refused(context) ? context.ExitCode : handler.Invoke(context);

        public Task<int> InvokeAsync(InvocationContext context) => Refused(context)
            ? Task.FromResult(context.ExitCode)
            : handler.InvokeAsync(context);
    }

    /// <summary>
    /// The refusal a command with no JSON rendering returns when <c>--format-json</c> was passed, and
    /// null when it was not — so a handler can read <c>RefuseJsonFormat(...) ?? RealWork(...)</c>.
    ///
    /// <para><c>--format-json</c> is a GLOBAL option on the root, so it parses on every command in the
    /// tree, including the ones that only ever print prose. Accepting it and printing prose anyway
    /// hands a caller who is piping into a parser exit 0 over unparseable text: a green light above a
    /// broken pipeline, which is worse than no output at all. Refusing is the honest answer until the
    /// command grows a JSON rendering of its own — and it does not prejudge what that rendering
    /// should be.</para>
    /// </summary>
    internal static int? RefuseJsonFormat(ParseResult parseResult, string command, TextWriter stderr)
    {
        if (!WantsJson(parseResult))
        {
            return null;
        }

        stderr.WriteLine($"`{command}` has no JSON rendering, so --format-json cannot be honoured here. "
            + "Refusing rather than printing prose a JSON reader cannot parse.");
        // Usage, not a failed verification — 65 stays reserved for a course that did not pass.
        return 1;
    }

    internal static Progress<ProgressReport>? CreateProgressReporter(
        bool verbose,
        bool json,
        ILogger logger,
        IConsoleRenderer renderer)
    {
        if (!verbose && json)
            return null;

        return new Progress<ProgressReport>(report =>
        {
            if (json)
            {
                logger.LogInformation(
                    "Progress: {Percentage}% - {Message}",
                    report.Percentage,
                    report.Message);
                return;
            }

            renderer.RenderProgress(report);
        });
    }

    internal static int RenderDomainFailure(
        ILogger logger,
        IConsoleRenderer renderer,
        Exception exception,
        string logMessage,
        int exitCode)
    {
        logger.LogError(exception, logMessage);
        var code = exception switch
        {
            Ashlar.Core.Domain.Exceptions.AnalysisException analysis => analysis.ErrorCode,
            Ashlar.Core.Domain.Exceptions.ValidationException validation => validation.ErrorCode,
            _ => null
        };
        var suggestion = exception switch
        {
            Ashlar.Core.Domain.Exceptions.AnalysisException analysis => analysis.Suggestion,
            Ashlar.Core.Domain.Exceptions.ValidationException validation => validation.Suggestion,
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(code))
            renderer.RenderErrorWithCode(exception.Message, code!, suggestion);
        else
            renderer.RenderError(exception.Message);

        return exitCode;
    }
}
