using FluentAssertions;
using Ashlar.BackgroundAgents.DataSensitivity;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.DataSensitivity;

/// <summary>
/// Tests for DataSensitivityMarker.
/// </summary>
public class DataSensitivityMarkerTests
{
    // INVERTED. This used to be GetSensitivityLevel_WithUnmarkedData_ReturnsPublic and pinned the
    // fail-open default as the contract: data nobody classified was reported as Public.
    [Fact]
    public void GetSensitivityLevel_WithUnmarkedData_ReturnsTheMostRestrictiveLevel()
    {
        // Arrange
        var registry = new DataSensitivityRegistry();
        var marker = new DataSensitivityMarker(registry);
        var data = new { Value = "test" };

        // Act
        var level = marker.GetSensitivityLevel(data);

        // Assert
        level.Should().Be(DataSensitivityLevels.TopSecret);
        marker.GetSensitivityLevel(null!).Should().Be(DataSensitivityLevels.TopSecret, "null data is unmarked too");
    }

    // "Most restrictive" is the registry's highest level, not a fixed name: a custom level
    // registered above TopSecret is where unmarked data lands.
    [Fact]
    public void GetSensitivityLevel_WithUnmarkedData_FollowsACustomLevelAboveTopSecret()
    {
        var registry = new DataSensitivityRegistry();
        var codeword = new ConfigurableSensitivityLevel("Codeword", "Codeword", 10, false, false, true, false, "above TopSecret");
        registry.Register(codeword);
        var marker = new DataSensitivityMarker(registry);

        marker.GetSensitivityLevel(new { Value = "test" }).Should().Be(codeword);
        marker.CanAccess(DataSensitivityLevels.TopSecret, new { Value = "test" }).Should().BeFalse();
    }

    [Fact]
    public void MarkSensitivity_WithLevel_MarksData()
    {
        // Arrange
        var registry = new DataSensitivityRegistry();
        var marker = new DataSensitivityMarker(registry);
        var data = new { Value = "test" };

        // Act
        marker.MarkSensitivity(data, DataSensitivityLevels.Confidential);
        var level = marker.GetSensitivityLevel(data);

        // Assert
        level.Should().Be(DataSensitivityLevels.Confidential);
    }

    [Fact]
    public void MarkSensitivity_WithLevelName_MarksData()
    {
        // Arrange
        var registry = new DataSensitivityRegistry();
        var marker = new DataSensitivityMarker(registry);
        var data = new { Value = "test" };

        // Act
        marker.MarkSensitivity(data, "Confidential");
        var level = marker.GetSensitivityLevel(data);

        // Assert
        level.Should().Be(DataSensitivityLevels.Confidential);
    }

    [Fact]
    public void MarkSensitivity_WithInvalidLevelName_ThrowsException()
    {
        // Arrange
        var registry = new DataSensitivityRegistry();
        var marker = new DataSensitivityMarker(registry);
        var data = new { Value = "test" };

        // Act & Assert
        var act = () => marker.MarkSensitivity(data, "InvalidLevel");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown sensitivity level*");
    }

    [Fact]
    public void CanAccess_WithAccessibleData_ReturnsTrue()
    {
        // Arrange
        var registry = new DataSensitivityRegistry();
        var marker = new DataSensitivityMarker(registry);
        var data = new { Value = "test" };
        marker.MarkSensitivity(data, DataSensitivityLevels.Internal);

        // Act
        var canAccess = marker.CanAccess(DataSensitivityLevels.Confidential, data);

        // Assert
        canAccess.Should().BeTrue();
    }

    [Fact]
    public void CanAccess_WithInaccessibleData_ReturnsFalse()
    {
        // Arrange
        var registry = new DataSensitivityRegistry();
        var marker = new DataSensitivityMarker(registry);
        var data = new { Value = "test" };
        marker.MarkSensitivity(data, DataSensitivityLevels.Secret);

        // Act
        var canAccess = marker.CanAccess(DataSensitivityLevels.Internal, data);

        // Assert
        canAccess.Should().BeFalse();
    }

    // INVERTED. This used to be CanAccess_WithUnmarkedData_ReturnsTrue: an Internal agent could read
    // anything nobody had marked, because unmarked meant Public.
    [Fact]
    public void CanAccess_WithUnmarkedData_IsDeniedBelowTheTopLevel()
    {
        // Arrange
        var registry = new DataSensitivityRegistry();
        var marker = new DataSensitivityMarker(registry);
        var data = new { Value = "test" };

        // Act
        var canAccess = marker.CanAccess(DataSensitivityLevels.Internal, data);

        // Assert
        canAccess.Should().BeFalse("unmarked data is the most restrictive level, not Public");

        // POSITIVE CONTROL: the top level still reads it, so the denial is the ordering and not a
        // marker that denies everything.
        marker.CanAccess(DataSensitivityLevels.TopSecret, data).Should().BeTrue();
    }
}
