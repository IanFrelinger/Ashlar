using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Ashlar.Infrastructure.Testing.CodeAnalysis;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
public sealed class CompilerReferenceInputTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "compiler-references-" + Guid.NewGuid().ToString("N"));
    private readonly RoslynCodeAnalysisService _compiler = new(NullLogger<RoslynCodeAnalysisService>.Instance);

    public CompilerReferenceInputTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task An_unused_missing_caller_reference_refuses_before_emitting()
    {
        var missing = Path.Combine(_directory, "missing.dll");
        var output = Path.Combine(_directory, "candidate.dll");
        var result = await _compiler.CompileAsync("public class Candidate {}", "Candidate", output, [missing]);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain(missing).And.Contain("caller-supplied");
        File.Exists(output).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Empty_caller_reference_is_a_configuration_error(string? path)
    {
        Action build = () => RoslynCodeAnalysisService.BuildReferenceSet([path!]);
        build.Should().Throw<InvalidOperationException>().WithMessage("*caller-supplied*not a file*");
    }

    [Fact]
    public void A_directory_is_not_a_caller_reference()
    {
        Action build = () => RoslynCodeAnalysisService.BuildReferenceSet([_directory]);
        build.Should().Throw<InvalidOperationException>().WithMessage("*caller-supplied*not a file*");
    }

    [Fact]
    public async Task A_declared_managed_dependency_is_used_by_the_emitted_candidate()
    {
        var dependencyPath = Path.Combine(_directory, "DeclaredDependency.dll");
        var dependency = CSharpCompilation.Create("DeclaredDependency",
            [CSharpSyntaxTree.ParseText("public static class DeclaredDependency { public const int Value = 42; }")],
            RoslynCodeAnalysisService.BuildReferenceSet(null),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        dependency.Emit(dependencyPath).Success.Should().BeTrue();

        var output = Path.Combine(_directory, "candidate.dll");
        var result = await _compiler.CompileAsync(
            "public static class Candidate { public static int Answer() => DeclaredDependency.Value; }",
            "Candidate", output, [dependencyPath]);

        result.Success.Should().BeTrue(string.Join(" | ", result.Errors));
        var assembly = Assembly.Load(File.ReadAllBytes(output));
        assembly.GetType("Candidate")!.GetMethod("Answer")!.Invoke(null, null).Should().Be(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_native_and_non_PE_files_are_refused_by_name(bool nativePe)
    {
        var path = InvalidReference(nativePe);
        Action build = () => RoslynCodeAnalysisService.BuildReferenceSet([path]);
        build.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain(path).And.Contain("managed metadata");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Discovered_native_and_non_PE_files_do_not_poison_a_valid_default_set(bool nativePe)
    {
        var path = InvalidReference(nativePe);
        var references = RoslynCodeAnalysisService.ComposeDefaultReferences(DefaultPaths().Append(path), []);
        references.OfType<PortableExecutableReference>().Should().NotContain(r => r.FilePath == path);
        AssertCompiles(references);
    }

    [Theory]
    [InlineData("System.Private.CoreLib.dll")]
    [InlineData("System.Runtime.dll")]
    [InlineData("System.Console.dll")]
    [InlineData("System.Linq.dll")]
    public void A_nonempty_default_set_missing_a_declared_assembly_is_refused(string omitted)
    {
        var incomplete = DefaultPaths().Where(path => Path.GetFileName(path) != omitted).ToArray();
        incomplete.Should().NotBeEmpty();
        Action build = () => RoslynCodeAnalysisService.ComposeDefaultReferences(incomplete, []);
        build.Should().Throw<InvalidOperationException>().WithMessage("*default set is missing*" + omitted + "*");
    }

    [Theory]
    [InlineData("System.Private.CoreLib.dll")]
    [InlineData("System.Runtime.dll")]
    [InlineData("System.Console.dll")]
    [InlineData("System.Linq.dll")]
    public void Fallback_repairs_a_partial_primary_set_and_the_result_compiles(string omitted)
    {
        var references = RoslynCodeAnalysisService.ComposeDefaultReferences(
            DefaultPaths().Where(path => Path.GetFileName(path) != omitted), DefaultPaths());
        AssertCompiles(references);
    }

    [Fact]
    public void A_native_file_named_as_a_required_assembly_does_not_satisfy_the_floor()
    {
        var path = Path.Combine(_directory, "System.Console.dll");
        WriteNativePe(path);
        Action build = () => RoslynCodeAnalysisService.ComposeDefaultReferences(
            DefaultPaths().Where(p => Path.GetFileName(p) != "System.Console.dll").Append(path), []);
        build.Should().Throw<InvalidOperationException>().WithMessage("*missing*System.Console.dll*");
    }

    [Fact]
    public void An_explicit_PE_with_a_corrupt_metadata_header_is_refused_by_name()
    {
        var path = Path.Combine(_directory, "corrupt-metadata.dll");
        WriteCorruptMetadataPe(path);
        Action build = () => RoslynCodeAnalysisService.BuildReferenceSet([path]);
        build.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain(path).And.Contain("caller-supplied").And.Contain("managed metadata");
    }

    [Fact]
    public async Task An_unused_corrupt_metadata_reference_refuses_before_emitting()
    {
        var path = Path.Combine(_directory, "corrupt-metadata.dll");
        WriteCorruptMetadataPe(path);
        var output = Path.Combine(_directory, "candidate.dll");
        var result = await _compiler.CompileAsync("public class Candidate {}", "Candidate", output, [path]);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain(path).And.Contain("caller-supplied");
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public void A_discovered_corrupt_metadata_header_does_not_poison_valid_defaults()
    {
        var path = Path.Combine(_directory, "corrupt-metadata.dll");
        WriteCorruptMetadataPe(path);
        var references = RoslynCodeAnalysisService.ComposeDefaultReferences(DefaultPaths().Append(path), []);
        references.OfType<PortableExecutableReference>().Should().NotContain(r => r.FilePath == path);
        AssertCompiles(references);
    }

    [Theory]
    [InlineData("System.Private.CoreLib.dll")]
    [InlineData("System.Runtime.dll")]
    [InlineData("System.Console.dll")]
    [InlineData("System.Linq.dll")]
    public void A_corrupt_metadata_header_does_not_satisfy_a_required_name(string name)
    {
        var path = Path.Combine(_directory, name);
        WriteCorruptMetadataPe(path);
        Action build = () => RoslynCodeAnalysisService.ComposeDefaultReferences(
            DefaultPaths().Where(p => Path.GetFileName(p) != name).Append(path), []);
        build.Should().Throw<InvalidOperationException>().WithMessage("*missing*" + name + "*");
    }

    [Theory]
    [InlineData("System.Private.CoreLib.dll")]
    [InlineData("System.Runtime.dll")]
    [InlineData("System.Console.dll")]
    [InlineData("System.Linq.dll")]
    public void A_valid_fallback_replaces_a_primary_with_a_corrupt_metadata_header(string name)
    {
        var path = Path.Combine(_directory, name);
        WriteCorruptMetadataPe(path);
        var references = RoslynCodeAnalysisService.ComposeDefaultReferences(
            DefaultPaths().Where(p => Path.GetFileName(p) != name).Append(path), DefaultPaths());
        references.OfType<PortableExecutableReference>().Should().NotContain(r => r.FilePath == path);
        AssertCompiles(references);
    }

    private string InvalidReference(bool nativePe)
    {
        var path = Path.Combine(_directory, nativePe ? "native.dll" : "text.dll");
        if (nativePe) WriteNativePe(path);
        else File.WriteAllText(path, "This is not a PE image.");
        return path;
    }

    private static string[] DefaultPaths() => RoslynCodeAnalysisService.RequiredDefaultReferenceAssemblies
        .Select(name => Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, name)).ToArray();

    private static void AssertCompiles(IEnumerable<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create("DefaultReferenceProbe",
            [CSharpSyntaxTree.ParseText("using System; using System.Linq; public static class Probe { public static int Run() { Console.WriteLine(42); return new[] { 21, 21 }.Sum(); } }")],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        emitted.Success.Should().BeTrue(string.Join(" | ", emitted.Diagnostics));
    }

    private static void WriteNativePe(string path)
    {
        // Serialize a real AMD64 PE image containing native machine code and no CLR directory.
        // This reaches HasMetadata == false; the text-file control reaches BadImageFormatException.
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

    private static void WriteCorruptMetadataPe(string path)
    {
        // Emit a valid managed PE independently of the helper being tested. Damage only BSJB:
        // PE/CLR directories remain intact, so HasMetadata cannot detect this input fault.
        var compilation = CSharpCompilation.Create("MetadataHeaderFixture",
            [CSharpSyntaxTree.ParseText("public class Dependency {}")],
            DefaultPaths().Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        emitted.Success.Should().BeTrue(string.Join(" | ", emitted.Diagnostics));
        var original = image.ToArray();
        var damaged = (byte[])original.Clone();
        using (var valid = new PEReader(new MemoryStream(original)))
        {
            valid.HasMetadata.Should().BeTrue();
            valid.GetMetadataReader().IsAssembly.Should().BeTrue();
            var offset = valid.PEHeaders.MetadataStartOffset;
            original.Skip(offset).Take(4).Should().Equal(0x42, 0x53, 0x4a, 0x42);
            Array.Clear(damaged, offset, 4);
        }
        original.Zip(damaged).Count(pair => pair.First != pair.Second).Should().Be(4);
        File.WriteAllBytes(path, damaged);
        using var corrupt = new PEReader(File.OpenRead(path));
        corrupt.HasMetadata.Should().BeTrue();
        Action read = () => corrupt.GetMetadataReader();
        read.Should().Throw<BadImageFormatException>();
    }

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
