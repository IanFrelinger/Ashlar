using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Hosting;

public class AshlarSimpleCliTests
{
    [Fact]
    public void CreateSimpleHost_ResolvesExecutionContext()
    {
        using var host = AshlarSimpleCli.CreateSimpleHost();

        var context = host.Services.GetService<IExecutionContext>();

        Assert.NotNull(context);
    }

    [Fact]
    public void CreateSimpleHost_RunsCallerServiceConfiguration()
    {
        var marker = new Marker();

        using var host = AshlarSimpleCli.CreateSimpleHost(configureServices: services => services.AddSingleton(marker));

        Assert.Same(marker, host.Services.GetService<Marker>());
    }

    [Fact]
    public void CreateSimpleHost_RunsCallerLoggingConfiguration()
    {
        var invoked = false;

        using var host = AshlarSimpleCli.CreateSimpleHost(configureLogging: logging =>
        {
            invoked = true;
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        Assert.True(invoked);
        var logger = host.Services.GetRequiredService<ILogger<AshlarSimpleCliTests>>();
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public void CreateSimpleHostBuilder_ReturnsUnbuiltBuilderThatBuildsTheSameServices()
    {
        var builder = AshlarSimpleCli.CreateSimpleHostBuilder();

        using var host = builder.Build();

        Assert.NotNull(host.Services.GetService<IExecutionContext>());
    }

    [Fact]
    public async Task ExecuteAsync_WithConfigureInputs_PassesConfiguredInputAndDefaultsToDeterministic()
    {
        var brick = new RecordingBrick();

        var output = await brick.ExecuteAsync(input =>
        {
            input.Set("name", "ashlar");
            input.Set("count", 3);
        });

        Assert.Equal("ashlar", brick.LastInput!.Get<string>("name"));
        Assert.Equal(3, brick.LastInput.Get<int>("count"));
        Assert.Equal(ImplementationType.Deterministic, brick.LastImplementation);
        Assert.NotNull(brick.LastContext);
        Assert.Equal("echo:ashlar", output.Get<string>("echo"));
    }

    [Fact]
    public async Task ExecuteAsync_WithInput_PassesTheSameInputImplementationAndToken()
    {
        var brick = new RecordingBrick();
        var input = new BrickInput();
        input.Set("name", "given");
        using var cts = new CancellationTokenSource();

        var output = await brick.ExecuteAsync(input, ImplementationType.Agentic, cts.Token);

        Assert.Same(input, brick.LastInput);
        Assert.Equal(ImplementationType.Agentic, brick.LastImplementation);
        Assert.Equal(cts.Token, brick.LastToken);
        Assert.Equal("echo:given", output.Get<string>("echo"));
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesBrickExceptions()
    {
        var brick = new RecordingBrick { Throw = new InvalidOperationException("boom") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => brick.ExecuteAsync(_ => { }));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void GetOrDefault_ReturnsValue_WhenKeyExistsWithMatchingType()
    {
        var output = new BrickOutput();
        output.Set("count", 7);

        Assert.Equal(7, output.GetOrDefault("count", -1));
    }

    [Fact]
    public void GetOrDefault_ReturnsDefault_WhenKeyMissingOrTypeMismatched()
    {
        var output = new BrickOutput();
        output.Set("count", "not a number");

        Assert.Equal(-1, output.GetOrDefault("count", -1));
        Assert.Equal("fallback", output.GetOrDefault("missing", "fallback"));
        Assert.Equal(0, output.GetOrDefault<int>("missing"));
    }

    [Fact]
    public void WriteTo_SummaryOnly_ByDefault()
    {
        var output = new BrickOutput { Summary = "2 blockers" };
        output.Set("state", "open");
        using var writer = new StringWriter();

        output.WriteTo(writer);

        Assert.Equal("Summary: 2 blockers" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void WriteTo_IncludeAllOutputs_WritesEveryValueInOrdinalKeyOrder()
    {
        var output = new BrickOutput { Summary = "done" };
        output.Set("state", "open");
        output.Set("blockers", new[] { "draft", "1 CI check(s) failed" });
        output.Set("mergeable", false);
        using var writer = new StringWriter();

        output.WriteTo(writer, includeAllOutputs: true);

        var lines = writer.ToString().Split(Environment.NewLine);
        Assert.Equal(
            new[]
            {
                "Summary: done",
                "",
                "Outputs:",
                "  blockers: draft, 1 CI check(s) failed",
                "  mergeable: false",
                "  state: open",
                "",
            },
            lines);
    }

    [Fact]
    public void WriteTo_IncludeAllOutputs_SaysNoneWhenThereAreNoOutputs()
    {
        var output = new BrickOutput();
        using var writer = new StringWriter();

        output.WriteTo(writer, includeAllOutputs: true);

        Assert.Contains("  (none)", writer.ToString());
        Assert.DoesNotContain("Summary:", writer.ToString());
    }

    private sealed class Marker
    {
    }

    private sealed class RecordingBrick : Brick
    {
        public RecordingBrick()
        {
            Id = "test.recording";
            Name = "Recording";
            Version = "1.0.0";
            Icon = "R";
            Category = BrickCategory.Transform;
            Description = "Records what it was executed with.";
            Interface = new BrickInterface
            {
                Inputs =
                [
                    new BrickInputDefinition("name", "string", "Name to echo", required: false),
                    new BrickInputDefinition("count", "int", "Count", required: false),
                ],
                Outputs = [new BrickOutputDefinition("echo", "string", "Echoed name")],
            };
        }

        public Exception? Throw { get; init; }

        public BrickInput? LastInput { get; private set; }

        public ImplementationType? LastImplementation { get; private set; }

        public IExecutionContext? LastContext { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input,
            ImplementationType implementation,
            IExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            LastInput = input;
            LastImplementation = implementation;
            LastContext = context;
            LastToken = cancellationToken;
            if (Throw is not null)
            {
                throw Throw;
            }

            var output = new BrickOutput();
            output.Set("echo", "echo:" + input.Get<string>("name", "none"));
            return Task.FromResult(output);
        }
    }
}
