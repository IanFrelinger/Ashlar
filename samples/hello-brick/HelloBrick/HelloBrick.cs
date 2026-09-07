using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace HelloBrick;

/// <summary>
/// Example code-authored Ashlar brick: the smallest brick the certification gate admits.
/// </summary>
/// <remarks>
/// One source file, one package (Ashlar.Brick.Contracts), one witness beside it
/// (<c>hello-brick.witness.json</c>). The gate binds its signed content hash over exactly this text.
/// </remarks>
public sealed class HelloBrick : Brick
{
    public HelloBrick()
    {
        Id = "hello";
        Name = "Hello Brick";
        Version = "1.0.0";
        Icon = "🧱";
        Category = BrickCategory.Transform;
        Description = "A starter code-authored Ashlar brick.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("name", "string", "Name to greet", required: false, defaultValue: "world")
            ],
            Outputs =
            [
                new BrickOutputDefinition("message", "string", "Greeting text"),
                new BrickOutputDefinition("implementation", "string", "Implementation the brick ran as")
            ]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var name = input.Get<string>("name");
        var output = new BrickOutput
        {
            Summary = $"Generated greeting for {name}."
        };
        output.Set("message", $"Hello, {name}!");
        output.Set("implementation", implementation.ToString());
        return Task.FromResult(output);
    }
}
