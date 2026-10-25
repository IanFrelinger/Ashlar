using System.CommandLine;
using System.CommandLine.Invocation;
using System.Reflection;
using System.Text.Json;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Scaffolds new Ashlar extension artifacts.
/// </summary>
public sealed class NewCommand : Command
{
    /// <summary>Creates a new NewCommand instance.</summary>
    public NewCommand()
        : base("new", "Scaffold Ashlar extension artifacts.")
    {
        AddCommand(CreateBrickCommand());
    }

    private static Command CreateBrickCommand()
    {
        var nameArg = new Argument<string>("name", "Brick name, for example MyThing.");
        var outputOpt = new Option<string>(
            "--output",
            () => Environment.CurrentDirectory,
            "Directory where the brick solution folder should be created.");
        var jsonOpt = new Option<bool>("--json", () => false, "Emit machine-readable JSON output.");
        var ashlarVersionOpt = new Option<string?>("--ashlar-version", () => null, "Ashlar.Authoring package version to reference.");

        var command = new Command("brick", "Scaffold a code-authored Ashlar brick project and test project.")
        {
            nameArg,
            outputOpt,
            jsonOpt,
            ashlarVersionOpt
        };

        command.SetHandler((InvocationContext context) =>
        {
            var name = context.ParseResult.GetValueForArgument(nameArg);
            var output = context.ParseResult.GetValueForOption(outputOpt) ?? Environment.CurrentDirectory;
            var json = context.ParseResult.GetValueForOption(jsonOpt);
            var ashlarVersion = context.ParseResult.GetValueForOption(ashlarVersionOpt);
            context.ExitCode = ExecuteBrick(name, output, json, ashlarVersion);
        });

        return command;
    }

    internal static int ExecuteBrick(string name, string outputDirectory, bool json, string? ashlarVersion = null)
    {
        var normalizedName = NormalizeBrickName(name);
        if (normalizedName is null)
        {
            WriteResult(false, "Brick name must be a valid C# identifier segment (letters, digits, underscore; cannot start with a digit).", null, json);
            return 1;
        }

        var templateFiles = LoadTemplateFiles();
        if (templateFiles.Count == 0)
        {
            WriteResult(false, "Brick template resources were not found in the CLI package.", null, json);
            return 1;
        }

        var root = Path.GetFullPath(outputDirectory);
        var projectRoot = Path.Combine(root, $"{normalizedName}Brick");
        var testsRoot = Path.Combine(root, $"{normalizedName}Brick.Tests");
        if ((Directory.Exists(projectRoot) && Directory.EnumerateFileSystemEntries(projectRoot).Any()) ||
            (Directory.Exists(testsRoot) && Directory.EnumerateFileSystemEntries(testsRoot).Any()))
        {
            WriteResult(false, $"Refusing to overwrite existing brick project directories under {root}.", null, json);
            return 1;
        }

        var resolvedAshlarVersion = ResolveAshlarVersion(ashlarVersion);
        var replacements = BuildReplacements(normalizedName, resolvedAshlarVersion);
        CopyTemplate(templateFiles, root, replacements);

        var testProject = Path.Combine(testsRoot, $"{normalizedName}Brick.Tests.csproj");
        WriteResult(
            true,
            $"Scaffolded {normalizedName}Brick.",
            new
            {
                outputDirectory = root,
                project = Path.Combine(projectRoot, $"{normalizedName}Brick.csproj"),
                testProject,
                next = $"dotnet test \"{testProject}\"",
                // Ashlar.Authoring is not published to nuget.org yet; without a feed the generated
                // PackageReference fails restore with NU1101. Say so up front instead of at restore time.
                restoreHint = $"Ashlar.Authoring {resolvedAshlarVersion} must be restorable: pass a local feed " +
                              "(dotnet restore --source FEED --source https://api.nuget.org/v3/index.json) " +
                              "or replace the PackageReference with a ProjectReference to src/Ashlar.Authoring/Ashlar.Authoring.csproj " +
                              "(docs/AuthoringBricks.md, section Restoring Ashlar.Authoring)."
            },
            json);
        return 0;
    }

    private static Dictionary<string, string> BuildReplacements(string brickName, string ashlarVersion)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__BrickName__"] = brickName,
            ["__DisplayName__"] = $"{brickName} Brick",
            ["__BrickId__"] = ToBrickId(brickName),
            ["__Namespace__"] = $"{brickName}Brick",
            ["__AshlarVersion__"] = ashlarVersion
        };
    }

    private static string ResolveAshlarVersion(string? overrideVersion)
    {
        if (!string.IsNullOrWhiteSpace(overrideVersion))
            return overrideVersion.Trim();

        // Both attributes derive from <Version>, which Directory.Build.targets sets from the root
        // VERSION file, so the scaffolded PackageReference tracks the packed CLI version. No release
        // number is hard-coded here: "0.0.0" is an obviously-unreleased sentinel for the (unexpected)
        // case where the assembly carries no version metadata at all.
        var assembly = typeof(NewCommand).Assembly;
        var info = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var assemblyVersion = assembly.GetName().Version;
        var version = (info
                       ?? (assemblyVersion is null ? null : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}")
                       ?? "0.0.0")
            .Split('+', 2)[0];
        return string.IsNullOrWhiteSpace(version) ? "0.0.0" : version;
    }

    private static void CopyTemplate(
        IReadOnlyDictionary<string, string> templateFiles,
        string outputRoot,
        IReadOnlyDictionary<string, string> replacements)
    {
        foreach (var (relative, text) in templateFiles)
        {
            var targetRelative = ReplaceTokens(relative, replacements);
            var target = Path.Combine(outputRoot, targetRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, ReplaceTokens(text, replacements));
        }
    }

    private static IReadOnlyDictionary<string, string> LoadTemplateFiles()
    {
        const string prefix = "Ashlar.CLI.Templates.Brick/";
        var assembly = typeof(NewCommand).Assembly;
        var embedded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                continue;
            using var reader = new StreamReader(stream);
            embedded[resourceName[prefix.Length..].Replace('\\', '/')] = reader.ReadToEnd();
        }

        if (embedded.Count > 0)
            return embedded;

        var repoRoot = TryFindRepoRoot();
        if (repoRoot is null)
            return embedded;

        var templateRoot = Path.Combine(repoRoot, "samples", "templates", "brick");
        if (!Directory.Exists(templateRoot))
            return embedded;

        foreach (var sourcePath in Directory.GetFiles(templateRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(templateRoot, sourcePath).Replace('\\', '/');
            embedded[relative] = File.ReadAllText(sourcePath);
        }

        return embedded;
    }

    private static string ReplaceTokens(string value, IReadOnlyDictionary<string, string> replacements)
    {
        foreach (var (token, replacement) in replacements)
            value = value.Replace(token, replacement, StringComparison.Ordinal);
        return value;
    }

    private static string? NormalizeBrickName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var cleaned = new string(name.Trim().Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (string.IsNullOrWhiteSpace(cleaned) || char.IsDigit(cleaned[0]))
            return null;

        return char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
    }

    private static string ToBrickId(string name)
    {
        var chars = new List<char>();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
                chars.Add('-');
            chars.Add(char.ToLowerInvariant(c));
        }
        return string.Concat(chars);
    }

    private static string FindRepoRoot()
    {
        return TryFindRepoRoot() ?? AppContext.BaseDirectory;
    }

    private static string? TryFindRepoRoot()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Ashlar.sln")) &&
                Directory.Exists(Path.Combine(current.FullName, "samples", "templates", "brick")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        return null;
    }

    private static void WriteResult(bool ok, string summary, object? details, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { ok, summary, details }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine(ok ? "ashlar new brick: ok" : "ashlar new brick: failed");
        Console.WriteLine(summary);
        if (details is not null)
            Console.WriteLine(JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = true }));
    }
}
