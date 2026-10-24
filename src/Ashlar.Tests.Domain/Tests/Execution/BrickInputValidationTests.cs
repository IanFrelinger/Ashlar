using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Xunit;

namespace Ashlar.Tests.Domain.Tests.Execution;

public class BrickInputValidationTests
{
    [Fact]
    public void ValidateRequiredInputs_AllPresent_DoesNotThrow()
    {
        var brickInterface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("name", "string", required: true),
                new BrickInputDefinition("count", "number", required: true)
            ]
        };

        var input = new BrickInput();
        input.Set("name", "test");
        input.Set("count", 42);

        var exception = Record.Exception(() => input.ValidateRequiredInputs(brickInterface));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateRequiredInputs_MissingRequired_ThrowsWithNames()
    {
        var brickInterface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("name", "string", required: true),
                new BrickInputDefinition("count", "number", required: true),
                new BrickInputDefinition("optional", "string", required: false)
            ]
        };

        var input = new BrickInput();
        input.Set("optional", "value");

        var exception = Assert.Throws<BrickInputValidationException>(
            () => input.ValidateRequiredInputs(brickInterface));

        Assert.Contains("name", exception.MissingInputs);
        Assert.Contains("count", exception.MissingInputs);
        Assert.Equal(2, exception.MissingInputs.Count);
        Assert.Contains("name", exception.Message);
        Assert.Contains("count", exception.Message);
    }

    [Fact]
    public void ValidateRequiredInputs_OnlyOptionalMissing_DoesNotThrow()
    {
        var brickInterface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("name", "string", required: true),
                new BrickInputDefinition("optional", "string", required: false)
            ]
        };

        var input = new BrickInput();
        input.Set("name", "test");

        var exception = Record.Exception(() => input.ValidateRequiredInputs(brickInterface));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateRequiredInputs_EmptyInterface_DoesNotThrow()
    {
        var brickInterface = new BrickInterface
        {
            Inputs = []
        };

        var input = new BrickInput();

        var exception = Record.Exception(() => input.ValidateRequiredInputs(brickInterface));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateRequiredInputs_NullInput_ThrowsArgumentNull()
    {
        var brickInterface = new BrickInterface();

        Assert.Throws<ArgumentNullException>(() => 
            ((BrickInput)null!).ValidateRequiredInputs(brickInterface));
    }

    [Fact]
    public void ValidateRequiredInputs_NullInterface_ThrowsArgumentNull()
    {
        var input = new BrickInput();

        Assert.Throws<ArgumentNullException>(() => 
            input.ValidateRequiredInputs(null!));
    }

    [Fact]
    public void TryValidateRequiredInputs_AllPresent_ReturnsTrue()
    {
        var brickInterface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("name", "string", required: true)
            ]
        };

        var input = new BrickInput();
        input.Set("name", "test");

        var result = input.TryValidateRequiredInputs(brickInterface, out var missingInputs);

        Assert.True(result);
        Assert.Empty(missingInputs);
    }

    [Fact]
    public void TryValidateRequiredInputs_MissingRequired_ReturnsFalseWithNames()
    {
        var brickInterface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("name", "string", required: true),
                new BrickInputDefinition("count", "number", required: true)
            ]
        };

        var input = new BrickInput();

        var result = input.TryValidateRequiredInputs(brickInterface, out var missingInputs);

        Assert.False(result);
        Assert.Equal(2, missingInputs.Count);
        Assert.Contains("name", missingInputs);
        Assert.Contains("count", missingInputs);
    }

    [Fact]
    public void ApplyDefaults_MissingOptionalWithDefault_AppliesDefault()
    {
        var brickInterface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("name", "string", required: false, defaultValue: "default-name"),
                new BrickInputDefinition("count", "number", required: false, defaultValue: 10)
            ]
        };

        var input = new BrickInput();
        input.ApplyDefaults(brickInterface);

        Assert.Equal("default-name", input.Get<string>("name"));
        Assert.Equal(10, input.Get<int>("count"));
    }

    [Fact]
    public void ApplyDefaults_PresentValue_DoesNotOverride()
    {
        var brickInterface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("name", "string", required: false, defaultValue: "default-name")
            ]
        };

        var input = new BrickInput();
        input.Set("name", "existing-name");
        input.ApplyDefaults(brickInterface);

        Assert.Equal("existing-name", input.Get<string>("name"));
    }

    [Fact]
    public void ApplyDefaults_RequiredInput_NotApplied()
    {
        var brickInterface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("name", "string", required: true, defaultValue: "default-name")
            ]
        };

        var input = new BrickInput();
        input.ApplyDefaults(brickInterface);

        Assert.False(input.TryGet<string>("name", out _));
    }

    [Fact]
    public void ApplyDefaults_OptionalWithNullDefault_DoesNotApply()
    {
        var brickInterface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("name", "string", required: false, defaultValue: null)
            ]
        };

        var input = new BrickInput();
        input.ApplyDefaults(brickInterface);

        Assert.False(input.TryGet<string>("name", out _));
    }

    [Fact]
    public void ApplyDefaults_NullInput_ThrowsArgumentNull()
    {
        var brickInterface = new BrickInterface();

        Assert.Throws<ArgumentNullException>(() => 
            ((BrickInput)null!).ApplyDefaults(brickInterface));
    }

    [Fact]
    public void ApplyDefaults_NullInterface_ThrowsArgumentNull()
    {
        var input = new BrickInput();

        Assert.Throws<ArgumentNullException>(() => 
            input.ApplyDefaults(null!));
    }

    [Fact]
    public void FullWorkflow_ValidateAndApplyDefaults_Works()
    {
        var brickInterface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("name", "string", required: true),
                new BrickInputDefinition("count", "number", required: false, defaultValue: 5),
                new BrickInputDefinition("verbose", "bool", required: false, defaultValue: false)
            ]
        };

        var input = new BrickInput();
        input.Set("name", "test");
        
        input.ApplyDefaults(brickInterface);
        
        var exception = Record.Exception(() => input.ValidateRequiredInputs(brickInterface));
        Assert.Null(exception);
        
        Assert.Equal("test", input.Get<string>("name"));
        Assert.Equal(5, input.Get<int>("count"));
        Assert.False(input.Get<bool>("verbose"));
    }
}
