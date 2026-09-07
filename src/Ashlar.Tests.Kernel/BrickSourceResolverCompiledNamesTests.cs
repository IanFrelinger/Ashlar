using FluentAssertions;
using Ashlar.Manifest;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>
/// The manifest-layer half of the same hole the certification loader carried: a list of file-name
/// suffixes standing in for "the compiler will not compile this".
///
/// <para><c>BrickSourceResolver</c> skipped <c>*.g.cs</c>, <c>*.generated.cs</c>,
/// <c>*.Designer.cs</c>, <c>*.AssemblyInfo.cs</c> and <c>*.AssemblyAttributes.cs</c>. The SDK
/// compiles every one of those names outside <c>obj/</c> and <c>bin/</c>. So the inventory that
/// <c>ashlar verify</c> reasons about — what source exists, and which of it implements a declared
/// brick — silently disagreed with what the build produced. Build output is excluded by DIRECTORY,
/// which is the compiler's own rule; a name is not evidence.</para>
/// </summary>
public sealed class BrickSourceResolverCompiledNamesTests : IDisposable
{
    private readonly string _dir;

    public BrickSourceResolverCompiledNamesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "resolver-names-" + Guid.NewGuid().ToString("N"));
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
    [InlineData("Widget.g.cs")]
    [InlineData("Widget.generated.cs")]
    [InlineData("Widget.Designer.cs")]
    public void Source_the_compiler_compiles_is_in_the_inventory_whatever_it_is_named(string name)
    {
        Write(name, "public sealed class Widget { }");

        BrickSourceResolver.Scan(_dir).SourceFiles
            .Select(Path.GetFileName)
            .Should().Contain(name,
                "the SDK compiles this file, so a verification that reasons about the project's source "
                + "must see it");
    }

    [Fact]
    public void A_brick_directory_holding_only_generated_looking_files_resolves()
    {
        // The user-visible consequence. A directory named after the brick resolves only when
        // there is C# inside it — correctly, since an empty folder is not an implementation. With
        // the suffix list in place, a brick whose directory held Widget.g.cs looked empty, so a
        // real compiled implementation read as "declared but absent" and the composition course
        // refused a brick that was there all along.
        Write("Widget/Widget.g.cs", "public sealed class Widget { }");

        var inventory = BrickSourceResolver.Scan(_dir);

        BrickSourceResolver.Resolve(inventory, "widget").Should().NotBeEmpty();
    }

    [Fact]
    public void Build_output_is_still_excluded_by_directory()
    {
        // The directory rule is what keeps a stale obj/ copy from resolving a brick whose source
        // was deleted. Dropping the name list must not touch it.
        Write("obj/Debug/Widget.cs", "public sealed class Widget { }");
        Write("bin/Debug/Widget.cs", "public sealed class Widget { }");

        var inventory = BrickSourceResolver.Scan(_dir);

        inventory.SourceFiles.Should().BeEmpty();
        BrickSourceResolver.Resolve(inventory, "widget").Should().BeEmpty();
    }
}
