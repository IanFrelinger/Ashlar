using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Ashlar.Infrastructure.Certification;

namespace Ashlar.Infrastructure.Testing.CodeAnalysis;

/// <summary>
/// Portable implementation of ICodeAnalysisService using Roslyn and ICSharpCode.Decompiler.
/// 
/// Provides:
/// - Code compilation using Microsoft.CodeAnalysis (Roslyn)
/// - Assembly decompilation using ICSharpCode.Decompiler
/// - Assembly analysis using System.Reflection
/// 
/// Fully portable - no command-line dependencies, works on all .NET platforms.
/// </summary>
public class RoslynCodeAnalysisService : ICodeAnalysisService
{
    private readonly ILogger<RoslynCodeAnalysisService> _logger;

    /// <summary>Initializes a new roslyn code analysis service.</summary>
    public RoslynCodeAnalysisService(ILogger<RoslynCodeAnalysisService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Compile asynchronously.</summary>
    public Task<CompilationResult> CompileAsync(
        string sourceCode,
        string assemblyName,
        string outputPath,
        IEnumerable<string>? references = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var startTime = DateTime.UtcNow;
            var errors = new List<string>();
            var warnings = new List<string>();

            try
            {
                _logger.LogInformation("Compiling C# code to assembly: {AssemblyName}", assemblyName);

                // Closed-world options: the same parse/emit surface as GateEmittedArtifactCompiler
                // so judged mutants and the shipped artifact cannot drift on language version,
                // unsafe, overflow, or optimization.
                var syntaxTree = CSharpSyntaxTree.ParseText(
                    sourceCode, BrickCompileOptions.ParseOptions, cancellationToken: cancellationToken);

            // Default references (core .NET libraries) plus any provided custom references.
            var allReferences = BuildReferenceSet(references);

            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { syntaxTree },
                allReferences,
                BrickCompileOptions.CompilationOptions);

            // Ensure output directory exists
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            // Emit the assembly
            var emitResult = compilation.Emit(outputPath, cancellationToken: cancellationToken);

            // Collect diagnostics
            foreach (var diagnostic in emitResult.Diagnostics)
            {
                var message = diagnostic.GetMessage();
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    errors.Add($"{diagnostic.Id}: {message}");
                }
                else if (diagnostic.Severity == DiagnosticSeverity.Warning)
                {
                    warnings.Add($"{diagnostic.Id}: {message}");
                }
            }

                var duration = DateTime.UtcNow - startTime;

                if (emitResult.Success)
                {
                    _logger.LogInformation("Successfully compiled assembly: {OutputPath} in {Duration}ms", outputPath, duration.TotalMilliseconds);
                    return new CompilationResult(true, outputPath, errors, warnings, duration);
                }
                else
                {
                    _logger.LogWarning("Compilation failed with {ErrorCount} error(s)", errors.Count);
                    return new CompilationResult(false, null, errors, warnings, duration);
                }
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Exception during compilation");
                errors.Add($"Exception: {ex.Message}");
                return new CompilationResult(false, null, errors, warnings, duration);
            }
        }, cancellationToken);
    }

    /// <summary>Decompile asynchronously.</summary>
    public async Task<DecompilationResult> DecompileAsync(
        string assemblyPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            if (!File.Exists(assemblyPath))
            {
                return new DecompilationResult(
                    false,
                    null,
                    null,
                    $"Assembly not found: {assemblyPath}",
                    DateTime.UtcNow - startTime);
            }

            _logger.LogInformation("Decompiling assembly: {AssemblyPath}", assemblyPath);

            // Ensure output directory exists
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            // Load assembly using ICSharpCode.Decompiler
            var decompiler = new CSharpDecompiler(assemblyPath, new DecompilerSettings
            {
                ThrowOnAssemblyResolveErrors = false
            });

            // Decompile to C# source
            var sourceCode = decompiler.DecompileWholeModuleAsString();

            // Write to output file
            await File.WriteAllTextAsync(outputPath, sourceCode, cancellationToken);

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation("Successfully decompiled assembly to: {OutputPath} in {Duration}ms", outputPath, duration.TotalMilliseconds);

            return new DecompilationResult(true, sourceCode, outputPath, null, duration);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogError(ex, "Exception during decompilation");
            return new DecompilationResult(false, null, null, ex.Message, duration);
        }
    }

    /// <summary>Analyze assembly asynchronously.</summary>
    public Task<AssemblyAnalysisResult> AnalyzeAssemblyAsync(
        string assemblyPath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var startTime = DateTime.UtcNow;

            try
            {
                if (!File.Exists(assemblyPath))
                {
                    return new AssemblyAnalysisResult(
                        false,
                        null,
                        null,
                        null,
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        $"Assembly not found: {assemblyPath}",
                        DateTime.UtcNow - startTime);
                }

                _logger.LogInformation("Analyzing assembly: {AssemblyPath}", assemblyPath);

                // Load assembly using reflection
                // Note: Assembly.LoadFrom is NOT available in .NET Standard 2.0
                // We use Assembly.Load(byte[]) for maximum compatibility across all platforms
                // This works on Windows, Linux, macOS, Android, iOS, and Unity
                Assembly assembly;
                AssemblyName assemblyName;
                
                try
                {
                    // Get assembly name first (works on all platforms)
                    assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                    
                    // Load assembly - use Load(byte[]) for .NET Standard 2.0 compatibility
                    // This is the recommended approach for .NET Standard 2.0 and works everywhere
                    var assemblyBytes = File.ReadAllBytes(assemblyPath);
                    assembly = Assembly.Load(assemblyBytes);
                }
                catch (FileNotFoundException)
                {
                    throw; // Re-throw file not found as-is
                }
                catch (BadImageFormatException ex)
                {
                    // Invalid assembly format
                    throw new InvalidOperationException(
                        $"Invalid assembly format: {assemblyPath}. " +
                        $"This may not be a valid .NET assembly. " +
                        $"Original error: {ex.Message}", ex);
                }
                catch (Exception ex)
                {
                    // Platform-specific loading issues
                    throw new InvalidOperationException(
                        $"Failed to load assembly from {assemblyPath}. " +
                        $"This may indicate platform compatibility issues. " +
                        $"Platform: {RuntimeInformation.OSDescription}, " +
                        $"Framework: {RuntimeInformation.FrameworkDescription}. " +
                        $"Original error: {ex.Message}", ex);
                }

                // Extract types
                var types = assembly.GetTypes()
                    .Select(t => t.FullName ?? t.Name)
                    .ToList();

                // Extract methods
                var methods = assembly.GetTypes()
                    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                    .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}")
                    .ToList();

                var duration = DateTime.UtcNow - startTime;
                _logger.LogInformation("Successfully analyzed assembly: {AssemblyName} in {Duration}ms", assemblyName.Name, duration.TotalMilliseconds);

                return new AssemblyAnalysisResult(
                    true,
                    assemblyName.Name,
                    assemblyName.Version?.ToString(),
                    assemblyName.CultureName,
                    types,
                    methods,
                    null,
                    duration);
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Exception during assembly analysis");
                return new AssemblyAnalysisResult(
                    false,
                    null,
                    null,
                    null,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    ex.Message,
                    duration);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Default references plus explicitly supplied custom reference paths — shared with the analyzer
    /// fence gate so gate and compiler resolve against the identical reference set.
    /// </summary>
    internal static List<MetadataReference> BuildReferenceSet(IEnumerable<string>? references)
    {
        var allReferences = GetDefaultReferences();
        if (references != null)
        {
            foreach (var path in references)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new InvalidOperationException(
                        $"Compilation reference set refused: caller-supplied path '{path ?? "<null>"}' is not a file on disk.");
                if (!CarriesManagedMetadata(path))
                    throw new InvalidOperationException(
                        $"Compilation reference set refused: caller-supplied path '{path}' has no readable managed metadata.");

                allReferences.Add(MetadataReference.CreateFromFile(path));
            }
        }

        return allReferences;
    }

    private static List<MetadataReference> GetDefaultReferences()
        => ComposeDefaultReferences(
            new[] { typeof(object).Assembly.Location, typeof(Console).Assembly.Location,
                typeof(System.Linq.Enumerable).Assembly.Location }.Concat(GetPathsFromRuntimeDirectory()),
            GetPathsFromTrustedPlatformAssemblies());

    // These are the assemblies the default set promises to provide, not a count of arbitrary files.
    internal static readonly string[] RequiredDefaultReferenceAssemblies =
        ["System.Private.CoreLib.dll", "System.Runtime.dll", "System.Console.dll", "System.Linq.dll"];

    internal static List<MetadataReference> ComposeDefaultReferences(
        IEnumerable<string> primary, IEnumerable<string> fallback)
    {
        var references = new List<MetadataReference>();
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || !addedPaths.Add(path))
                return;
            if (CarriesManagedMetadata(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        foreach (var path in primary)
        {
            TryAdd(path);
        }

        string[] Missing() => RequiredDefaultReferenceAssemblies.Except(
            references.OfType<PortableExecutableReference>().Select(r => Path.GetFileName(r.FilePath) ?? string.Empty),
            StringComparer.OrdinalIgnoreCase).ToArray();

        if (Missing().Length != 0)
        {
            foreach (var path in fallback)
            {
                TryAdd(path);
            }
        }

        var missing = Missing();
        if (missing.Length != 0)
            throw new InvalidOperationException(
                "Compilation reference set refused: the default set is missing " + string.Join(", ", missing)
                + ". Use a framework-dependent host with its reference assemblies available on disk.");

        return references;
    }

    private static bool CarriesManagedMetadata(string path)
    {
        // Roslyn's file reference factory is lazy. HasMetadata finds the CLR directory but does
        // not parse its header; read that too before an input fault becomes a candidate diagnostic.
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return false;
            _ = pe.GetMetadataReader();
            return true;
        }
        catch (BadImageFormatException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> GetPathsFromRuntimeDirectory()
    {
        string? coreLocation = null;
        string? runtimeDir = null;
        try
        {
            coreLocation = typeof(object).Assembly.Location;
            if (!string.IsNullOrEmpty(coreLocation))
                runtimeDir = Path.GetDirectoryName(coreLocation);
        }
        catch { /* ignore */ }

        if (string.IsNullOrEmpty(coreLocation)) yield break;
        yield return coreLocation;

        if (string.IsNullOrEmpty(runtimeDir)) yield break;

        foreach (var name in new[] { "System.Runtime", "System.Console", "System.Linq", "netstandard" })
        {
            var path = Path.Combine(runtimeDir, name + ".dll");
            if (File.Exists(path))
                yield return path;
        }
    }

    private static IEnumerable<string> GetPathsFromTrustedPlatformAssemblies()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrEmpty(tpa)) yield break;

        var needed = new[] { "System.Private.CoreLib", "System.Runtime", "System.Console", "System.Linq" };
        foreach (var path in tpa.Split(new[] { ';', Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (needed.Any(n => fileName.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
                yield return path;
        }
    }
}
