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
                // Every key ExecuteAsync writes must be declared here: the certification gate's
                // analyzer leg (ASHLAR0002) refuses an undeclared output, because consumers read
                // this interface to learn what the brick produces.
                new BrickOutputDefinition("implementation", "string", "Implementation type the brick ran under")
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
