using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Ashlar.Analyzers.Tests;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

// The project source-links the actual analyzer harness helper. These tests exercise its inputs;
// readiness additionally runs the analyzer project and its complete sample suite.
[Trait("Category", "Certification")]
public sealed class AnalyzerReferenceInputTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "analyzer-references-" + Guid.NewGuid().ToString("N"));
    public AnalyzerReferenceInputTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void The_live_helper_compiles_framework_types_and_executes_the_emitted_sample()
        => AssertCompiles(AnalyzerReferenceSet.For(typeof(AnalyzerReferenceInputTests)));

    [Fact]
    public void A_declared_anchor_outside_the_output_directory_is_used_by_the_sample()
    {
        var path = EmitDeclaredAnchor();
        var references = AnalyzerReferenceSet.Compose([path], [], FrameworkPaths());
        AssertCompiles(references,
            "public static class Probe { public static int Answer() => DeclaredAnchor.Value; }");
    }

    [Fact]
    public void A_corrupt_metadata_header_is_refused_for_an_explicit_anchor()
    {
        var valid = EmitDeclaredAnchor();
        AssertCompiles(AnalyzerReferenceSet.Compose([valid], [], FrameworkPaths()),
            "public static class Probe { public static int Answer() => DeclaredAnchor.Value; }");
        var corrupt = CorruptMetadataHeader(valid, "CorruptAnchor.dll");

        Action compose = () => AnalyzerReferenceSet.Compose([corrupt], [], FrameworkPaths());
        compose.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain(corrupt).And.Contain("managed metadata");
    }

    [Fact]
    public void A_corrupt_metadata_header_cannot_satisfy_a_required_framework_name()
    {
        var valid = FrameworkPaths().Single(path => Path.GetFileName(path) == "System.Runtime.dll");
        var corrupt = CorruptMetadataHeader(valid, "System.Runtime.dll");
        var framework = FrameworkPaths().Select(path => path == valid ? corrupt : path).ToArray();

        Action compose = () => AnalyzerReferenceSet.Compose([], [], framework);
        compose.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing framework assemblies*System.Runtime.dll*");
    }

    [Fact]
    public void A_corrupt_app_local_header_does_not_hide_the_readable_framework_copy()
    {
        var valid = FrameworkPaths().Single(path => Path.GetFileName(path) == "System.Runtime.dll");
        var corrupt = CorruptMetadataHeader(valid, "System.Runtime.dll");
        var references = AnalyzerReferenceSet.Compose([], [corrupt], FrameworkPaths());

        references.OfType<PortableExecutableReference>().Where(reference => Path.GetFileName(reference.FilePath) == "System.Runtime.dll")
            .Should().ContainSingle().Which.FilePath.Should().Be(valid);
        AssertCompiles(references);
    }

    [Theory]
    [InlineData("System.Private.CoreLib.dll")]
    [InlineData("System.Runtime.dll")]
    [InlineData("System.Collections.dll")]
    [InlineData("System.Linq.dll")]
    [InlineData("System.ComponentModel.dll")]
    [InlineData("System.Runtime.Numerics.dll")]
    public void A_nonempty_framework_subset_is_refused_by_name(string omitted)
    {
        var incomplete = FrameworkPaths().Where(path => Path.GetFileName(path) != omitted).ToArray();
        incomplete.Should().NotBeEmpty();
        Action compose = () => AnalyzerReferenceSet.Compose([], [], incomplete);
        compose.Should().Throw<InvalidOperationException>().WithMessage("*missing framework assemblies*" + omitted + "*");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("missing.dll")]
    public void Unavailable_declared_anchors_are_refused_even_with_a_complete_framework(string name)
    {
        var path = name == "missing.dll" ? Path.Combine(_directory, name) : name;
        Action compose = () => AnalyzerReferenceSet.Compose([path], [], FrameworkPaths());
        compose.Should().Throw<InvalidOperationException>().WithMessage("*anchor*missing*");
    }

    [Fact]
    public void A_dynamic_anchor_without_a_location_is_refused_by_the_live_helper()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("LocationlessAnchor"), AssemblyBuilderAccess.Run);
        var anchor = assembly.DefineDynamicModule("Module").DefineType("RequiredAnchor").CreateType()!;
        Action compose = () => AnalyzerReferenceSet.For(anchor);
        compose.Should().Throw<InvalidOperationException>().WithMessage("*RequiredAnchor*no assembly location*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Discovered_native_and_non_PE_files_are_skipped(bool nativePe)
    {
        var path = InvalidReference(nativePe, "discovered.dll");
        var references = AnalyzerReferenceSet.Compose([], [path], FrameworkPaths());
        references.OfType<PortableExecutableReference>().Should().NotContain(r => r.FilePath == path);
        AssertCompiles(references);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_and_non_PE_declared_anchors_are_refused(bool nativePe)
    {
        var path = InvalidReference(nativePe, "anchor.dll");
        Action compose = () => AnalyzerReferenceSet.Compose([path], [], FrameworkPaths());
        compose.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(path).And.Contain("managed metadata");
    }

    [Fact]
    public void A_valid_app_local_framework_copy_takes_precedence_and_still_compiles()
    {
        var local = Path.Combine(_directory, "System.ComponentModel.dll");
        File.Copy(FrameworkPaths().Single(p => Path.GetFileName(p) == "System.ComponentModel.dll"), local);
        var references = AnalyzerReferenceSet.Compose([], [local], FrameworkPaths());
        references.OfType<PortableExecutableReference>().Where(r => Path.GetFileName(r.FilePath) == "System.ComponentModel.dll")
            .Should().ContainSingle().Which.FilePath.Should().Be(local);
        AssertCompiles(references);
    }

    [Fact]
    public void A_native_app_local_copy_does_not_hide_the_managed_framework_copy()
    {
        var local = InvalidReference(true, "System.ComponentModel.dll");
        var references = AnalyzerReferenceSet.Compose([], [local], FrameworkPaths());
        references.OfType<PortableExecutableReference>().Should().NotContain(r => r.FilePath == local);
        AssertCompiles(references);
    }

    [Fact]
    public void A_native_file_cannot_satisfy_a_required_framework_name()
    {
        var native = InvalidReference(true, "System.ComponentModel.dll");
        Action compose = () => AnalyzerReferenceSet.Compose([], [native],
            FrameworkPaths().Where(p => Path.GetFileName(p) != "System.ComponentModel.dll"));
        compose.Should().Throw<InvalidOperationException>().WithMessage("*missing framework assemblies*System.ComponentModel.dll*");
    }

    private string InvalidReference(bool nativePe, string name)
    {
        var path = Path.Combine(_directory, name);
        if (!nativePe) File.WriteAllText(path, "This is not a PE image.");
        else
        {
            var image = new BlobBuilder();
            new NativePeBuilder().Serialize(image);
            File.WriteAllBytes(path, image.ToArray());
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            pe.PEHeaders.CoffHeader.Machine.Should().Be(Machine.Amd64);
            pe.PEHeaders.PEHeader.Should().NotBeNull();
            pe.PEHeaders.SectionHeaders.Should().ContainSingle(s => s.Name == ".text");
            pe.HasMetadata.Should().BeFalse();
        }
        return path;
    }

    private static string[] FrameworkPaths() => AnalyzerReferenceSet.RequiredFrameworkAssemblies
        .Select(name => Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, name)).ToArray();

    private string EmitDeclaredAnchor()
    {
        var path = Path.Combine(_directory, "DeclaredAnchor.dll");
        var dependency = CSharpCompilation.Create("DeclaredAnchor",
            [CSharpSyntaxTree.ParseText("public static class DeclaredAnchor { public const int Value = 42; }")],
            AnalyzerReferenceSet.Compose([], [], FrameworkPaths()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var emitted = dependency.Emit(path);
        emitted.Success.Should().BeTrue(string.Join(" | ", emitted.Diagnostics));
        return path;
    }

    private string CorruptMetadataHeader(string validPath, string name)
    {
        var original = File.ReadAllBytes(validPath);
        using var valid = new PEReader(new MemoryStream(original));
        valid.HasMetadata.Should().BeTrue();
        valid.GetMetadataReader().IsAssembly.Should().BeTrue();
        var offset = valid.PEHeaders.MetadataStartOffset;
        original.AsSpan(offset, 4).ToArray().Should().Equal(new byte[] { 0x42, 0x53, 0x4a, 0x42 });

        var damaged = (byte[])original.Clone();
        Array.Clear(damaged, offset, 4);
        original.Zip(damaged).Count(pair => pair.First != pair.Second).Should().Be(4);
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, damaged);

        using var corrupt = new PEReader(File.OpenRead(path));
        corrupt.HasMetadata.Should().BeTrue();
        corrupt.PEHeaders.MetadataStartOffset.Should().Be(offset);
        corrupt.PEHeaders.CorHeader!.MetadataDirectory.Should().Be(valid.PEHeaders.CorHeader!.MetadataDirectory);
        Action read = () => corrupt.GetMetadataReader();
        read.Should().Throw<BadImageFormatException>();
        return path;
    }

    private static void AssertCompiles(IEnumerable<MetadataReference> references,
        string source = "using System; using System.Numerics; public static class Probe { public static object Resolve(IServiceProvider p, Type t) => p.GetService(t); public static int Answer() => (int)(new BigInteger(21) * 2); }")
    {
        var compilation = CSharpCompilation.Create("AnalyzerReferenceProbe" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        emitted.Success.Should().BeTrue(string.Join(" | ", emitted.Diagnostics));
        Assembly.Load(output.ToArray()).GetType("Probe")!.GetMethod("Answer")!.Invoke(null, null).Should().Be(42);
    }

    // A real AMD64 PE section with native instructions and no CLR metadata. The text-file control
    // above reaches BadImageFormatException; this image reaches HasMetadata == false instead.
    private sealed class NativePeBuilder() : PEBuilder(
        new PEHeaderBuilder(machine: Machine.Amd64, imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll), null)
    {
        protected override ImmutableArray<Section> CreateSections() =>
            [new(".text", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute)];
        protected override BlobBuilder SerializeSection(string name, SectionLocation location)
        {
            var code = new BlobBuilder();
            code.WriteBytes(new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 }); // mov eax, 1; ret
            return code;
        }
        protected override PEDirectoriesBuilder GetDirectories() => new();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* A metadata reader may retain a file mapping on Windows. */ }
        catch (UnauthorizedAccessException) { /* Best-effort test-directory cleanup. */ }
    }
}
