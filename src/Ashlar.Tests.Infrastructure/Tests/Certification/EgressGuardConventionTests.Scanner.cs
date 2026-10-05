using System.Text.RegularExpressions;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The population walk and the classifier behind <see cref="EgressGuardConventionTests"/>. Text-level on
/// purpose, like every scan in the required check: no Roslyn, no build. See the type remarks on the main
/// part for what each marker and rule means and what the scan cannot see.
/// </summary>
public sealed partial class EgressGuardConventionTests
{
    /// <summary>The trees that ship. Roots that do not exist in this checkout are skipped, not failed.</summary>
    private static readonly string[] ProductionRoots =
        ["src", "application", "applications", "commercial", "products", "tools", "consumer-template"];

    /// <summary>
    /// Excluded BY NAME, and on purpose: <c>spikes/</c> and <c>samples/</c> are not shipped and are outside
    /// Ashlar.sln, the same reason <c>DiagnosticSuppressionConventionTests</c> gives for the FirstFlight spike
    /// (a recorded run, not a shipped artifact). They are not production roots, and
    /// <see cref="F7_the_walk_reaches_shipped_code_and_skips_test_projects"/> asserts nothing under them is scanned.
    /// </summary>
    private static readonly string[] UnshippedRoots = ["spikes", "samples"];

    /// <summary>The TSV's <c>marker</c> column. Named "marker", not "family", so it is not confused with the runtime <c>EgressFamilies</c>.</summary>
    internal static class Marker
    {
        public const string HttpNew = "http.new";
        public const string HttpParam = "http.param";
        public const string HttpRegister = "http.register";
        public const string SdkClient = "sdk.client";
        public const string Socket = "socket";
        public const string Process = "process";
        public const string Door = "door";
        public const string Telemetry = "telemetry";
        public const string Store = "store";
        public const string ChatRegister = "chat.register";

        /// <summary>Never allowed, never pinned (F4). Counted so a control can prove the scan sees it.</summary>
        public const string Banned = "banned";

        /// <summary>The markers a TSV row may name. <see cref="Banned"/> is deliberately absent.</summary>
        public static readonly string[] Pinnable =
            [HttpNew, HttpParam, HttpRegister, SdkClient, Socket, Process, Door, Telemetry, Store, ChatRegister];
    }

    /// <summary>How an occurrence is guarded. The TSV's <c>guarded_by</c> column spells the non-None values.</summary>
    internal enum GuardKind
    {
        None = 0,

        /// <summary>G2: built by <c>EgressHttp.CreateClient</c>/<c>Wrap</c>, or handed one (sdk.client, G2-file).</summary>
        Wrapped,

        /// <summary>
        /// G3: a guard call precedes it in the same block or an enclosing one; or, for http.param, the stored
        /// client is sent only after one (the stored-client rule, <c>Scanner.StoredClientPrecedes</c>).
        /// </summary>
        Precedes,

        /// <summary>
        /// D1: an <c>AddHttpClient</c> followed, at a higher offset and as a statement of the same innermost code
        /// block (the file root of a top-level program counts), by a non-declaration <c>AddAshlarEgressGuard(</c>.
        /// </summary>
        Factory,
    }

    /// <summary>One examined token: where it is, which marker it is, and whether a rule guards it.</summary>
    internal sealed record Occurrence(string Path, string Marker, int Line, int Offset, string Token, GuardKind Guard)
    {
        public bool Guarded => Guard != GuardKind.None;

        public string Where => Path + ":" + Line;

        public override string ToString() =>
            $"{Where}  {Marker}  {Token}  [{(Guarded ? "guarded: " + Guard : "UNGUARDED")}]";
    }

    /// <summary>The guard kind a marker's guarded occurrences must carry; <see cref="GuardKind.None"/> when it has no guarded form.</summary>
    private static GuardKind ExpectedGuardKind(string marker) => marker switch
    {
        Marker.HttpNew or Marker.SdkClient => GuardKind.Wrapped,
        Marker.HttpRegister => GuardKind.Factory,
        Marker.Socket or Marker.Process or Marker.Door or Marker.Telemetry or Marker.HttpParam => GuardKind.Precedes,
        _ => GuardKind.None,
    };

    /// <summary>One production csproj of the scanned population: whether it is an executable or a test project, and its direct ProjectReferences.</summary>
    private sealed record ProjectInfo(string Path, bool IsExe, bool IsTest, List<string> References);

    /// <summary>
    /// Everything one walk of the production tree yields, computed once per test run (<see cref="Load"/>) or from
    /// in-memory sources for a route control (<see cref="FromSources"/>). It keeps the CLEANED code of every file;
    /// a <see cref="SourceModel"/> (blocks, line starts) is rebuilt on demand and cached only for the files a route
    /// check reads. Word mentions are indexed by name on first use, which is the supply-site index (by type name)
    /// and the invocation index (by method name) the Factory and Upstream routes read.
    /// </summary>
    private sealed class TreeScan
    {
        private readonly Func<string, string> _read;
        private readonly Dictionary<string, string> _code;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SourceModel> _models = new(StringComparer.Ordinal);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<(string Path, int Offset)>> _mentions = new(StringComparer.Ordinal);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>> _closures = new(StringComparer.Ordinal);
        private List<string>? _governance;

        private TreeScan(
            string root,
            List<string> files,
            Func<string, string> read,
            Dictionary<string, ProjectInfo> projects)
        {
            Root = root;
            Files = files;
            _read = read;
            Projects = projects;
            _code = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var relative in files)
            {
                var model = new SourceModel(relative, read(relative));
                _code[relative] = model.Code;
                Occurrences.AddRange(Scanner.Classify(model));
                HttpDefaultsBindings.AddRange(Scanner.HttpDefaultsBindings(model));
                SiteIdLiterals.AddRange(Scanner.SiteIdLiterals(model));
                var (declarations, calls) = Scanner.GuardInstalls(model);
                GuardDeclarations.AddRange(declarations.Select(d => (relative, model.LineOf(d))));
                GuardCalls.AddRange(calls.Select(c => (relative, model.LineOf(c))));
                RailProblems.AddRange(Scanner.RailProblems(model, calls));
                FactoryImplementations.AddRange(Scanner.FactoryImplementations(model));
            }
        }

        public string Root { get; }

        public List<string> Files { get; }

        public List<Occurrence> Occurrences { get; } = [];

        public List<(string Path, int Line, bool BindsHandler)> HttpDefaultsBindings { get; } = [];

        public List<(string Path, int Line, string Id)> SiteIdLiterals { get; } = [];

        /// <summary>Declarations of <c>AddAshlarEgressGuard(</c>: a match preceded by a type (F4 B).</summary>
        public List<(string Path, int Line)> GuardDeclarations { get; } = [];

        /// <summary>Every other <c>AddAshlarEgressGuard(</c> match: a call.</summary>
        public List<(string Path, int Line)> GuardCalls { get; } = [];

        /// <summary>F4 (E): each call that covers no registration under D1.</summary>
        public List<string> RailProblems { get; } = [];

        /// <summary>F4 (D): each production type implementing, or direct registration of, IHttpClientFactory.</summary>
        public List<string> FactoryImplementations { get; } = [];

        /// <summary>The production csproj files of the population, by repo-relative path.</summary>
        public Dictionary<string, ProjectInfo> Projects { get; }

        public int ExaminedOccurrences => Occurrences.Count;

        /// <summary>The full F5 list, computed once per scan: Governance (F3) reads it for every governed row.</summary>
        public List<string> Governance => _governance ??= GovernanceProblems(this);

        public static TreeScan Load(string root)
        {
            var files = new List<string>();
            var csprojs = new List<string>();
            foreach (var top in ProductionRoots)
            {
                var dir = Path.Combine(root, top);
                if (Directory.Exists(dir) && !IsPruned(dir))
                    Collect(root, dir, files, csprojs);
            }

            files.Sort(StringComparer.Ordinal);
            var projects = csprojs.ToDictionary(
                p => p,
                p => ParseProject(p, File.ReadAllText(Path.Combine(root, p))),
                StringComparer.Ordinal);
            return new TreeScan(root, files, relative => File.ReadAllText(Path.Combine(root, relative)), projects);
        }

        /// <summary>A scan of in-memory sources and csproj texts, for the route controls. Every file is production.</summary>
        public static TreeScan FromSources(IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> csprojs)
        {
            var list = files.Keys.OrderBy(f => f, StringComparer.Ordinal).ToList();
            var projects = csprojs.ToDictionary(p => p.Key, p => ParseProject(p.Key, p.Value), StringComparer.Ordinal);
            return new TreeScan(string.Empty, list, relative => files[relative], projects);
        }

        /// <summary>The cleaned code of a scanned file, or null when the file is not in the population.</summary>
        public string? Code(string path) => _code.TryGetValue(path, out var code) ? code : null;

        /// <summary>The model of a scanned file, built on first use, or null when the file is not in the population.</summary>
        public SourceModel? Model(string path) =>
            _code.ContainsKey(path) ? _models.GetOrAdd(path, p => new SourceModel(p, _read(p))) : null;

        /// <summary>Every word-boundary mention of <paramref name="word"/> in cleaned production code, in file then offset order.</summary>
        public IReadOnlyList<(string Path, int Offset)> Mentions(string word) =>
            _mentions.GetOrAdd(word, w =>
            {
                var rx = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(w) + @"(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
                var found = new List<(string, int)>();
                foreach (var path in Files)
                {
                    var code = _code[path];
                    if (!code.Contains(w, StringComparison.Ordinal))
                        continue;
                    foreach (Match m in rx.Matches(code))
                        found.Add((path, m.Index));
                }

                return found;
            });

        /// <summary>The nearest csproj at or above the file's directory, or null.</summary>
        public string? ProjectOf(string path)
        {
            var dir = path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;
            while (true)
            {
                var prefix = dir.Length == 0 ? string.Empty : dir + "/";
                var here = Projects.Keys
                    .Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && !p[prefix.Length..].Contains('/', StringComparison.Ordinal))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (here is not null)
                    return here;
                if (dir.Length == 0)
                    return null;
                dir = dir.Contains('/', StringComparison.Ordinal) ? dir[..dir.LastIndexOf('/')] : string.Empty;
            }
        }

        /// <summary>Every project <paramref name="project"/> reaches through ProjectReference, transitively, itself excluded.</summary>
        public HashSet<string> Closure(string project) =>
            _closures.GetOrAdd(project, start =>
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var stack = new Stack<string>();
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    if (!Projects.TryGetValue(current, out var info))
                        continue;
                    foreach (var reference in info.References)
                    {
                        if (seen.Add(reference))
                            stack.Push(reference);
                    }
                }

                seen.Remove(start);
                return seen;
            });

        private static readonly Regex ProjectReference = new(
            @"<ProjectReference\b[^>]*?\bInclude\s*=\s*""([^""]+)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex ExeSdk = new(@"<Project\b[^>]*\bSdk\s*=\s*""[^""]*\.(?:Web|Worker)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex ExeOutputType = new(@"<OutputType>\s*(?:Exe|WinExe)\s*</OutputType>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// IsExe: an Sdk ending <c>.Web</c> or <c>.Worker</c>, or OutputType Exe/WinExe. IsTest: the csproj rule of
        /// <see cref="IsTestProjectRoot"/>. References: each ProjectReference Include, resolved against the csproj's
        /// directory to a repo-relative path. Conditions are ignored, so the closure over-approximates, which only
        /// makes the Upstream checks stricter.
        /// </summary>
        private static ProjectInfo ParseProject(string path, string text)
        {
            var dir = path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;
            var references = ProjectReference.Matches(text)
                .Select(m => ResolveRelative(dir, m.Groups[1].Value))
                .Where(r => r is not null)
                .Select(r => r!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var isTest = text.Contains("<IsTestProject>true<", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal);
            return new ProjectInfo(path, ExeSdk.IsMatch(text) || ExeOutputType.IsMatch(text), isTest, references);
        }

        private static string? ResolveRelative(string dir, string include)
        {
            var parts = new List<string>(dir.Length == 0 ? [] : dir.Split('/'));
            foreach (var segment in include.Replace('\\', '/').Split('/'))
            {
                if (segment.Length == 0 || segment == ".")
                    continue;
                if (segment == "..")
                {
                    if (parts.Count == 0)
                        return null;
                    parts.RemoveAt(parts.Count - 1);
                }
                else
                {
                    parts.Add(segment);
                }
            }

            return string.Join('/', parts);
        }

        private static void Collect(string root, string directory, List<string> files, List<string> csprojs)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
                files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            foreach (var csproj in Directory.EnumerateFiles(directory, "*.csproj"))
                csprojs.Add(Path.GetRelativePath(root, csproj).Replace('\\', '/'));

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!IsPruned(child))
                    Collect(root, child, files, csprojs);
            }
        }
    }

    /// <summary>
    /// Build output, agent scratch space and every dot-directory; any directory holding a <c>.git</c> file or
    /// directory (a worktree or nested clone, which would otherwise be scanned twice on a developer machine);
    /// and a TEST PROJECT, identified by its csproj (see <see cref="IsTestProjectRoot"/>), never by its name.
    /// </summary>
    private static bool IsPruned(string directory)
    {
        var name = Path.GetFileName(directory);
        if (name is "bin" or "obj" or ".claude" || name.StartsWith('.'))
            return true;

        var git = Path.Combine(directory, ".git");
        if (File.Exists(git) || Directory.Exists(git))
            return true;

        return IsTestProjectRoot(directory);
    }

    /// <summary>
    /// A test project root: a csproj that says <c>&lt;IsTestProject&gt;true&lt;</c> or references
    /// <c>Microsoft.NET.Test.Sdk</c>, exactly as <c>UnstableHashKeyConventionTests.IsTestProjectRoot</c> decides
    /// it. Never by name: a name rule once hid 59 production files from a gate, and a substring filter in this
    /// test's own design pass dropped <c>RemoteExecutionPlatform.cs</c> (it lives under <c>Testing/</c>).
    /// <c>Ashlar.Agents.TestKit</c> says <c>&lt;IsTestProject&gt;false&lt;</c> and is shipped, so it is scanned.
    /// </summary>
    internal static bool IsTestProjectRoot(string directory)
    {
        foreach (var csproj in Directory.EnumerateFiles(directory, "*.csproj"))
        {
            var text = File.ReadAllText(csproj);
            if (text.Contains("<IsTestProject>true<", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A brace pair in the cleaned text, its parent, and whether it is a namespace or type body.</summary>
    private sealed record Block(int Open, int Close, int Parent, bool IsTypeBody, int HeaderStart);

    /// <summary>
    /// One source file, cleaned (comments and literal contents blanked, offsets kept), with its block tree.
    /// Every marker runs on <see cref="Code"/>, never on <see cref="Raw"/>.
    /// </summary>
    private sealed class SourceModel
    {
        private static readonly Regex FileScopedNamespace = new(
            @"(?m)^[ \t]*namespace\s+@?[A-Za-z_][A-Za-z0-9_.]*\s*;", RegexOptions.CultureInvariant);

        private readonly int[] _lineStarts;

        public SourceModel(string path, string raw)
        {
            Path = path;
            Raw = raw;
            Literals = new Dictionary<int, string>();
            Code = Scanner.Clean(raw, Literals);
            _lineStarts = LineStarts(Code);
            Blocks = BuildBlocks(Code);
            RootIsCode = !FileScopedNamespace.IsMatch(Code);
        }

        public string Path { get; }

        public string Raw { get; }

        public string Code { get; }

        /// <summary>Each literal's original text, by the offset of its first character (Clean records them).</summary>
        public Dictionary<int, string> Literals { get; }

        /// <summary>In order of their opening brace, so the last one containing an offset is the innermost.</summary>
        public List<Block> Blocks { get; }

        /// <summary>
        /// True unless the file declares a file-scoped namespace. The file root then holds top-level statements
        /// (a top-level program) or only usings and type declarations, so treating it as code is safe: no
        /// statement can sit at the root of a file that is not a top-level program.
        /// </summary>
        public bool RootIsCode { get; }

        public int LineOf(int offset)
        {
            var index = Array.BinarySearch(_lineStarts, offset);
            return index >= 0 ? index + 1 : ~index;
        }

        /// <summary>The innermost block containing <paramref name="offset"/>, or -1 for the file root.</summary>
        public int Innermost(int offset)
        {
            var best = -1;
            for (var b = 0; b < Blocks.Count; b++)
            {
                var block = Blocks[b];
                if (block.Open >= offset)
                    break;
                if (offset < block.Close)
                    best = b;
            }

            return best;
        }

        /// <summary>Statements can run here: any block but a namespace or type body; the root when <see cref="RootIsCode"/>.</summary>
        public bool IsCodeBlock(int block) => block < 0 ? RootIsCode : !Blocks[block].IsTypeBody;

        /// <summary>True when <paramref name="ancestor"/> is <paramref name="block"/> or encloses it. The root encloses everything.</summary>
        public bool IsAncestorOrSelf(int ancestor, int block)
        {
            if (ancestor < 0)
                return true;
            for (var b = block; b >= 0; b = Blocks[b].Parent)
            {
                if (b == ancestor)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The member that holds <paramref name="offset"/>, header included: the outermost enclosing block below a
        /// type body, from its declaration to its closing brace. An occurrence directly in a type body (a field
        /// initializer, an expression-bodied member) is its declaration up to the depth-0 ';'. In a top-level
        /// program, the file is one member.
        /// </summary>
        public (int Start, int End) Member(int offset)
        {
            var candidate = -1;
            for (var b = Innermost(offset); b >= 0; b = Blocks[b].Parent)
            {
                if (Blocks[b].IsTypeBody)
                    return candidate >= 0 ? Span(candidate) : (Scanner.StatementStart(Code, offset), Scanner.StatementEnd(Code, offset));
                candidate = b;
            }

            if (!RootIsCode)
                return candidate >= 0 ? Span(candidate) : (Scanner.StatementStart(Code, offset), Scanner.StatementEnd(Code, offset));
            return (0, Code.Length);
        }

        private (int Start, int End) Span(int block) => (Blocks[block].HeaderStart, Math.Min(Blocks[block].Close + 1, Code.Length));

        private static int[] LineStarts(string code)
        {
            var starts = new List<int> { 0 };
            for (var k = 0; k < code.Length; k++)
            {
                if (code[k] == '\n')
                    starts.Add(k + 1);
            }

            return [.. starts];
        }

        private static List<Block> BuildBlocks(string code)
        {
            var blocks = new List<Block>();
            var open = new Stack<int>();
            for (var k = 0; k < code.Length; k++)
            {
                if (code[k] == '{')
                {
                    var headerStart = Scanner.StatementStart(code, k);
                    blocks.Add(new Block(k, code.Length, open.Count > 0 ? open.Peek() : -1, IsTypeHeader(code[headerStart..k]), headerStart));
                    open.Push(blocks.Count - 1);
                }
                else if (code[k] == '}' && open.Count > 0)
                {
                    var index = open.Pop();
                    blocks[index] = blocks[index] with { Close = k };
                }
            }

            return blocks;
        }

        private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal)
        {
            "class", "struct", "interface", "enum", "record", "namespace",
        };

        private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
        {
            "public", "private", "protected", "internal", "static", "sealed", "abstract", "partial", "readonly",
            "ref", "unsafe", "new", "file", "extern", "required", "virtual", "override",
        };

        /// <summary>
        /// A namespace or type body: after attributes and modifiers, the header's first word is a type or
        /// namespace keyword. Positional on purpose, so <c>foreach (var record in records)</c> and
        /// <c>where T : class</c> are code. Preprocessor lines are dropped first (<c>#region Private class helpers</c>).
        /// </summary>
        private static bool IsTypeHeader(string header)
        {
            var text = string.Join('\n', header.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
            var k = 0;
            while (true)
            {
                while (k < text.Length && char.IsWhiteSpace(text[k]))
                    k++;
                if (k >= text.Length)
                    return false;
                if (text[k] == '[')
                {
                    var depth = 0;
                    for (; k < text.Length; k++)
                    {
                        if (text[k] == '[')
                        {
                            depth++;
                        }
                        else if (text[k] == ']' && --depth == 0)
                        {
                            k++;
                            break;
                        }
                    }

                    continue;
                }

                var start = k;
                while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '_'))
                    k++;
                if (k == start)
                    return false;
                var word = text[start..k];
                if (TypeKeywords.Contains(word))
                    return true;
                if (!Modifiers.Contains(word))
                    return false;
            }
        }
    }

    /// <summary>The classifier: markers, G2, G2-file, G3, the stored-client rule and D1, over one <see cref="SourceModel"/>.</summary>
    private static class Scanner
    {
        private const RegexOptions Rx = RegexOptions.CultureInvariant;

        /// <summary>Optional <c>global::System.Net.Http.</c> qualifier.</summary>
        private const string HttpNs = @"(?:global::)?(?:System\s*\.\s*Net\s*\.\s*Http\s*\.\s*)?";

        /// <summary>Optional <c>global::System.Diagnostics.</c> qualifier.</summary>
        private const string DiagnosticsNs = @"(?:global::)?(?:System\s*\.\s*Diagnostics\s*\.\s*)?";

        /// <summary>Optional <c>global::System.Net.Sockets.</c> / <c>.WebSockets.</c> qualifier.</summary>
        private const string SocketsNs = @"(?:global::)?(?:System\s*\.\s*Net\s*\.\s*(?:Sockets|WebSockets)\s*\.\s*)?";

        /// <summary>Any dotted qualifier (<c>Amazon.BedrockRuntime.</c>, <c>ModelContextProtocol.Client.</c>).</summary>
        private const string AnyNs = @"(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*";

        /// <summary>http.new: a raw client, handler or invoker. "then ( or {" — an initializer on the next line counts.</summary>
        internal static readonly Regex RawHttpConstruction = new(
            @"\bnew\s+" + HttpNs + @"(?:HttpClient|HttpClientHandler|SocketsHttpHandler|WinHttpHandler)\s*[({]"
            + @"|\bnew\s+" + HttpNs + @"HttpMessageInvoker\s*\(",
            Rx);

        /// <summary>http.new, target-typed: <c>HttpClient[?] id = new(</c>. Group 1 is the <c>new</c>.</summary>
        private static readonly Regex TargetTypedHttpConstruction = new(
            @"\b" + HttpNs + @"(?:HttpClient|HttpClientHandler|SocketsHttpHandler|WinHttpHandler|HttpMessageInvoker)\s*\??\s+@?[A-Za-z_][A-Za-z0-9_]*\s*=\s*(new)\s*\(",
            Rx);

        /// <summary>http.new, guarded form: the sanctioned constructors. Each is one guarded (Wrapped) occurrence.</summary>
        private static readonly Regex EgressHttpFactoryCall = new(@"\bEgressHttp\s*\.\s*(?:CreateClient|Wrap)\s*\(", Rx);

        /// <summary>
        /// http.param candidates: <c>HttpClient[?] id</c> right after a '(' or ',' (attributes and this/in/ref/out
        /// allowed). Group 1 is the type name. Only a declaration's list counts; <see cref="IsParameterList"/> decides.
        /// </summary>
        internal static readonly Regex HttpClientParameter = new(
            @"(?<=[(,]\s*(?:\[[^\[\]]*\]\s*)*(?:(?:this|in|ref|out|params|scoped)\s+)?)" + HttpNs
            + @"(HttpClient)\s*\??\s+@?[A-Za-z_][A-Za-z0-9_]*\s*(?=[,)=])",
            Rx);

        /// <summary>Words before a '(' that make the parenthesis a statement or an operator, not a parameter list or a tuple type.</summary>
        private static readonly HashSet<string> NotAParameterList = new(StringComparer.Ordinal)
        {
            "using", "for", "foreach", "fixed", "lock", "while", "if", "switch", "catch", "return", "await",
            "typeof", "nameof", "sizeof", "default", "when", "is",
        };

        internal static readonly Regex HttpClientRegistration = new(@"\.\s*AddHttpClient\s*[(<]", Rx);

        private static readonly Regex AddAshlarEgressGuardCall = new(@"\bAddAshlarEgressGuard\s*\(", Rx);

        private static readonly Regex SdkClient = new(
            @"\bGrpcChannel\s*\.\s*ForAddress\s*\("
            + @"|\bnew\s+" + AnyNs + @"(?:HttpClientTransport|SseClientTransport|A2AClient|A2ACardResolver)\s*\("
            + @"|\bMcpClient\s*\.\s*CreateAsync\s*\("
            + @"|\bnew\s+" + AnyNs + @"Amazon[A-Za-z0-9_]*Client\s*\("
            + @"|\.\s*AsIChatClient\s*\(",
            Rx);

        private static readonly Regex McpTransportConstruction = new(@"\bnew\s+" + AnyNs + @"(?:HttpClientTransport|SseClientTransport)\s*\(", Rx);

        private static readonly Regex PassesHandlerOrClient = new(
            @"\bEgressHttp\s*\.\s*[A-Za-z_][A-Za-z0-9_]*\s*\(|\bHttpHandler\s*=(?![=>])|\bHttpClient\s*=(?![=>])", Rx);

        private static readonly Regex SocketPrimitive = new(
            @"\bnew\s+" + SocketsNs + @"(?:UdpClient|TcpClient|Socket|ClientWebSocket)\s*\("
            + @"|\b(?:System\s*\.\s*Net\s*\.\s*)?Dns\s*\.\s*(?:GetHostAddresses|GetHostEntry)(?:Async)?\s*\(",
            Rx);

        private static readonly Regex ProcessPrimitive = new(
            @"\bnew\s+" + DiagnosticsNs + @"ProcessStartInfo\s*[({]"
            + @"|\bProcess\s*\.\s*Start\s*\("
            + @"|\bnew\s+" + DiagnosticsNs + @"Process\s*[({]",
            Rx);

        private static readonly Regex DoorPrimitive = new(
            @"\bMeshStore\s*\.\s*Publish\s*\(|\bExtensionPackaging\s*\.\s*Pack\s*\(|\bResults\s*\.\s*(?:Stream|File)\s*\(", Rx);

        /// <summary>
        /// Door members: methods that write data out of the process with no primitive this scan can see, so
        /// their BODY must hold a guard call (in the body block itself, not a nested one).
        /// </summary>
        internal static readonly (string Type, string Member)[] DoorMembers =
        [
            ("NativeBundle", "StageApp"),
            ("SneakernetTransport", "ExportAsync"),
            ("FileBasedSharedAdaptationStore", "BroadcastAsync"),
        ];

        private static readonly Regex TelemetryExporter = new(@"\.\s*AddOtlpExporter\s*\(", Rx);

        private static readonly Regex StoreClient = new(@"\bnew\s+" + AnyNs + @"(?:NpgsqlConnection|DockerClientConfiguration)\s*\(", Rx);

        internal static readonly Regex ChatRegistration = new(
            @"\.\s*(?:AddKeyedChatClient|AddChatClient|AddEmbeddingGenerator|AddKeyedEmbeddingGenerator)\s*[(<]"
            + @"|\bnew\s+" + AnyNs + @"(?:OllamaHttpChatClient|LlamaSharpChatClient)\s*\(",
            Rx);

        /// <summary>
        /// banned: registering or resolving a bare <c>HttpClient</c> — <c>[Try]Add…&lt;HttpClient&gt;</c>,
        /// <c>typeof(HttpClient)</c>, <c>Get[Required][Keyed]Service[s]&lt;HttpClient&gt;</c> — which hands out a
        /// client no factory default ever touches.
        /// </summary>
        private static readonly Regex BannedRegistration = new(
            @"\b(?:Try)?Add[A-Za-z0-9_]*\s*<\s*" + HttpNs + @"HttpClient\s*>|\btypeof\s*\(\s*" + HttpNs + @"HttpClient\s*\)"
            + @"|\bGet(?:Required)?(?:Keyed)?Services?\s*<\s*" + HttpNs + @"HttpClient\s*>",
            Rx);

        /// <summary>The guard call G3 recognises: <c>….Evaluate(new EgressRequest(</c>.</summary>
        internal static readonly Regex GuardCall = new(@"\.\s*Evaluate\s*\(\s*new\s+" + AnyNs + @"EgressRequest\s*\(", Rx);

        private static readonly Regex SiteIdLiteral = new(@"^@?""EG-[A-Z]+-\d+""$", Rx);

        private static readonly Regex Whitespace = new(@"\s+", Rx);

        public static List<Occurrence> Classify(SourceModel m)
        {
            var code = m.Code;
            var found = new List<Occurrence>();
            void Add(string marker, int offset, int length, GuardKind guard) =>
                found.Add(new Occurrence(m.Path, marker, m.LineOf(offset), offset, Whitespace.Replace(code.Substring(offset, length), " ").Trim(), guard));

            var guards = GuardCall.Matches(code).Select(g => g.Index).ToList();
            var factoryCalls = EgressHttpFactoryCall.Matches(code)
                .Select(c => (At: c.Index, Open: c.Index + c.Length - 1, Close: ClosingParen(code, c.Index + c.Length - 1), Length: c.Length))
                .ToList();

            // ── http.new ─────────────────────────────────────────────────────────────────────────────
            foreach (var call in factoryCalls)
                Add(Marker.HttpNew, call.At, call.Length, GuardKind.Wrapped);

            var raw = RawHttpConstruction.Matches(code).Select(r => (At: r.Index, r.Length))
                .Concat(TargetTypedHttpConstruction.Matches(code).Select(r => (At: r.Groups[1].Index, Length: r.Index + r.Length - r.Groups[1].Index)));
            var unguardedHttpNew = 0;
            foreach (var (at, length) in raw)
            {
                var wrapped = factoryCalls.Any(c => c.Open < at && at < c.Close) || StoredInALocalWrappedLater(m, at, factoryCalls);
                if (!wrapped)
                    unguardedHttpNew++;
                Add(Marker.HttpNew, at, length, wrapped ? GuardKind.Wrapped : GuardKind.None);
            }

            // ── http.param: Precedes through the stored-client rule ─────────────────────────────────
            foreach (Match p in HttpClientParameter.Matches(code))
            {
                if (IsParameterList(m, p.Index))
                {
                    var at = p.Groups[1].Index;
                    Add(Marker.HttpParam, at, p.Index + p.Length - at, StoredClientPrecedes(m, at, guards) ? GuardKind.Precedes : GuardKind.None);
                }
            }

            // ── http.register: Factory under D1, a later install call in the registration's own block ─
            var installs = GuardInstalls(m).Calls;
            foreach (Match r in HttpClientRegistration.Matches(code))
                Add(Marker.HttpRegister, r.Index, r.Length, InstalledAfter(m, installs, r.Index) ? GuardKind.Factory : GuardKind.None);

            // ── sdk.client: G2-file ──────────────────────────────────────────────────────────────────
            var transportsPass = McpTransportConstruction.Matches(code)
                .Select(t => PassesAClient(code, t.Index, t.Index + t.Length - 1))
                .ToList();
            foreach (Match s in SdkClient.Matches(code))
            {
                var open = s.Index + s.Length - 1;
                var passes = s.Value.Contains("McpClient", StringComparison.Ordinal)
                    ? transportsPass.Count > 0 && transportsPass.All(x => x)
                    : PassesAClient(code, s.Index, open);
                Add(Marker.SdkClient, s.Index, s.Length, passes && unguardedHttpNew == 0 ? GuardKind.Wrapped : GuardKind.None);
            }

            // ── G3 markers ───────────────────────────────────────────────────────────────────────────
            foreach (var (marker, pattern) in new[]
                     {
                         (Marker.Socket, SocketPrimitive), (Marker.Process, ProcessPrimitive),
                         (Marker.Door, DoorPrimitive), (Marker.Telemetry, TelemetryExporter),
                     })
            {
                foreach (Match g in pattern.Matches(code))
                    Add(marker, g.Index, g.Length, Precedes(m, guards, g.Index) ? GuardKind.Precedes : GuardKind.None);
            }

            foreach (var (type, member) in DoorMembers)
            {
                foreach (var (at, length, body) in MemberDeclarations(m, type, member))
                {
                    var guarded = body >= 0 && guards.Any(g => m.Innermost(g) == body);
                    Add(Marker.Door, at, length, guarded ? GuardKind.Precedes : GuardKind.None);
                }
            }

            // ── store, chat.register, banned: no guarded form ────────────────────────────────────────
            foreach (Match s in StoreClient.Matches(code))
                Add(Marker.Store, s.Index, s.Length, GuardKind.None);
            foreach (Match c in ChatRegistration.Matches(code))
                Add(Marker.ChatRegister, c.Index, c.Length, GuardKind.None);
            foreach (Match b in BannedRegistration.Matches(code))
                Add(Marker.Banned, b.Index, b.Length, GuardKind.None);

            found.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : string.CompareOrdinal(a.Marker, b.Marker));
            return found;
        }

        // ── AddAshlarEgressGuard: declarations, calls, D1 and the rail ──────────────────────────────────────

        /// <summary>
        /// Every <c>AddAshlarEgressGuard(</c> match, split: a DECLARATION is preceded by a type
        /// (<see cref="EndsAType"/>: <c>IServiceCollection AddAshlarEgressGuard(</c>); every other match is a call
        /// (<c>services.AddAshlarEgressGuard(</c>, <c>return AddAshlarEgressGuard(s)</c>).
        /// </summary>
        public static (List<int> Declarations, List<int> Calls) GuardInstalls(SourceModel m)
        {
            var declarations = new List<int>();
            var calls = new List<int>();
            foreach (Match g in AddAshlarEgressGuardCall.Matches(m.Code))
            {
                var j = SkipSpaceBack(m.Code, g.Index - 1);
                (j >= 0 && EndsAType(m.Code, j) ? declarations : calls).Add(g.Index);
            }

            return (declarations, calls);
        }

        /// <summary>
        /// D1: a call lies at a higher offset than the registration, its innermost block IS the registration's
        /// innermost block, and that block is a code block (the root of a top-level program counts). Not an
        /// enclosing block: a call there also runs on paths that register no client. Not before: above an
        /// unbraced early return it installs the guard on the disabled path. An early return BETWEEN the two is a
        /// known miss (F9).
        /// </summary>
        public static bool InstalledAfter(SourceModel m, List<int> installs, int registration)
        {
            var block = m.Innermost(registration);
            return m.IsCodeBlock(block) && installs.Any(g => g > registration && m.Innermost(g) == block);
        }

        /// <summary>F4 (E), the D1 rail: every call must cover a registration at a lower offset in its own code block.</summary>
        public static IEnumerable<string> RailProblems(SourceModel m, List<int> calls)
        {
            var registrations = HttpClientRegistration.Matches(m.Code).Select(r => r.Index).ToList();
            foreach (var call in calls)
            {
                var block = m.Innermost(call);
                if (!m.IsCodeBlock(block) || !registrations.Any(r => r < call && m.Innermost(r) == block))
                {
                    yield return $"{m.Path}:{m.LineOf(call)}: AddAshlarEgressGuard( covers no AddHttpClient; call it after a "
                        + "registration, as a statement of that registration's own block (D1)";
                }
            }
        }

        private static readonly Regex FactoryRegistration = new(
            @"\b(?:Try)?Add[A-Za-z0-9_]*\s*<\s*" + HttpNs + @"IHttpClientFactory\b|\btypeof\s*\(\s*" + HttpNs + @"IHttpClientFactory\s*\)", Rx);

        private static readonly Regex ImplementsFactory = new(@":[^{]*\b" + HttpNs + @"IHttpClientFactory\b", Rx);

        /// <summary>
        /// F4 (D): a production type whose base list names <c>IHttpClientFactory</c>, or a direct registration of it
        /// (<c>Add…&lt;IHttpClientFactory…&gt;</c>, <c>typeof(IHttpClientFactory)</c>). A custom factory is the one way
        /// an IHttpClientFactory-typed receiver can hand out a client the defaults never touched.
        /// </summary>
        public static IEnumerable<string> FactoryImplementations(SourceModel m)
        {
            foreach (var block in m.Blocks.Where(b => b.IsTypeBody))
            {
                var header = m.Code[block.HeaderStart..block.Open];
                if (TypeHeader.IsMatch(header) && ImplementsFactory.IsMatch(header))
                    yield return $"{m.Path}:{m.LineOf(block.HeaderStart + header.Length - header.TrimStart().Length)}: a production type implements IHttpClientFactory";
            }

            foreach (Match r in FactoryRegistration.Matches(m.Code))
                yield return $"{m.Path}:{m.LineOf(r.Index)}: '{Whitespace.Replace(r.Value, " ")}' registers IHttpClientFactory directly";
        }

        // ── http.param: the stored-client rule ──────────────────────────────────────────────────────────────

        /// <summary>A type declaration's keyword and name, in a block header.</summary>
        internal static readonly Regex TypeHeader = new(
            @"\b(?<kw>class|struct|record|interface)\s+(?:(?:class|struct)\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)", Rx);

        /// <summary>The type whose body holds a declaration: its body block, name, keyword and modifiers.</summary>
        internal sealed record TypeInfo(int Body, string Name, string Keyword, bool Sealed, bool Partial);

        /// <summary>
        /// The nearest enclosing type body of <paramref name="offset"/> (a class, struct, record or interface, not a
        /// namespace), or null when a namespace body or the file root comes first (a primary constructor's list).
        /// </summary>
        internal static TypeInfo? EnclosingType(SourceModel m, int offset)
        {
            for (var b = m.Innermost(offset); b >= 0; b = m.Blocks[b].Parent)
            {
                if (!m.Blocks[b].IsTypeBody)
                    continue;
                var header = m.Code[m.Blocks[b].HeaderStart..m.Blocks[b].Open];
                var t = TypeHeader.Match(header);
                if (!t.Success)
                    return null;
                return new TypeInfo(b, t.Groups["name"].Value, t.Groups["kw"].Value,
                    Regex.IsMatch(header, @"\b(?:sealed|static)\b"), Regex.IsMatch(header, @"\bpartial\b"));
            }

            return null;
        }

        /// <summary>The parameter name after an http.param type token (<c>HttpClient? name</c>).</summary>
        internal static string? ParameterName(string code, int typeAt)
        {
            var n = ParameterNameAfterType.Match(code, typeAt);
            return n.Success ? n.Groups[1].Value : null;
        }

        private static readonly Regex ParameterNameAfterType = new(
            @"\G(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*HttpClient\s*\??\s+@?([A-Za-z_][A-Za-z0-9_]*)", Rx);

        /// <summary>
        /// The end (exclusive) of the body of the declaration whose parameter list closes at <paramref name="close"/>,
        /// constructor initializer and generic constraints included: a block's closing brace, or an expression
        /// body's depth-0 ';'. -1 when the declaration has no body (an interface or abstract member, a delegate).
        /// </summary>
        internal static int DeclarationBodyEnd(SourceModel m, int close)
        {
            var code = m.Code;
            var depth = 0;
            for (var k = close + 1; k < code.Length; k++)
            {
                var c = code[k];
                if (c is '(' or '[')
                {
                    depth++;
                }
                else if (c is ')' or ']')
                {
                    depth--;
                }
                else if (depth == 0 && c == ';')
                {
                    return -1;
                }
                else if (depth == 0 && c == '{')
                {
                    var block = m.Blocks.FindIndex(b => b.Open == k);
                    return block < 0 ? -1 : Math.Min(m.Blocks[block].Close + 1, code.Length);
                }
                else if (depth == 0 && c == '=' && k + 1 < code.Length && code[k + 1] == '>')
                {
                    return StatementEnd(code, k);
                }
            }

            return -1;
        }

        /// <summary>A mention preceded (spaces skipped) by <c>nameof(</c>.</summary>
        internal static bool InNameof(string code, int at)
        {
            var j = SkipSpaceBack(code, at - 1);
            if (j < 0 || code[j] != '(')
                return false;
            j = SkipSpaceBack(code, j - 1);
            return j >= 5 && code.AsSpan(j - 5, 6).SequenceEqual("nameof") && (j < 6 || !IsIdentifierChar(code[j - 6]));
        }

        private static readonly Regex ConfigurationAccess = new(
            @"\G\s*\.\s*(?:DefaultRequestHeaders\b|(?:Timeout|BaseAddress|DefaultRequestVersion|DefaultVersionPolicy|MaxResponseContentBufferSize)\s*=(?![=>]))",
            Rx);

        private static readonly Regex ClosesRightAway = new(@"\G\s*\)", Rx);

        private static readonly Regex EndsTheAssignment = new(@"\G\s*(?:;|\?\?\s*throw\b)", Rx);

        /// <summary>
        /// The stored-client rule: an http.param parameter x of declaration D in type T is guarded (Precedes) when
        /// (a) T is not partial; (b) every mention of x from the close of D's parameter list to the end of D's body,
        /// constructor initializer included, is inside <c>nameof(</c>, the sole argument of <c>ThrowIfNull(</c>, the
        /// right-hand side of <c>h = x</c> or <c>h = x ?? throw …</c> where h (or <c>this.h</c>) is a field or property
        /// declared private in T, a configuration access (<c>x.DefaultRequestHeaders…</c>, or an assignment to
        /// <c>x.Timeout</c>, <c>.BaseAddress</c>, <c>.DefaultRequestVersion</c>, <c>.DefaultVersionPolicy</c>,
        /// <c>.MaxResponseContentBufferSize</c>), or G3-preceded by a guard call; (c) every other mention of each holder
        /// h in the file (<c>this.h</c> and <c>.h</c> included), apart from its declaration and D's assignment, is a
        /// configuration access or G3-preceded; and (d) at least one mention of x or of a holder is G3-preceded.
        /// Anything else — a send from a sibling member, the guard after the send, a protected holder, x handed to
        /// another object or to <c>base(x)</c>/<c>this(x)</c>, a holder exposed by a property,
        /// <c>_h ?? EgressHttp…</c> — leaves it unguarded.
        /// </summary>
        internal static bool StoredClientPrecedes(SourceModel m, int typeAt, List<int> guards)
        {
            var code = m.Code;
            var name = ParameterName(code, typeAt);
            var open = OpeningParenthesis(code, typeAt);
            if (name is null || open < 0)
                return false;
            var type = EnclosingType(m, open);
            if (type is null || type.Partial || type.Keyword == "interface")
                return false;
            var close = ClosingParen(code, open);
            var bodyEnd = DeclarationBodyEnd(m, close);
            if (bodyEnd < 0)
                return false;

            var preceded = false;
            var holders = new Dictionary<string, (int Declaration, HashSet<int> Assignments)>(StringComparer.Ordinal);
            var mention = WordMention(name, allowMemberAccess: false);
            for (var u = mention.Match(code, close); u.Success && u.Index < bodyEnd; u = u.NextMatch())
            {
                if (InNameof(code, u.Index) || IsSoleThrowIfNullArgument(code, u.Index, u.Length)
                    || ConfigurationAccess.Match(code, u.Index + u.Length).Success)
                {
                    continue;
                }

                if (StoredInPrivateHolder(m, type, u.Index, u.Length, out var holder, out var holderAt, out var declaration))
                {
                    if (!holders.TryGetValue(holder, out var seen))
                        holders[holder] = seen = (declaration, new HashSet<int>());
                    seen.Assignments.Add(holderAt);
                    continue;
                }

                if (!Precedes(m, guards, u.Index))
                    return false;
                preceded = true;
            }

            foreach (var (holder, (declaration, assignments)) in holders)
            {
                foreach (Match h in WordMention(holder, allowMemberAccess: true).Matches(code))
                {
                    if (h.Index == declaration || assignments.Contains(h.Index) || ConfigurationAccess.Match(code, h.Index + h.Length).Success)
                        continue;
                    if (!Precedes(m, guards, h.Index))
                        return false;
                    preceded = true;
                }
            }

            return preceded;
        }

        /// <summary>A word mention; with <paramref name="allowMemberAccess"/>, <c>.h</c> and <c>this.h</c> count too.</summary>
        internal static Regex WordMention(string name, bool allowMemberAccess) =>
            new((allowMemberAccess ? @"(?<![A-Za-z0-9_])" : @"(?<![A-Za-z0-9_.])") + "@?" + Regex.Escape(name) + @"(?![A-Za-z0-9_])", Rx);

        private static bool IsSoleThrowIfNullArgument(string code, int at, int length)
        {
            var j = SkipSpaceBack(code, at - 1);
            if (j < 0 || code[j] != '(' || !ClosesRightAway.Match(code, at + length).Success)
                return false;
            j = SkipSpaceBack(code, j - 1);
            var end = j + 1;
            while (j >= 0 && IsIdentifierChar(code[j]))
                j--;
            return code[(j + 1)..end] == "ThrowIfNull";
        }

        /// <summary>
        /// The mention at <paramref name="at"/> is the whole right-hand side of a statement <c>h = x;</c> or
        /// <c>h = x ?? throw …;</c>, where h (or <c>this.h</c>) is declared private (explicitly, or with no access
        /// modifier) directly in <paramref name="type"/>'s body. Out: h, the offset of h in the assignment, and the
        /// offset of h's name in its declaration.
        /// </summary>
        private static bool StoredInPrivateHolder(SourceModel m, TypeInfo type, int at, int length, out string holder, out int holderAt, out int declaration)
        {
            holder = string.Empty;
            holderAt = declaration = -1;
            var code = m.Code;
            if (!EndsTheAssignment.Match(code, at + length).Success)
                return false;
            var j = SkipSpaceBack(code, at - 1);
            if (j < 1 || code[j] != '=' || "=!<>+-*/%&|^?".Contains(code[j - 1], StringComparison.Ordinal))
                return false;
            j = SkipSpaceBack(code, j - 1);
            var end = j + 1;
            while (j >= 0 && IsIdentifierChar(code[j]))
                j--;
            var name = code[(j + 1)..end];
            if (name.Length == 0 || char.IsDigit(name[0]))
                return false;
            holderAt = j + 1;
            var k = SkipSpaceBack(code, j);
            if (k >= 0 && code[k] == '.')
            {
                k = SkipSpaceBack(code, k - 1);
                if (k < 3 || code.Substring(k - 3, 4) != "this" || (k >= 4 && IsIdentifierChar(code[k - 4])))
                    return false;
                k = SkipSpaceBack(code, k - 4);
            }

            if (!(k < 0 || code[k] is ';' or '{' or '}' || (code[k] == '>' && k >= 1 && code[k - 1] == '=')))
                return false;

            declaration = PrivateMemberDeclaration(m, type, name);
            holder = name;
            return declaration >= 0;
        }

        /// <summary>
        /// The offset of <paramref name="name"/>'s declaration as a field or property directly in the type body (a
        /// type before it; ';', '=', '{' or '=>' after it), when its modifiers make it private: <c>private</c> and not
        /// <c>protected</c>, or no access modifier at all. -1 otherwise.
        /// </summary>
        internal static int PrivateMemberDeclaration(SourceModel m, TypeInfo type, string name)
        {
            var code = m.Code;
            var body = m.Blocks[type.Body];
            var after = new Regex(@"\G\s*(?:;|=|\{)", Rx);
            for (var d = WordMention(name, allowMemberAccess: false).Match(code, body.Open); d.Success && d.Index < body.Close; d = d.NextMatch())
            {
                if (m.Innermost(d.Index) != type.Body || !after.Match(code, d.Index + d.Length).Success)
                    continue;
                var j = SkipSpaceBack(code, d.Index - 1);
                if (j < 0 || !EndsAType(code, j))
                    continue;
                var modifiers = code[StatementStart(code, d.Index)..d.Index];
                var isPrivate = Regex.IsMatch(modifiers, @"\bprivate\b") && !Regex.IsMatch(modifiers, @"\bprotected\b");
                var noAccess = !Regex.IsMatch(modifiers, @"\b(?:public|protected|internal|private)\b");
                return isPrivate || noAccess ? d.Index : -1;
            }

            return -1;
        }

        /// <summary>
        /// The depth-0 arguments of the list opened at <paramref name="open"/>, as [start, end) spans. Brackets,
        /// braces and generic type-argument lists (a '&lt;' right after a name whose '&gt;' closes before any operator)
        /// nest, so <c>new Dictionary&lt;string, int&gt;()</c> is one argument.
        /// </summary>
        internal static List<(int Start, int End)> SplitArguments(string code, int open)
        {
            var spans = new List<(int, int)>();
            var depth = 0;
            var start = open + 1;
            for (var k = open + 1; k < code.Length; k++)
            {
                var c = code[k];
                if (c is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (c is ')' or ']' or '}')
                {
                    if (depth == 0)
                    {
                        if (code[start..k].Trim().Length > 0 || spans.Count > 0)
                            spans.Add((start, k));
                        return spans;
                    }

                    depth--;
                }
                else if (c == '<' && IsIdentifierChar(code[SkipSpaceBack(code, k - 1)]) && TypeArgumentsEnd(code, k) > 0)
                {
                    k = TypeArgumentsEnd(code, k);
                }
                else if (c == ',' && depth == 0)
                {
                    spans.Add((start, k));
                    start = k + 1;
                }
            }

            return spans;
        }

        /// <summary>The '&gt;' closing a type-argument list opened at <paramref name="lt"/>, or -1 when it is an operator.</summary>
        private static int TypeArgumentsEnd(string code, int lt)
        {
            var depth = 0;
            for (var k = lt; k < code.Length; k++)
            {
                var c = code[k];
                if (c == '<')
                {
                    depth++;
                }
                else if (c == '>')
                {
                    if (--depth == 0)
                        return k;
                }
                else if (!(IsIdentifierChar(c) || char.IsWhiteSpace(c) || c is ',' or '.' or '?' or '[' or ']' or '(' or ')' or ':'))
                {
                    return -1;
                }
            }

            return -1;
        }

        /// <summary>Production uses of the F4 binding call: where, and whether its argument region builds the guard handler.</summary>
        public static IEnumerable<(string Path, int Line, bool BindsHandler)> HttpDefaultsBindings(SourceModel m)
        {
            var binding = new Regex(TokenPattern(HttpDefaultsBindingToken), Rx);
            var handler = new Regex(TokenPattern(HttpDefaultsHandlerToken), Rx);
            foreach (Match b in binding.Matches(m.Code))
            {
                var region = HttpDefaultsBindingToken.EndsWith('(')
                    ? m.Code[(b.Index + b.Length)..ClosingParen(m.Code, b.Index + b.Length - 1)]
                    : m.Code;
                yield return (m.Path, m.LineOf(b.Index), handler.IsMatch(region));
            }
        }

        /// <summary>Every <c>"EG-…"</c> literal, read from the RAW text Clean recorded (Clean blanks literal contents).</summary>
        public static IEnumerable<(string Path, int Line, string Id)> SiteIdLiterals(SourceModel m) =>
            m.Literals
                .Where(l => SiteIdLiteral.IsMatch(l.Value))
                .OrderBy(l => l.Key)
                .Select(l => (m.Path, m.LineOf(l.Key), l.Value.TrimStart('@').Trim('"')));

        /// <summary>A token constant spelled as a regex that tolerates whitespace around '.' and before '('.</summary>
        internal static string TokenPattern(string token)
        {
            var parts = token.TrimEnd('(').Split('.');
            var body = string.Join(@"\s*\.\s*", parts.Select(Regex.Escape));
            return (char.IsLetter(token[0]) ? @"\b" : string.Empty) + body + (token.EndsWith('(') ? @"\s*\(" : string.Empty);
        }

        /// <summary>
        /// G2-file, the per-call half: the argument region passes a client or handler — an <c>EgressHttp.</c>
        /// call, an <c>HttpHandler =</c> or <c>HttpClient =</c> property — or has enough depth-0 arguments to
        /// carry one (A2A: 2, <c>HttpClientTransport</c>: 3). The caller adds "and the file has no unguarded http.new".
        /// </summary>
        private static bool PassesAClient(string code, int at, int open)
        {
            var close = ClosingParen(code, open);
            var region = code[(open + 1)..close];
            if (PassesHandlerOrClient.IsMatch(region))
                return true;

            var head = code[at..open];
            if (head.Contains("A2AClient", StringComparison.Ordinal) || head.Contains("A2ACardResolver", StringComparison.Ordinal))
                return ArgumentCount(code, open + 1) >= 2;
            if (head.Contains("HttpClientTransport", StringComparison.Ordinal))
                return ArgumentCount(code, open + 1) >= 3;
            return false;
        }

        /// <summary>
        /// G3: a guard call at a LOWER offset whose innermost block is the primitive's block or encloses it, and
        /// is not a namespace or type body (the root counts only in a top-level program). A guard in a sibling
        /// member, a field initializer, a braced nested block (an if, loop, lambda or local function with its own
        /// braces), or after the primitive never counts. Blocks are brace pairs and nothing else, so a guard with
        /// no braces of its own counts for its enclosing block even when it may never run: the body of an
        /// unbraced if, else or loop, a sibling switch case, an expression-bodied lambda or local function.
        /// Those are pinned as known misses in F9.
        /// </summary>
        internal static bool Precedes(SourceModel m, List<int> guards, int primitive)
        {
            var block = m.Innermost(primitive);
            foreach (var g in guards)
            {
                if (g >= primitive)
                    break;
                var guardBlock = m.Innermost(g);
                if (m.IsCodeBlock(guardBlock) && m.IsAncestorOrSelf(guardBlock, block))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// G2, the local half. The construction must initialize a DECLARED LOCAL in a code block:
        /// <c>var h = new …</c> or <c>SocketsHttpHandler h = new() { … }</c>, with <c>var</c> or a type before the
        /// name (<see cref="DeclaredLocalName"/>). A field, a property, <c>this.x</c>, an object-initializer member,
        /// a field assigned in a constructor (<c>_raw = new …</c>) or any other bare <c>x = new …</c> is not a
        /// declaration, so no wrap anywhere launders it. Then every LATER mention of the local, up to the end of
        /// its block, must sit inside an <c>EgressHttp.Wrap(</c>/<c>CreateClient(</c> argument region or configure
        /// it (<c>h.Prop = …</c>, <c>h.A.B = …</c>), and at least one must be inside such a region. Any other mention
        /// keeps the raw handler reachable and the construction unguarded: returning it, passing it to an SDK
        /// (<c>HttpHandler = h</c>, which also fails that sdk.client through G2-file), calling a method on it, or
        /// reassigning it.
        /// </summary>
        private static bool StoredInALocalWrappedLater(SourceModel m, int at, List<(int At, int Open, int Close, int Length)> factoryCalls)
        {
            var code = m.Code;
            var name = DeclaredLocalName(code, at);
            if (name is null)
                return false;
            var block = m.Innermost(at);
            if (!m.IsCodeBlock(block))
                return false;

            var scopeEnd = block < 0 ? code.Length : m.Blocks[block].Close;
            var mention = new Regex(@"(?<![A-Za-z0-9_.])" + Regex.Escape(name) + @"(?![A-Za-z0-9_])", Rx);
            var wrapped = false;
            for (var u = mention.Match(code, StatementEnd(code, at)); u.Success && u.Index < scopeEnd; u = u.NextMatch())
            {
                if (factoryCalls.Any(c => c.Open < u.Index && u.Index < c.Close))
                    wrapped = true;
                else if (!ConfiguresTheLocal.Match(code, u.Index + u.Length).Success)
                    return false;
            }

            return wrapped;
        }

        /// <summary>Right after a mention of the local: a member assignment, <c>.Prop = </c> or <c>.A.B = </c> (not '==' or '=>').</summary>
        private static readonly Regex ConfiguresTheLocal = new(@"\G\s*(?:\.\s*[A-Za-z_][A-Za-z0-9_]*\s*)+=(?![=>])", Rx);

        /// <summary>
        /// The local a construction initializes: a plain '=' before it, a name before that, and before the name a
        /// type (<see cref="EndsAType"/>: <c>var</c>, <c>HttpClientHandler</c>, <c>SocketsHttpHandler?</c>). Null for
        /// a bare <c>x = new …</c> after a ';', '{' or '}' (a field or property assigned in a constructor),
        /// <c>this.x = new …</c>, <c>Prop = new …</c> in an object initializer (after '{' or ','), a compound
        /// assignment, <c>if (c) x = new …</c>, <c>else x = new …</c> and <c>=> x = new …</c>.
        /// </summary>
        private static string? DeclaredLocalName(string code, int at)
        {
            var j = SkipSpaceBack(code, at - 1);
            if (j < 1 || code[j] != '=' || "=!<>+-*/%&|^?".Contains(code[j - 1], StringComparison.Ordinal))
                return null;
            j = SkipSpaceBack(code, j - 1);
            var name = ReadNameBack(code, ref j);
            return name is not null && j >= 0 && EndsAType(code, j) ? name : null;
        }

        /// <summary>
        /// The '(' that opens the list holding <paramref name="at"/> is a DECLARATION's: a constructor, method,
        /// local function, delegate, operator or primary constructor. A name precedes the '(' (its type arguments
        /// skipped, and an explicit interface qualifier <c>IFoo.Bar(</c> allowed), the name is not a keyword, and
        /// before it sits a return type or modifier (<see cref="EndsAType"/>); or, for a constructor with no
        /// modifier, a ';', '{' or '}' inside a type body with the list followed by its body ('{', '=>' or ':').
        /// Not a declaration, so not counted: an invocation's arguments (<c>TryGetValue(k, out HttpClient? c)</c>,
        /// the name after '.', '=', '(', ',' or an expression keyword such as <c>return</c>), a deconstruction or a
        /// tuple type (<c>(HttpClient a, string b) = …</c>, <c>List&lt;(HttpClient C, string N)&gt;</c>: no name
        /// before the '('), a lambda's parameter list (no name, or <c>async</c>/<c>static</c>), and a statement's
        /// parenthesis (<c>using (HttpClient c = new …)</c>).
        /// </summary>
        private static bool IsParameterList(SourceModel m, int at)
        {
            var code = m.Code;
            var open = OpeningParenthesis(code, at);
            if (open < 0)
                return false;

            var j = SkipSpaceBack(code, open - 1);
            var name = ReadNameBack(code, ref j);
            if (name is null || NotAMemberName.Contains(name))
                return false;

            var qualified = false;
            while (j >= 0 && code[j] == '.')
            {
                qualified = true;
                j = SkipSpaceBack(code, j - 1);
                if (ReadNameBack(code, ref j) is null)
                    return false;
            }

            if (j < 0)
                return false;
            if (code[j] is ';' or '{' or '}')
            {
                var owner = m.Innermost(open);
                return !qualified && owner >= 0 && m.Blocks[owner].IsTypeBody && FollowedByABody(code, ClosingParen(code, open));
            }

            return EndsAType(code, j);
        }

        /// <summary>Words that cannot name a declared method or constructor: statement heads, modifiers, keywords.</summary>
        private static readonly HashSet<string> NotAMemberName = new(NotAParameterList.Concat(new[]
            {
                "public", "private", "protected", "internal", "static", "readonly", "sealed", "abstract", "virtual",
                "override", "extern", "unsafe", "async", "new", "const", "volatile", "this", "base", "throw", "else",
                "in", "out", "ref", "params", "scoped", "var", "and", "or", "not", "as", "do", "case", "yield", "goto",
            }), StringComparer.Ordinal);

        /// <summary>Words that, right before a name, make it an expression (an invocation, a pattern, an assignment), not a declaration.</summary>
        internal static readonly HashSet<string> NotATypeWord = new(StringComparer.Ordinal)
        {
            "return", "await", "new", "else", "do", "throw", "case", "in", "is", "as", "out", "ref", "when", "and",
            "or", "not", "async", "yield", "goto", "select", "where",
        };

        /// <summary>
        /// The character at <paramref name="k"/> (not whitespace) ends a type or a modifier, so the name after it is
        /// being declared: an identifier that is not an expression keyword (<c>void</c>, <c>Task</c>,
        /// <c>public</c>, <c>var</c>), a generic type's '>', a nullable type's '?' written against its type, an
        /// array type's or attribute's ']', or a tuple type's ')'. A cast's ')' and a statement head's ')'
        /// (<c>if (x) h = …</c>) are not, and neither is '=>'.
        /// </summary>
        internal static bool EndsAType(string code, int k)
        {
            var c = code[k];
            if (IsIdentifierChar(c))
            {
                var end = k + 1;
                while (k >= 0 && IsIdentifierChar(code[k]))
                    k--;
                return !NotATypeWord.Contains(code[(k + 1)..end]);
            }

            return c switch
            {
                ']' => true,
                '?' => k >= 1 && (IsIdentifierChar(code[k - 1]) || code[k - 1] is '>' or ']'),
                '>' => TypeArgumentsStart(code, k) >= 0,
                ')' => IsTupleType(code, k),
                _ => false,
            };
        }

        /// <summary>
        /// The identifier ending at <paramref name="j"/>, type arguments before it skipped (<c>Get&lt;T&gt;</c>);
        /// <paramref name="j"/> moves to the first non-space before it. Null when no identifier is there.
        /// </summary>
        internal static string? ReadNameBack(string code, ref int j)
        {
            if (j >= 0 && code[j] == '>')
            {
                j = TypeArgumentsStart(code, j);
                if (j < 0)
                    return null;
            }

            var end = j + 1;
            while (j >= 0 && IsIdentifierChar(code[j]))
                j--;
            var word = code[(j + 1)..end];
            j = SkipSpaceBack(code, j);
            return word.Length > 0 && !char.IsDigit(word[0]) ? word : null;
        }

        /// <summary>
        /// <paramref name="j"/> is a '>': the index of the generic name's last character before its matching '&lt;',
        /// or -1 when it is not a type-argument list ('=>', a comparison, a shift).
        /// </summary>
        private static int TypeArgumentsStart(string code, int j)
        {
            if (j >= 1 && code[j - 1] == '=')
                return -1;
            var depth = 0;
            for (; j >= 0; j--)
            {
                var c = code[j];
                if (c == '>')
                {
                    depth++;
                }
                else if (c == '<')
                {
                    if (--depth == 0)
                    {
                        var k = SkipSpaceBack(code, j - 1);
                        return k >= 0 && IsIdentifierChar(code[k]) ? k : -1;
                    }
                }
                else if (!(IsIdentifierChar(c) || char.IsWhiteSpace(c) || c is ',' or '.' or '?' or '[' or ']' or '(' or ')' or ':'))
                {
                    return -1;
                }
            }

            return -1;
        }

        /// <summary>
        /// <paramref name="close"/> is a ')' that ends a tuple type: its list has a depth-0 ',' (a cast never does)
        /// and no statement keyword opens it (<c>for (int i = 0, j = 0; …)</c>).
        /// </summary>
        private static bool IsTupleType(string code, int close)
        {
            var depth = 0;
            var open = -1;
            for (var k = close; k >= 0; k--)
            {
                if (code[k] == ')')
                {
                    depth++;
                }
                else if (code[k] == '(' && --depth == 0)
                {
                    open = k;
                    break;
                }
            }

            if (open < 0)
                return false;
            var w = SkipSpaceBack(code, open - 1);
            var end = w + 1;
            while (w >= 0 && IsIdentifierChar(code[w]))
                w--;
            if (NotAParameterList.Contains(code[(w + 1)..end]))
                return false;

            depth = 0;
            for (var k = open + 1; k < close; k++)
            {
                var c = code[k];
                if (c is '(' or '[' or '<')
                    depth++;
                else if (c is ')' or ']' or '>')
                    depth--;
                else if (c == ',' && depth == 0)
                    return true;
            }

            return false;
        }

        /// <summary>The '(' that opens the bracket holding <paramref name="at"/>, or -1 when that bracket is '[' or '{' or there is none.</summary>
        internal static int OpeningParenthesis(string code, int at)
        {
            var depth = 0;
            for (var j = at - 1; j >= 0; j--)
            {
                var ch = code[j];
                if (ch is ')' or ']' or '}')
                {
                    depth++;
                }
                else if (ch is '(' or '[' or '{')
                {
                    if (depth == 0)
                        return ch == '(' ? j : -1;
                    depth--;
                }
            }

            return -1;
        }

        /// <summary>After the ')' at <paramref name="close"/>: a declaration's body or constructor initializer ('{', '=>' or ':').</summary>
        private static bool FollowedByABody(string code, int close)
        {
            var k = close + 1;
            while (k < code.Length && char.IsWhiteSpace(code[k]))
                k++;
            return k < code.Length && (code[k] is '{' or ':' || (code[k] == '=' && k + 1 < code.Length && code[k + 1] == '>'));
        }

        internal static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        internal static int SkipSpaceForward(string code, int k)
        {
            while (k < code.Length && char.IsWhiteSpace(code[k]))
                k++;
            return k;
        }

        /// <summary>
        /// The '.' at <paramref name="dot"/> qualifies a DECLARED name, not a member access: an explicit interface
        /// implementation <c>Task&lt;bool&gt; IVerifier.VerifyAsync(</c>, where a type sits before the qualifier.
        /// </summary>
        internal static bool IsQualifiedDeclaration(string code, int dot)
        {
            var k = SkipSpaceBack(code, dot - 1);
            var qualifier = ReadNameBack(code, ref k);
            return qualifier is not null and not "this" and not "base" && k >= 0 && EndsAType(code, k);
        }

        internal static int SkipSpaceBack(string code, int j)
        {
            while (j >= 0 && char.IsWhiteSpace(code[j]))
                j--;
            return j;
        }

        /// <summary>
        /// Declarations of <paramref name="member"/> inside a type body whose header declares <paramref name="type"/>:
        /// the name's offset and length, and the body block (-1 for an expression body, which cannot hold a G3 guard).
        /// </summary>
        private static IEnumerable<(int At, int Length, int Body)> MemberDeclarations(SourceModel m, string type, string member)
        {
            var code = m.Code;
            var typeHeader = new Regex(@"\b(?:class|struct|record|interface)\s+" + Regex.Escape(type) + @"\b", Rx);
            var name = new Regex(@"\b" + Regex.Escape(member) + @"\s*(?:<[^<>()]*>)?\s*\(", Rx);
            foreach (Match n in name.Matches(code))
            {
                var j = n.Index - 1;
                while (j >= 0 && char.IsWhiteSpace(code[j]))
                    j--;
                if (j < 0 || !(char.IsLetterOrDigit(code[j]) || code[j] is '_' or '>' or ']' or '?'))
                    continue;

                var close = ClosingParen(code, n.Index + n.Length - 1);
                var k = close + 1;
                while (k < code.Length && char.IsWhiteSpace(code[k]))
                    k++;
                var block = k < code.Length && code[k] == '{';
                var arrow = k + 1 < code.Length && code[k] == '=' && code[k + 1] == '>';
                if (!block && !arrow)
                    continue;

                var owner = m.Innermost(n.Index);
                if (owner < 0 || !m.Blocks[owner].IsTypeBody
                    || !typeHeader.IsMatch(code[m.Blocks[owner].HeaderStart..m.Blocks[owner].Open]))
                {
                    continue;
                }

                yield return (n.Index, member.Length, block ? m.Blocks.FindIndex(b => b.Open == k) : -1);
            }
        }

        /// <summary>After the previous ';', '{' or '}': where the statement or declaration holding <paramref name="at"/> starts.</summary>
        internal static int StatementStart(string code, int at)
        {
            var j = at - 1;
            while (j >= 0 && code[j] is not (';' or '{' or '}'))
                j--;
            return j + 1;
        }

        /// <summary>Just past the depth-0 ';' that ends the statement holding <paramref name="at"/>, or at a bracket it never opened.</summary>
        internal static int StatementEnd(string code, int at)
        {
            var depth = 0;
            for (var k = at; k < code.Length; k++)
            {
                var ch = code[k];
                if (ch is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (ch is ')' or ']' or '}')
                {
                    if (depth == 0)
                        return k;
                    depth--;
                }
                else if (ch == ';' && depth == 0)
                {
                    return k + 1;
                }
            }

            return code.Length;
        }

        /// <summary>The index of the ')' closing the '(' at <paramref name="open"/>, or the end of the code.</summary>
        internal static int ClosingParen(string code, int open)
        {
            var depth = 0;
            for (var k = open; k < code.Length; k++)
            {
                if (code[k] == '(')
                {
                    depth++;
                }
                else if (code[k] == ')')
                {
                    depth--;
                    if (depth == 0)
                        return k;
                }
            }

            return code.Length;
        }

        /// <summary>Depth-0 arguments from <paramref name="open"/> (just past the paren) to its match, as <c>GateRecordReadFunnelConventionTests.ArgumentCount</c>.</summary>
        internal static int ArgumentCount(string code, int open)
        {
            var depth = 0;
            var commas = 0;
            var any = false;
            for (var i = open; i < code.Length; i++)
            {
                var c = code[i];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    if (depth == 0)
                        return any ? commas + 1 : 0;
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    commas++;
                }
                else if (!char.IsWhiteSpace(c))
                {
                    any = true;
                }
            }

            return any ? commas + 1 : 0;
        }

        // ── Copied VERBATIM from PathContainmentConventionTests.Scanner.Clean and .Blank. Do not edit one ──
        // ── without the other: two scans that disagree about what a comment is disagree about everything. ──

        /// <summary>
        /// Blanks comments and the CONTENTS of string and char literals (delimiters and newlines kept,
        /// so offsets and line numbers survive), and records each literal's original text by the
        /// offset of its first character.
        /// </summary>
        internal static string Clean(string text, Dictionary<int, string> literals)
        {
            var buffer = text.ToCharArray();
            var n = text.Length;
            var i = 0;
            while (i < n)
            {
                var c = text[i];
                var next = i + 1 < n ? text[i + 1] : '\0';

                if (c == '/' && next == '/')
                {
                    var end = text.IndexOf('\n', i);
                    end = end < 0 ? n : end;
                    Blank(buffer, i, end);
                    i = end;
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    var end = close < 0 ? n : close + 2;
                    Blank(buffer, i, end);
                    i = end;
                    continue;
                }

                if (c == '\'')
                {
                    var k = i + 1;
                    while (k < n && text[k] != '\'' && text[k] != '\n')
                        k += text[k] == '\\' ? 2 : 1;
                    var end = Math.Min(k + 1, n);
                    literals[i] = text[i..end];
                    Blank(buffer, i + 1, end - 1);
                    i = end;
                    continue;
                }

                if (c == '"' || (c is '@' or '$' && next is '"' or '@' or '$'))
                {
                    var j = i;
                    var verbatim = false;
                    while (j < n && text[j] is '@' or '$')
                    {
                        verbatim |= text[j] == '@';
                        j++;
                    }

                    if (j >= n || text[j] != '"')
                    {
                        i++;
                        continue;
                    }

                    var q = j;
                    while (q < n && text[q] == '"')
                        q++;
                    var quotes = q - j;
                    if (quotes >= 3)
                    {
                        // Raw string literal: closed by the same run of quotes.
                        var close = text.IndexOf(new string('"', quotes), q, StringComparison.Ordinal);
                        var rawEnd = close < 0 ? n : close + quotes;
                        literals[i] = text[i..rawEnd];
                        Blank(buffer, q, close < 0 ? n : close);
                        i = rawEnd;
                        continue;
                    }

                    var k = j + 1;
                    while (k < n)
                    {
                        var ch = text[k];
                        if (verbatim)
                        {
                            if (ch == '"' && k + 1 < n && text[k + 1] == '"')
                            {
                                k += 2;
                                continue;
                            }

                            if (ch == '"')
                                break;
                        }
                        else
                        {
                            if (ch == '\\')
                            {
                                k += 2;
                                continue;
                            }

                            if (ch is '"' or '\n')
                                break;
                        }

                        k++;
                    }

                    var stringEnd = Math.Min(k + 1, n);
                    literals[i] = text[i..stringEnd];
                    Blank(buffer, j + 1, stringEnd - 1);
                    i = stringEnd;
                    continue;
                }

                i++;
            }

            return new string(buffer);
        }

        private static void Blank(char[] buffer, int from, int to)
        {
            for (var k = from; k < to && k < buffer.Length; k++)
            {
                if (buffer[k] != '\n')
                    buffer[k] = ' ';
            }
        }
    }
}
