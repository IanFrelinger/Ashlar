using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace __Namespace__;

/// <summary>
/// Example code-authored Ashlar brick generated from the code-brick template.
/// </summary>
public sealed class __BrickName__Brick : Brick
{
    public __BrickName__Brick()
    {
        Id = "__BrickId__";
        Name = "__DisplayName__";
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
                new BrickOutputDefinition("implementation", "string", "Execution implementation")
            ]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var name = input.Get<string>("name", "world");
        var output = new BrickOutput
        {
            Summary = $"Generated greeting for {name}."
        };
        output.Set("message", $"Hello, {name}!");
        output.Set("implementation", implementation.ToString());
        return Task.FromResult(output);
    }
}
