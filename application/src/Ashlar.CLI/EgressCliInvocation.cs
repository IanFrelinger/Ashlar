using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.CLI;

/// <summary>One CLI invocation retains refusals even when a command converts one into a result.</summary>
internal sealed class EgressCliInvocation : IEgressDecisionSink, IDisposable
{
    private static readonly AsyncLocal<EgressCliInvocation?> Current = new();
    private readonly EgressCliInvocation? _parent;
    private readonly IDisposable _subscription;
    private EgressRefusedException? _refusal;
    private int _reported;

    private EgressCliInvocation()
    {
        _parent = Current.Value;
        Current.Value = this;
        _subscription = EgressDecisionLog.Subscribe(this);
    }

    internal static async Task<int> InvokeAsync(RootCommand root, string[] args)
    {
        using var invocation = new EgressCliInvocation();
        var parser = new CommandLineBuilder(root).UseDefaults().UseExceptionHandler((error, context) =>
        {
            var refusal = EgressRefusal.Find(error) ?? Volatile.Read(ref invocation._refusal);
            if (refusal is not null)
            {
                Interlocked.CompareExchange(ref invocation._refusal, refusal, null);
                context.ExitCode = 77;
            }
            else
            {
                context.Console.Error.Write(error.ToString());
                context.ExitCode = 1;
            }
        }).Build();
        return invocation.Resolve(await parser.InvokeAsync(args).ConfigureAwait(false));
    }

    // Some existing command handlers terminate the process rather than returning to the parser.
    // They must resolve the invocation's refusal before that exit, too.
    internal static void Exit(int exitCode) => Environment.Exit(Current.Value?.Resolve(exitCode) ?? exitCode);

    private int Resolve(int exitCode)
    {
        var refusal = Volatile.Read(ref _refusal);
        if (refusal is null) return exitCode;
        if (Interlocked.Exchange(ref _reported, 1) == 0) Console.Error.WriteLine(refusal.Message);
        return 77;
    }

    public void Record(EgressDecision decision)
    {
        // Do not attribute another invocation's work (or a timer with no invocation) to this command.
        if (ReferenceEquals(Current.Value, this) && decision.Refused)
            Interlocked.CompareExchange(ref _refusal, new EgressRefusedException(decision), null);
    }

    public void Dispose()
    {
        _subscription.Dispose();
        Current.Value = _parent;
    }
}
