using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JobSearch.Studio;

/// <summary>One setting a step takes: what it means, for the designer and for the writer.</summary>
public sealed record StepSetting(string Label, string Help, string Example);

/// <summary>
/// What a step does, in words: generic, with no one's personal values. The private model writes it from what a user
/// typed; it is what may leave the host for the step writer once the PII gate passes it.
/// </summary>
public sealed record StepDesign
{
    /// <summary><see cref="StepContract.Check"/> or <see cref="StepContract.Column"/>.</summary>
    public string Kind { get; init; } = StepContract.Check;
    /// <summary>Short name, e.g. "Exclude postings that mention words".</summary>
    public string Name { get; init; } = "";
    /// <summary>One or two sentences for the library.</summary>
    public string Description { get; init; } = "";
    /// <summary>Up to three settings.</summary>
    public List<StepSetting> Settings { get; init; } = [];
    /// <summary>The exact rules, in terms of the inputs and settings.</summary>
    public string Behavior { get; init; } = "";
}

/// <summary>One witness case: every input, and what the step must answer.</summary>
public sealed record StepCase
{
    /// <summary>All twelve inputs (<see cref="StepContract.Inputs"/>).</summary>
    public Dictionary<string, string> Inputs { get; init; } = new(StringComparer.Ordinal);
    /// <summary><c>verdict</c> and <c>note</c> for a check; <c>value</c> for a column.</summary>
    public Dictionary<string, string> Expected { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>A step's code and cases, as the writer (or a seed) provides them.</summary>
public sealed record StepCode
{
    /// <summary>The method(s), written to <see cref="StepSource.MethodSignature"/>.</summary>
    public string Code { get; init; } = "";
    /// <summary>The witness cases.</summary>
    public List<StepCase> Cases { get; init; } = [];
}

/// <summary>A starter step shipped with the studio, certified by the platform on first start like any other.</summary>
public sealed record StepSeed
{
    /// <summary>Stable seed name; the step id is derived from it.</summary>
    public string Slug { get; init; } = "";
    /// <summary>The design.</summary>
    public StepDesign Design { get; init; } = new();
    /// <summary>The code.</summary>
    public string Code { get; init; } = "";
    /// <summary>Witness cases in the writer's flat form.</summary>
    public List<Dictionary<string, string>> Cases { get; init; } = [];
}

/// <summary>
/// Turns a design and model-written code into the one source file the certifier compiles. Everything but the method
/// body is fixed here: the class, the metadata, the interface and the input handling are never model-written.
/// <para>Ashlar's mutation testing reaches only <c>ExecuteAsync</c> and private <b>instance</b> methods, and at most
/// four sites per operator in source order (DOGFOOD-FINDINGS F13). So the written methods must be private instance
/// methods (the word <c>static</c> is refused) and come before <c>ExecuteAsync</c>, where the budget reaches them
/// first.</para>
/// </summary>
public static class StepSource
{
    /// <summary>The fully qualified type every step compiles to; each step is its own assembly.</summary>
    public const string TypeName = "JobSearch.Steps.GeneratedStep";

    /// <summary>Largest accepted code, in characters.</summary>
    public const int MaxCode = 12000;

    private static readonly string Parameters = string.Join(", ", StepContract.Inputs.Select(i => "string " + i));

    /// <summary>The method the writer implements, by kind.</summary>
    public static string MethodSignature(string kind) => kind switch
    {
        StepContract.Check => $"private string Evaluate({Parameters}, out string note)",
        StepContract.Column => $"private string Value({Parameters})",
        _ => throw new ArgumentException($"unknown step kind {kind}", nameof(kind)),
    };

    private static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(1);

    private static readonly (Regex Pattern, string Problem)[] Refused =
    [
        (new Regex(@"\bstatic\b", RegexOptions.None, RegexBudget), "helper methods must be private instance methods: do not use \"static\" (mutation testing does not reach static methods)"),
        (new Regex(@"\b(namespace|using|unsafe|stackalloc|extern|goto|fixed)\b", RegexOptions.None, RegexBudget), "write only methods: no namespace, using, unsafe, stackalloc, extern, goto or fixed"),
        (new Regex(@"\b(class|struct|record|interface|enum|delegate|event)\b", RegexOptions.None, RegexBudget), "write only methods: no nested types, delegates or events"),
        (new Regex(@"^\s*\[", RegexOptions.Multiline, RegexBudget), "do not use attributes"),
        (new Regex(@"\b(public|protected|internal|override|virtual|abstract)\b", RegexOptions.None, RegexBudget), "every method must be private"),
    ];

    /// <summary>Why the code cannot be used as written; empty when it can. Cheaper than a certification run; the IL fence is the real check.</summary>
    public static IReadOnlyList<string> Lint(string kind, string code)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(code))
        {
            problems.Add("the code is empty");
            return problems;
        }
        if (code.Length > MaxCode)
            problems.Add($"the code is longer than {MaxCode} characters");
        var signature = MethodSignature(kind);
        var at = code.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0 || code.IndexOf(signature, at + 1, StringComparison.Ordinal) >= 0)
            problems.Add($"the code must define this method exactly once: {signature}");
        var bare = StripLiteralsAndComments(code);
        foreach (var (pattern, problem) in Refused)
            if (pattern.IsMatch(bare) && !problems.Contains(problem))
                problems.Add(problem);
        if (bare.Count(c => c == '{') != bare.Count(c => c == '}'))
            problems.Add("the code's braces do not balance");
        return problems;
    }

    /// <summary>The code with string and character literals emptied and comments removed, so lint reads only code.</summary>
    public static string StripLiteralsAndComments(string code)
    {
        var sb = new StringBuilder(code.Length);
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                while (i < code.Length && code[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            if (ch == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < code.Length && !(code[i] == '*' && code[i + 1] == '/')) i++;
                i++;
                continue;
            }
            if (ch is '"' or '\'')
            {
                var quote = ch;
                var verbatim = i > 0 && code[i - 1] == '@';
                sb.Append(quote);
                i++;
                while (i < code.Length)
                {
                    if (!verbatim && code[i] == '\\') { i += 2; continue; }
                    if (code[i] == quote)
                    {
                        if (verbatim && i + 1 < code.Length && code[i + 1] == quote) { i += 2; continue; }
                        break;
                    }
                    i++;
                }
                sb.Append(quote);
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>The C# source of the step brick.</summary>
    public static string Render(string stepId, StepDesign design, string code)
    {
        ArgumentNullException.ThrowIfNull(design);
        var kind = design.Kind;
        var outputs = StepContract.Outputs(kind);
        var sb = new StringBuilder();
        sb.Append("""
            // Generated by Job Search Studio. The class, metadata and ExecuteAsync are the studio's fixed template; the
            // methods before ExecuteAsync were written for this step and are what the certification gate judged.
            using System.Globalization;
            using System.Text;
            using Ashlar.Core.Domain.Bricks;
            using Ashlar.Core.Domain.Execution;

            namespace JobSearch.Steps;

            public sealed class GeneratedStep : DomainBrick
            {
                public GeneratedStep()
                {

            """);
        sb.Append("        Id = ").Append(Literal(stepId)).Append(";\n");
        sb.Append("        Name = ").Append(Literal(design.Name)).Append(";\n");
        sb.Append("        Version = \"1.0.0\";\n");
        sb.Append("        Category = ").Append(kind == StepContract.Check ? "BrickCategory.Validation" : "BrickCategory.Analysis").Append(";\n");
        sb.Append("        Description = ").Append(Literal(design.Description)).Append(";\n");
        sb.Append("""
                    Implementations = new BrickImplementations
                    {
                        Deterministic = new DeterministicImplementation
                        {
                            Id = "rules", Name = "Rules", Executor = "code", Description = "Certified studio step; no model at run time.",
                        },
                    };
                    Interface = new BrickInterface
                    {
                        Inputs =
                        [

            """);
        foreach (var input in StepContract.Inputs)
            sb.Append("                new BrickInputDefinition(").Append(Literal(input)).Append(", \"string\", ").Append(Literal(StepContract.InputHelp[input])).Append("),\n");
        sb.Append("            ],\n            Outputs =\n            [\n");
        foreach (var o in outputs)
            sb.Append("                new BrickOutputDefinition(").Append(Literal(o)).Append(", \"string\", ").Append(Literal(o)).Append("),\n");
        sb.Append("            ],\n        };\n    }\n\n");

        foreach (var line in code.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n').Split('\n'))
            sb.Append("    ").Append(line).Append('\n');

        var args = string.Join(", ", StepContract.Inputs.Select(i => $"input.Get<string>({Literal(i)})"));
        sb.Append("\n    public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)\n    {\n");
        if (kind == StepContract.Check)
        {
            sb.Append("        var verdict = Evaluate(").Append(args).Append(", out var note);\n");
            sb.Append("        var output = new BrickOutput { Summary = verdict };\n");
            sb.Append("        output.Set(\"verdict\", verdict);\n        output.Set(\"note\", note);\n");
        }
        else
        {
            sb.Append("        var value = Value(").Append(args).Append(");\n");
            sb.Append("        var output = new BrickOutput { Summary = value };\n");
            sb.Append("        output.Set(\"value\", value);\n");
        }
        sb.Append("        return Task.FromResult(output);\n    }\n}\n");
        return sb.ToString();
    }

    /// <summary>The project file the certifier's dependency fence reads.</summary>
    public const string ProjectFile = """
        <Project Sdk="Microsoft.NET.Sdk">
          <!-- A Job Search Studio step: the certifier compiles the single .cs file itself and never builds this project. -->
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <IsPackable>false</IsPackable>
            <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Ashlar.Brick.Contracts" Version="0.1.0" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>The witness file the certifier runs: every case's inputs and expected outputs.</summary>
    public static string Witness(string stepId, IReadOnlyList<StepCase> cases) => JsonSerializer.Serialize(new
    {
        brickId = stepId,
        cases = cases.Select(c => new { input = c.Inputs, expectedOutput = c.Expected }),
    }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never });

    /// <summary>Flat cases (the writer's form: every input and output as one object) into witness cases.</summary>
    public static List<StepCase> FromFlat(string kind, IEnumerable<IReadOnlyDictionary<string, string>> flat)
    {
        var outputs = StepContract.Outputs(kind);
        var cases = new List<StepCase>();
        foreach (var row in flat)
        {
            var c = new StepCase();
            foreach (var input in StepContract.Inputs)
                c.Inputs[input] = row.TryGetValue(input, out var v) ? v ?? "" : "";
            foreach (var o in outputs)
                c.Expected[o] = row.TryGetValue(o, out var v) ? v ?? "" : "";
            cases.Add(c);
        }
        return cases;
    }

    /// <summary>Problems with a set of cases before certification: too few, duplicates, invalid verdicts.</summary>
    public static IReadOnlyList<string> CheckCases(string kind, IReadOnlyList<StepCase> cases)
    {
        var problems = new List<string>();
        if (cases.Count < 6)
            problems.Add("write at least 6 witness cases");
        if (cases.Count > 40)
            problems.Add("write at most 40 witness cases");
        if (kind == StepContract.Check)
        {
            for (var i = 0; i < cases.Count; i++)
                if (!StepContract.Verdicts.Contains(cases[i].Expected.GetValueOrDefault("verdict") ?? ""))
                    problems.Add($"case {(i + 1).ToString(CultureInfo.InvariantCulture)}: verdict must be pass, flag or exclude");
            if (!cases.Any(c => c.Expected.GetValueOrDefault("verdict") == "pass") || !cases.Any(c => c.Expected.GetValueOrDefault("verdict") != "pass"))
                problems.Add("include cases that pass and cases that do not");
        }
        var keys = cases.Select(c => string.Join("\u0001", StepContract.Inputs.Select(i => c.Inputs.GetValueOrDefault(i)))).ToList();
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
            problems.Add("two cases have identical inputs");
        return problems;
    }

    /// <summary>A step id from a name: <c>step.&lt;slug&gt;.&lt;8 hex&gt;</c>.</summary>
    public static string NewId(string name, string? suffix = null)
    {
        var slug = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
                slug.Append(ch);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
            if (slug.Length >= 40)
                break;
        }
        var s = slug.ToString().Trim('-');
        return $"step.{(s.Length == 0 ? "custom" : s)}.{suffix ?? Guid.NewGuid().ToString("N")[..8]}";
    }

    /// <summary>A C# string literal.</summary>
    public static string Literal(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when ch < ' ' => "\\u" + ((int)ch).ToString("x4", CultureInfo.InvariantCulture),
                _ => ch.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }
}
