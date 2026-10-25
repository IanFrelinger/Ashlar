using FluentAssertions;
using Ashlar.Core.Domain.Bricks;
using Xunit;

namespace Ashlar.Tests.Domain.Tests.Bricks;

public sealed class BrickTypeRegistrationTests
{
    [Fact]
    public void Carries_the_registered_brick_type()
    {
        new BrickTypeRegistration(typeof(TestBrick)).BrickType.Should().Be(typeof(TestBrick));
    }

    [Fact]
    public void Refuses_a_null_type()
    {
        var act = () => new BrickTypeRegistration(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("brickType");
    }
}
