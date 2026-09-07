using Ashlar.BackgroundAgents.Campaign;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Campaign;

/// <summary>Regression specialist checks the gate surface and invoked command.</summary>
public sealed class RegressionLaneRunnerTests : IDisposable
{
    private readonly string _root;

    public RegressionLaneRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ashlar-regression-" + Guid.NewGuid().ToString("N"));
        Write(_root, "scripts/run-cert-gate.sh", "#!/bin/bash\n");
        Write(_root, "src/Ashlar.Tests.Infrastructure/Tests/Dogfood/DogfoodBlock1Tests.cs", "class DogfoodBlock1Tests {}\n");
        Write(_root, "src/Ashlar.Tests.BackgroundAgents/Campaign/CampaignAgentSetConventionTests.cs", "class CampaignAgentSetConventionTests {}\n");
        Write(_root, "Makefile", "dogfood-campaign:\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Surface_only_mode_passes_when_files_exist()
    {
        var report = await new RegressionLaneRunner().RunAsync(Context(skipProcess: true));
        report.Verdict.Should().Be(CampaignVerdictKind.Pass);
    }

    [Fact]
    public async Task Missing_cert_gate_fails()
    {
        File.Delete(Path.Combine(_root, "scripts", "run-cert-gate.sh"));
        var report = await new RegressionLaneRunner().RunAsync(Context(skipProcess: true));
        report.Verdict.Should().Be(CampaignVerdictKind.Fail);
        report.Findings.Should().Contain(f => f.Code == "missing-cert-gate");
    }

    [Fact]
    public async Task Fast_mode_invokes_the_convention_slice()
    {
        var invoker = new RecordingInvoker(0);
        var report = await new RegressionLaneRunner(invoker).RunAsync(Context(skipProcess: false));
        report.Verdict.Should().Be(CampaignVerdictKind.Pass);
        invoker.FileName.Should().Be("dotnet");
        invoker.Arguments.Should().Contain("FullyQualifiedName~CampaignAgentSetConventionTests");
    }

    [Fact]
    public async Task Full_mode_invokes_cert_gate_fast()
    {
        var invoker = new RecordingInvoker(0);
        var report = await new RegressionLaneRunner(invoker).RunAsync(Context(skipProcess: false) with { Full = true });
        report.Verdict.Should().Be(CampaignVerdictKind.Pass);
        invoker.FileName.Should().Be("bash");
        invoker.Arguments.Should().Contain("scripts/run-cert-gate.sh");
        invoker.Arguments.Should().Contain("--fast");
    }

    [Fact]
    public async Task Nonzero_exit_is_a_blocker()
    {
        var invoker = new RecordingInvoker(2, stderr: "boom");
        var report = await new RegressionLaneRunner(invoker).RunAsync(Context(skipProcess: false));
        report.Verdict.Should().Be(CampaignVerdictKind.Fail);
        report.Findings.Should().Contain(f => f.Code == "regression-command-failed");
    }

    private CampaignRunContext Context(bool skipProcess) => new(
        _root,
        "dev-tool-dogfood",
        "regression",
        "tester",
        Full: false,
        SkipProcessLanes: skipProcess);

    private static void Write(string root, string relative, string contents)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private sealed class RecordingInvoker : ICampaignProcessInvoker
    {
        private readonly CampaignProcessResult _result;
        public string? FileName { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }

        public RecordingInvoker(int exit, string stderr = "")
            => _result = new CampaignProcessResult(exit, "ok", stderr);

        public Task<CampaignProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            FileName = fileName;
            Arguments = arguments;
            return Task.FromResult(_result);
        }
    }
}
