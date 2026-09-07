using FluentAssertions;
using Ashlar.Manifest;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>
/// The manifest-layer instance of certification bypass #2: excluding a directory NAMED
/// <c>obj</c> or <c>bin</c> at any depth, when the SDK excludes only the two a project owns.
///
/// <para><c>$(BaseOutputPath)</c> and <c>$(BaseIntermediateOutputPath)</c> are <c>bin/</c> and
/// <c>obj/</c> directly under the project that owns them; that pair is the whole of the default
/// compile glob's exclusion. A folder called <c>obj</c> nested somewhere else is ordinary project
/// source. Matching the name at any depth therefore made real, compiled source invisible to this
/// inventory with no csproj edit to notice — and the direction that bites is not the composition
/// course (an unresolved brick refuses, which is safe) but <c>ashlar export</c>, which stages
/// declared-brick carrier directories FROM this inventory: source it cannot see is source that
/// silently does not travel in the bundle.</para>
/// </summary>
public sealed class BrickSourceResolverNestedBuildOutputTests : IDisposable
{
    private readonly string _dir;

    public BrickSourceResolverNestedBuildOutputTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "resolver-nested-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Theory]
    [InlineData("Widget/obj/Widget.cs")]
    [InlineData("Widget/bin/Widget.cs")]
    [InlineData("Sub/obj/Nested/Widget.cs")]
    public void Source_under_a_nested_directory_merely_NAMED_like_build_output_is_in_the_inventory(string path)
    {
        // No csproj anywhere here, so none of these is a project's own output — they are folders a
        // person happened to name obj/ and bin/, and the compiler compiles what is in them.
        Write(path, "public sealed class Widget { }");

        BrickSourceResolver.Scan(_dir).SourceFiles
            .Select(f => Path.GetRelativePath(_dir, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Should().Contain(path,
                "the SDK excludes a project's OWN bin/ and obj/, not every directory with those names");
    }

    [Fact]
    public void A_brick_whose_source_sits_under_a_nested_obj_still_resolves_and_can_be_staged()
    {
        // The user-visible consequence: `ashlar export` stages declared-brick carrier directories
        // out of this inventory, so a brick invisible here does not travel and the exported bundle
        // is quietly incomplete.
        Write("Widget/obj/Widget.cs", "public sealed class Widget { }");

        var inventory = BrickSourceResolver.Scan(_dir);

        BrickSourceResolver.Resolve(inventory, "widget").Should().NotBeEmpty();
    }

    [Fact]
    public void A_projects_own_build_output_is_still_excluded()
    {
        // The fix must not swing the other way. Where a csproj sits, its bin/ and obj/ are output,
        // and a stale copy there must never resolve a brick whose source was deleted.
        Write("MyBrick/MyBrick.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("MyBrick/obj/Debug/Widget.cs", "public sealed class Widget { }");
        Write("MyBrick/bin/Debug/Widget.cs", "public sealed class Widget { }");

        var inventory = BrickSourceResolver.Scan(_dir);

        inventory.SourceFiles.Should().BeEmpty();
        BrickSourceResolver.Resolve(inventory, "widget").Should().BeEmpty();
    }

    [Fact]
    public void The_scan_roots_own_build_output_is_still_excluded()
    {
        // The application root's own bin/ and obj/ are output whether or not a csproj sits beside
        // them — the CLI writes there.
        Write("obj/Debug/Widget.cs", "public sealed class Widget { }");
        Write("bin/Debug/Widget.cs", "public sealed class Widget { }");

        BrickSourceResolver.Scan(_dir).SourceFiles.Should().BeEmpty();
    }
}
