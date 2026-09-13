using System.Text.Json;
using System.Xml.Linq;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure.Validation.Adapters;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Checks the literal, unconditional commercial ProjectReference graph and the real validation
/// discovery/framework selectors. External references must exist; their graphs are outside this
/// lens. Native readiness's TRX check separately observes execution, without a full-selection claim.
/// </summary>
public sealed class CommercialCoverageConventionTests
{
    private sealed record Suite(string Project, string Framework, string Assembly);

    [Fact]
    public void Registered_test_roots_are_discovered_and_reach_every_commercial_project()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var suites = JsonSerializer.Deserialize<Suite[]>(
            File.ReadAllText(Path.Combine(root, "ci", "commercial-test-suites.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        suites.Should().HaveCount(3);
        CheckCoverage(root, suites).Should().HaveCount(8,
            "the current commercial graph has five production projects and three test roots");
    }

    [Fact]
    public void A_discovered_test_root_with_a_production_dependency_is_a_positive_control()
    {
        using var fixture = new GraphFixture();
        CheckCoverage(fixture.Root, fixture.Suites).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("stale root")]
    [InlineData("unregistered root")]
    [InlineData("orphan")]
    [InlineData("missing reference")]
    [InlineData("undiscoverable root")]
    [InlineData("wrong framework")]
    [InlineData("wrong assembly")]
    [InlineData("conditional reference")]
    [InlineData("dynamic reference")]
    [InlineData("excluded reference")]
    [InlineData("target-scoped reference")]
    public void Incomplete_or_unmeasurable_coverage_fails(string defect)
    {
        using var fixture = new GraphFixture();
        var suites = fixture.Suites;
        switch (defect)
        {
            case "empty":
                Directory.Delete(Path.Combine(fixture.Root, "commercial"), recursive: true);
                Directory.CreateDirectory(Path.Combine(fixture.Root, "commercial"));
                break;
            case "stale root": suites = [suites[0] with { Project = "commercial/tests/Missing.csproj" }]; break;
            case "unregistered root":
                fixture.Write("commercial/tests/More.Tests.csproj", TestProject());
                fixture.Write(suites[0].Project, TestProject().Replace("</ItemGroup>",
                    "<ProjectReference Include=\"More.Tests.csproj\" /></ItemGroup>"));
                break;
            case "orphan": fixture.Write("commercial/src/Orphan.csproj", "<Project />"); break;
            case "missing reference": File.Delete(Path.Combine(fixture.Root, "commercial/src/Production.csproj")); break;
            case "undiscoverable root":
                var hidden = "commercial/templates/Root.Tests.csproj";
                fixture.Write(hidden, TestProject("../src/Production.csproj"));
                File.Delete(Path.Combine(fixture.Root, suites[0].Project));
                suites = [suites[0] with { Project = hidden }];
                break;
            case "wrong framework": suites = [suites[0] with { Framework = "net10.0" }]; break;
            case "wrong assembly": suites = [suites[0] with { Assembly = "Other" }]; break;
            case "conditional reference":
                fixture.Write(suites[0].Project, TestProject().Replace("<ProjectReference ", "<ProjectReference Condition=\"'$(Configuration)' == 'Release'\" "));
                break;
            case "dynamic reference": fixture.Write(suites[0].Project, TestProject("$(Dependency).csproj")); break;
            case "excluded reference":
                fixture.Write(suites[0].Project, TestProject().Replace("<ProjectReference ", "<ProjectReference Exclude=\"../src/Production.csproj\" "));
                break;
            case "target-scoped reference":
                fixture.Write(suites[0].Project, TestProject().Replace("<ItemGroup>", "<Target Name=\"Unused\"><ItemGroup>")
                    .Replace("</ItemGroup>", "</ItemGroup></Target>"));
                break;
        }
        Action check = () => CheckCoverage(fixture.Root, suites);
        check.Should().Throw<InvalidOperationException>();
    }

    private static IReadOnlyCollection<string> CheckCoverage(string root, Suite[] suites)
    {
        var rootDirectory = new DirectoryInfo(root);
        var commercial = Path.Combine(root, "commercial");
        var projects = Directory.GetFiles(commercial, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(commercial, path).Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .ToDictionary(Path.GetFullPath, XDocument.Load, StringComparer.OrdinalIgnoreCase);
        Require(projects.Count > 0 && suites.Length > 0, "commercial projects and registered roots must be nonempty");
        var registered = suites.Select(suite => Path.GetFullPath(Path.Combine(root, suite.Project))).ToArray();
        Require(registered.Distinct(StringComparer.OrdinalIgnoreCase).Count() == suites.Length, "duplicate registered root");
        var tests = projects.Where(pair => pair.Value.Descendants().Any(element =>
            element.Name.LocalName == "PackageReference" && (string?)element.Attribute("Include") == "Microsoft.NET.Test.Sdk"))
            .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Require(tests.SetEquals(registered), "test roots differ from the receipt registry (new, missing or stale root)");
        foreach (var suite in suites)
        {
            var path = Path.GetFullPath(Path.Combine(root, suite.Project));
            Require(ValidationServiceAdapter.IsDiscoverableTestProject(new FileInfo(path), rootDirectory), "validation does not discover " + suite.Project);
            Require(ValidationServiceAdapter.SelectTestFramework(path) == suite.Framework, "validation framework differs for " + suite.Project);
            var names = projects[path].Descendants().Where(e => e.Name.LocalName == "AssemblyName").ToArray();
            Require(names.Length <= 1 && (names.SingleOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(path)) == suite.Assembly,
                "receipt assembly differs for " + suite.Project);
        }
        var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, document) in projects)
        {
            var dependencies = new List<string>();
            foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
            {
                var include = (string?)reference.Attribute("Include");
                Require(!string.IsNullOrWhiteSpace(include) && include.IndexOfAny(['$', '*', '?', ';', '@']) < 0,
                    "commercial reference must be a literal path: " + path);
                Require(!reference.AncestorsAndSelf().Any(e => e.Attribute("Condition") is not null)
                    && reference.Parent?.Name.LocalName == "ItemGroup" && reference.Parent.Parent == document.Root
                    && reference.Attributes().All(attribute => attribute.Name.LocalName == "Include")
                    && !reference.HasElements,
                    "conditional or modified commercial reference needs an evaluated coverage rule: " + path);
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, include!.Replace('\\', Path.DirectorySeparatorChar)));
                Require(File.Exists(target), "missing referenced project: " + target);
                if (projects.ContainsKey(target)) dependencies.Add(target);
            }
            edges.Add(path, dependencies);
        }
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(registered);
        while (pending.TryPop(out var path))
            if (reached.Add(path))
                foreach (var dependency in edges[path]) pending.Push(dependency);
        Require(reached.SetEquals(projects.Keys), "commercial projects outside the selected test-root closure: "
            + string.Join(", ", projects.Keys.Except(reached, StringComparer.OrdinalIgnoreCase)));
        return reached;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string TestProject(string reference = "../src/Production.csproj") =>
        $"<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup>"
        + $"<PackageReference Include=\"Microsoft.NET.Test.Sdk\" /><ProjectReference Include=\"{reference}\" />"
        + "</ItemGroup></Project>";

    private sealed class GraphFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "commercial-coverage-" + Guid.NewGuid().ToString("N"));
        public Suite[] Suites { get; } = [new("commercial/tests/Root.Tests.csproj", "net8.0", "Root.Tests")];
        public GraphFixture()
        {
            Write(Suites[0].Project, TestProject());
            Write("commercial/src/Production.csproj", "<Project />");
        }
        public void Write(string path, string content)
        {
            path = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
