using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// F3's reason checks: which <c>unguarded_reason</c> each marker allows, and the routes behind Factory,
/// <c>Upstream:&lt;path&gt;</c> and Governance; and the route controls, multi-file fixtures that prove each rule
/// holds or fails as declared.
/// </summary>
public sealed partial class EgressGuardConventionTests
{
    // A property, not a field: ExemptReasons lives in another part, and the order of static initializers across
    // partial declarations is unspecified.
    private static string ClosedExemptSet => "Exempt:<" + string.Join("|", ExemptReasons) + ">";

    /// <summary>The reasons a marker allows when something is unguarded, as F3 prints them.</summary>
    private static string AllowedReasons(string marker) => marker switch
    {
        Marker.HttpNew => $"{ClosedExemptSet}, {GuardImplReason} (only under {GuardImplFolder}), {GovernanceReason} (governed pairs only)",
        Marker.HttpParam => $"{FactoryReason}, {UpstreamPrefix}<path>, {GovernanceReason} (governed pairs only), {ClosedExemptSet}",
        Marker.HttpRegister => $"{UpstreamPrefix}<path>, Exempt:ConsumerSdk (only under {ConsumerSdkFolder})",
        Marker.SdkClient => $"{GovernanceReason} (governed pairs only), {ClosedExemptSet}",
        Marker.ChatRegister => $"{GovernanceReason} (governed pairs only)",
        _ => ClosedExemptSet,
    };

    /// <summary>
    /// The problems with one row's <c>unguarded_reason</c>, for a row with something unguarded: the reason must be
    /// one its marker allows, and its route must hold on this scan. Every message starts with the row's location.
    /// </summary>
    private static List<string> ReasonProblems(Pin pin, TreeScan scan, IReadOnlyList<Pin> pins, IReadOnlyDictionary<string, ObservedRow> observed)
    {
        var where = $"{InventoryRelativePath}:{pin.Line} {pin.Path} [{pin.Marker}]";
        return new RouteContext(scan, pins, observed).RowProblems(pin).Select(p => $"{where}: {p}").ToList();
    }

    /// <summary>A receiving member: the declaration whose parameter list holds an http.param occurrence.</summary>
    private sealed record Receiver(
        string Path,
        int Offset,
        string Param,
        int Position,
        int HttpClientParameters,
        string Member,
        bool IsConstructor,
        string TypeName,
        Scanner.TypeInfo? Type,
        bool Sealed,
        bool Partial,
        bool IsPrivate,
        bool IsExtension,
        int ListClose,
        int BodyEnd)
    {
        public string Key => Path + "@" + Offset;

        public string Name => IsConstructor ? TypeName + " constructor" : (TypeName.Length > 0 ? TypeName + "." : string.Empty) + Member;
    }

    /// <summary>One place that supplies a receiving member: a construction, an initializer or an invocation, with its argument list.</summary>
    private sealed record Site(string Path, int Offset, int Open, string Kind);

    /// <summary>
    /// One F3 evaluation: memoises each row's route and each receiving member's F-call proof, and marks the ones in
    /// progress so a cycle fails instead of recursing.
    /// </summary>
    private sealed class RouteContext(TreeScan scan, IReadOnlyList<Pin> pins, IReadOnlyDictionary<string, ObservedRow> observed)
    {
        private readonly Dictionary<string, List<string>?> _rows = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>?> _calls = new(StringComparer.Ordinal);

        private static readonly Regex NewBefore = new(@"\bnew\s+(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*$", RegexOptions.CultureInvariant);
        private static readonly Regex OpenAfter = new(@"\G\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex TargetTypedAfter = new(
            @"\G\s*(?:<[^<>()]*>)?\s*\??\s+@?[A-Za-z_][A-Za-z0-9_]*\s*=\s*new\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex CreateInstanceBefore = new(@"\bCreateInstance\s*<\s*(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*$", RegexOptions.CultureInvariant);
        private static readonly Regex TypeofBefore = new(@"\btypeof\s*\(\s*(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*$", RegexOptions.CultureInvariant);
        private static readonly Regex CloseAngleAfter = new(@"\G\s*>", RegexOptions.CultureInvariant);
        private static readonly Regex CloseParenAfter = new(@"\G\s*\)", RegexOptions.CultureInvariant);
        private static readonly Regex EmptyRegistrationBefore = new(
            @"(?:\b(?:Try)?Add(?:Singleton|Scoped|Transient)|\bServiceDescriptor\s*\.\s*(?:Singleton|Scoped|Transient))\s*<[^<>;(){}]*$", RegexOptions.CultureInvariant);
        private static readonly Regex EmptyRegistrationAfter = new(@"\G\s*>\s*\(\s*\)", RegexOptions.CultureInvariant);
        private static readonly Regex TypedRegistrationBefore = new(@"\.\s*AddHttpClient\s*<[^<>;(){}]*$", RegexOptions.CultureInvariant);
        private static readonly Regex TypedRegistrationAfter = new(@"\G\s*>\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex ThisInitializer = new(@":\s*this\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex CallAfter = new(@"\G\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex CreateClientCall = new(
            @"\G(?:this\s*\.\s*)?@?(?<r>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*CreateClient\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex BareCall = new(@"\G(?:this\s*\.\s*)?@?(?<n>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex Identifier = new(@"^@?([A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant);
        private static readonly Regex NamedArgument = new(@"^\s*@?([A-Za-z_][A-Za-z0-9_]*)\s*:(?!:)", RegexOptions.CultureInvariant);
        private static readonly Regex FactoryResolve = new(
            @"^[^;]*?\.\s*Get(?:Required)?Service\s*<\s*(?:global::)?(?:System\s*\.\s*Net\s*\.\s*Http\s*\.\s*)?IHttpClientFactory\s*>\s*\(\s*\)$",
            RegexOptions.CultureInvariant);
        private static readonly Regex Return = new(@"\breturn\b", RegexOptions.CultureInvariant);
        private static readonly Regex HttpClientMember = new(
            @"(?<![A-Za-z0-9_.])(?:global::)?(?:System\s*\.\s*Net\s*\.\s*Http\s*\.\s*)?HttpClient\s*\??\s+@?([A-Za-z_][A-Za-z0-9_]*)\s*(?=;|=|\{)",
            RegexOptions.CultureInvariant);

        /// <summary>Words that, before a name, never make it a declaration's type.</summary>
        private static readonly HashSet<string> NotADeclaringType = new(
            Scanner.NotATypeWord.Concat(["throw", "using", "lock", "typeof", "sizeof", "default", "nameof", "var"]), StringComparer.Ordinal);

        /// <summary>The route problems of one pinned row, memoised; a row met again while in progress is a cycle.</summary>
        public List<string> RowProblems(Pin pin)
        {
            if (_rows.TryGetValue(pin.Key, out var done))
                return done ?? [$"{pin.Path} [{pin.Marker}] is part of a route cycle: its route depends on itself"];

            _rows[pin.Key] = null;
            var problems = Evaluate(pin);
            _rows[pin.Key] = problems;
            return problems;
        }

        private List<string> Evaluate(Pin pin)
        {
            var reason = pin.Reason;
            var notAllowed = $"unguarded_reason '{reason}' is not allowed for {pin.Marker}; allowed: {AllowedReasons(pin.Marker)}. "
                + "`Unrouted` was removed by SPEC-007 PR 3b: route the site";

            if (reason.StartsWith("Exempt:", StringComparison.Ordinal))
                return ExemptProblems(pin, notAllowed);

            if (reason == FactoryReason)
            {
                return pin.Marker == Marker.HttpParam
                    ? FactoryRowProblems(pin)
                    : [$"Factory is for http.param (a parameter fed from IHttpClientFactory); a guarded {pin.Marker} is pinned in "
                        + $"guarded_by, and what is left is {AllowedReasons(pin.Marker)}"];
            }

            if (reason.StartsWith(UpstreamPrefix, StringComparison.Ordinal))
            {
                return pin.Marker switch
                {
                    Marker.HttpParam => UpstreamParamProblems(pin, reason[UpstreamPrefix.Length..]),
                    Marker.HttpRegister => UpstreamRegisterProblems(pin, reason[UpstreamPrefix.Length..]),
                    _ => [notAllowed],
                };
            }

            if (reason == GovernanceReason)
                return GovernanceRowProblems(pin, notAllowed);

            return [notAllowed];
        }

        private static List<string> ExemptProblems(Pin pin, string notAllowed)
        {
            var name = pin.Reason["Exempt:".Length..];
            if (pin.Reason == GuardImplReason)
            {
                return pin.Marker == Marker.HttpNew && pin.Path.StartsWith(GuardImplFolder, StringComparison.Ordinal)
                    ? []
                    : [$"{GuardImplReason} is accepted only for http.new under {GuardImplFolder}, where the guard builds the client it hands out"];
            }

            if (!ExemptReasons.Contains(name, StringComparer.Ordinal) || pin.Marker == Marker.ChatRegister
                || (pin.Marker == Marker.HttpRegister && name != "ConsumerSdk"))
            {
                return [notAllowed];
            }

            return name == "ConsumerSdk" && !pin.Path.StartsWith(ConsumerSdkFolder, StringComparison.Ordinal)
                ? [$"Exempt:ConsumerSdk only under {ConsumerSdkFolder} (the consumer SDK); route this site"]
                : [];
        }

        private List<string> GovernanceRowProblems(Pin pin, string notAllowed)
        {
            if (!GovernedPairs.Contains((pin.Path, pin.Marker)))
            {
                return [$"{notAllowed}. Governance is allowed only for the four governed MEAI pairs: "
                    + string.Join(", ", GovernedPairs.Select(p => $"{p.Path} [{p.Marker}]"))];
            }

            var f5 = scan.Governance;
            return f5.Count == 0 ? [] : ["Governance needs F5 green:\n    " + string.Join("\n    ", f5)];
        }

        // ── Factory ────────────────────────────────────────────────────────────────────────────────────────

        private List<string> FactoryRowProblems(Pin pin)
        {
            if (!observed.TryGetValue(pin.Key, out var row))
                return ["the scan sees no occurrence to route"];

            var problems = new List<string>();
            foreach (var o in row.Occurrences.Where(o => !o.Guarded))
            {
                var receiver = ReceiverAt(scan.Model(o.Path)!, o.Offset);
                problems.AddRange(CallRoute(receiver).Select(p => $"{o.Where} ({receiver.Name}, '{receiver.Param}'): {p}"));
            }

            return problems;
        }

        /// <summary>
        /// Factory for one receiving member, memoised (a member met again while in progress is a cycle): exactly one
        /// HttpClient parameter, a sealed and non-partial type for a constructor, no supplier the scan cannot follow,
        /// every typed registration Factory-classified, and then F-typed (no other supply site) or F-call (every site
        /// passes a factory client in the parameter's position).
        /// </summary>
        private List<string> CallRoute(Receiver r)
        {
            if (_calls.TryGetValue(r.Key, out var done))
                return done ?? [$"{r.Name} is fed through a cycle of parameters"];

            _calls[r.Key] = null;
            var problems = new List<string>();
            if (r.HttpClientParameters != 1)
                problems.Add($"{r.Name} declares {r.HttpClientParameters} HttpClient parameters; the Factory route needs exactly one");
            if (r.IsConstructor && !r.Sealed)
                problems.Add($"{r.TypeName} is not sealed: a derived type's base(…) call would be a supply site the scan cannot list");
            if (r.Partial)
                problems.Add($"{r.TypeName} is partial: another part could supply it from a file the scan does not tie to it");

            var (sites, unknown, typed) = SupplySites(r);
            problems.AddRange(unknown);
            foreach (var (path, line, factory) in typed.Where(t => !t.Factory))
                problems.Add($"{path}:{line}: AddHttpClient<…{r.TypeName}> is not Factory-classified; call AddAshlarEgressGuard after it in the same block");

            if (sites.Count == 0 && typed.Count == 0)
            {
                problems.Add($"nothing supplies {r.Name}: no construction, initializer or call in production");
            }
            else
            {
                foreach (var site in sites)
                {
                    var why = SiteProblem(r, site);
                    if (why is not null)
                        problems.Add($"{Where(site)} ({site.Kind}): {why}");
                }
            }

            if (problems.Count > 0 && sites.Count > 0)
                problems.Add($"supply sites of {r.Name}: {string.Join(", ", sites.Select(Where))}");

            _calls[r.Key] = problems;
            return problems;
        }

        private string Where(Site site) => $"{site.Path}:{scan.Model(site.Path)!.LineOf(site.Offset)}";

        /// <summary>Why the argument a site passes for the receiver's parameter is not a factory client; null when it is.</summary>
        private string? SiteProblem(Receiver r, Site site)
        {
            var m = scan.Model(site.Path)!;
            var code = m.Code;
            var arguments = Scanner.SplitArguments(code, site.Open);
            var position = r.IsExtension && site.Kind == ".N(…)" ? r.Position - 1 : r.Position;
            if (position < 0)
                return "the client is the extension receiver, which the route cannot follow";

            (int Start, int End)? argument = null;
            foreach (var a in arguments)
            {
                var named = NamedArgument.Match(code[a.Start..a.End]);
                if (named.Success && named.Groups[1].Value == r.Param)
                    argument = (a.Start + named.Length, a.End);
            }

            if (argument is null && position < arguments.Count)
            {
                var named = NamedArgument.Match(code[arguments[position].Start..arguments[position].End]);
                if (named.Success)
                    return $"argument {position} is named '{named.Groups[1].Value}', which the route cannot align with '{r.Param}'";
                argument = arguments[position];
            }

            if (argument is null)
                return $"passes no argument for '{r.Param}', so the parameter's default is used";

            return FactoryEvidence(m, site.Offset, argument.Value.Start, argument.Value.End);
        }

        /// <summary>
        /// F-call, for the expression [start, end) at a site: (i) <c>r.CreateClient(…)</c> on an IHttpClientFactory
        /// declared in the file; (ii) a local declared earlier in the same member, in the site's block or an
        /// enclosing one, as <c>var|HttpClient v = (i)|(iii)</c> and never reassigned; (iii) a call to a same-file
        /// private method of a non-partial type returning HttpClient, whose every return is (i) or such a local;
        /// (iv) the enclosing member's own HttpClient parameter, proven the same way. Null when it is one.
        /// </summary>
        private string? FactoryEvidence(SourceModel m, int site, int start, int end)
        {
            var code = m.Code;
            (start, end) = Trim(code, start, end);
            var text = Collapse(code[start..end]);

            var create = CreateClientEvidence(m, start, end);
            if (create is null)
                return null;
            if (create.Length > 0)
                return create;

            var method = PrivateFactoryMethodEvidence(m, start, end);
            if (method is null)
                return null;
            if (method.Length > 0)
                return method;

            var id = Identifier.Match(text);
            if (id.Success)
            {
                var local = LocalEvidence(m, site, id.Groups[1].Value, allowMethod: true);
                if (local is not null)
                    return local.Length == 0 ? null : local;

                var parameter = EnclosingParameter(m, site, id.Groups[1].Value);
                if (parameter is not null)
                {
                    var route = CallRoute(parameter);
                    return route.Count == 0
                        ? null
                        : $"'{text}' is {parameter.Name}'s own parameter, which is not itself factory-fed: {route[0]}";
                }
            }

            return $"'{text}' is not r.CreateClient(…) on an IHttpClientFactory, a factory-fed local, a same-file private "
                + "factory method or a factory-fed parameter. An EgressHttp-built client is not Factory: pin the row Upstream:<file>";
        }

        /// <summary>(i): null when [start, end) is exactly <c>r.CreateClient(…)</c> on an IHttpClientFactory receiver; empty when it is not that shape; a reason when the receiver is not a factory.</summary>
        private static string? CreateClientEvidence(SourceModel m, int start, int end)
        {
            var code = m.Code;
            var call = CreateClientCall.Match(code, start);
            if (!call.Success || Scanner.ClosingParen(code, call.Index + call.Length - 1) != end - 1)
                return string.Empty;
            var receiver = call.Groups["r"].Value;
            return ReceiverIsHttpClientFactory(m, receiver)
                ? null
                : $"'{receiver}' is not declared as IHttpClientFactory in {m.Path} (a parameter, a field, an explicitly typed "
                    + "local, or var = ….GetRequiredService<IHttpClientFactory>()), so its CreateClient is not evidence";
        }

        /// <summary>
        /// Every declaration of <paramref name="name"/> in the file — a parameter (attributes allowed), a field, a typed
        /// or var local — is IHttpClientFactory-typed, or a var initialized by <c>….Get[Required]Service&lt;IHttpClientFactory&gt;()</c>.
        /// </summary>
        private static bool ReceiverIsHttpClientFactory(SourceModel m, string name)
        {
            var code = m.Code;
            var declaration = new Regex(
                @"(?<![A-Za-z0-9_.])(?<type>(?:global::)?[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)*(?:\s*<[^;(){}=]*>)?(?:\s*\?)?)\s+@?"
                + Regex.Escape(name) + @"(?![A-Za-z0-9_])\s*(?:=(?!=)|[;,){]|\bin\b)",
                RegexOptions.CultureInvariant);
            var count = 0;
            foreach (Match d in declaration.Matches(code))
            {
                var type = Whitespace.Replace(d.Groups["type"].Value, string.Empty);
                if (NotADeclaringType.Contains(type) && type != "var")
                    continue;
                count++;
                var bare = type.Replace("global::", string.Empty, StringComparison.Ordinal)
                    .Replace("System.Net.Http.", string.Empty, StringComparison.Ordinal).TrimEnd('?');
                if (bare == "IHttpClientFactory")
                    continue;
                if (type == "var")
                {
                    var eq = d.Index + d.Length - 1;
                    var stop = Scanner.StatementEnd(code, d.Index);
                    if (code[eq] == '=' && stop > eq + 1 && code[stop - 1] == ';' && FactoryResolve.IsMatch(code[(eq + 1)..(stop - 1)].Trim()))
                        continue;
                }

                return false;
            }

            return count > 0;
        }

        /// <summary>
        /// (iii): null when [start, end) is exactly a call <c>M(…)</c> to the file's one private, non-partial-type
        /// method <c>HttpClient M(…)</c> whose every return is (i) or a never-reassigned local initialized by (i);
        /// empty when it is not a call to such a declaration; a reason when it is one that returns something else.
        /// </summary>
        private static string? PrivateFactoryMethodEvidence(SourceModel m, int start, int end)
        {
            var code = m.Code;
            var call = BareCall.Match(code, start);
            if (!call.Success || Scanner.ClosingParen(code, call.Index + call.Length - 1) != end - 1)
                return string.Empty;
            var name = call.Groups["n"].Value;
            var declarations = new Regex(
                @"(?<![A-Za-z0-9_.])(?:global::)?(?:System\s*\.\s*Net\s*\.\s*Http\s*\.\s*)?HttpClient\s*\??\s+@?" + Regex.Escape(name) + @"\s*\(",
                RegexOptions.CultureInvariant).Matches(code);
            if (declarations.Count == 0)
                return string.Empty;
            if (declarations.Count > 1)
                return $"'{name}' is declared {declarations.Count} times in {m.Path}; the route reads exactly one same-file factory method";

            var declaration = declarations[0];
            var open = declaration.Index + declaration.Length - 1;
            var type = Scanner.EnclosingType(m, open);
            var header = code[Scanner.StatementStart(code, declaration.Index)..declaration.Index];
            if (type is null || type.Partial || type.Keyword == "interface" || !IsPrivateHeader(header))
                return $"'{name}' is not a private method of a non-partial type in {m.Path}";

            var close = Scanner.ClosingParen(code, open);
            var bodyEnd = Scanner.DeclarationBodyEnd(m, close);
            if (bodyEnd < 0)
                return $"'{name}' has no body";

            var arrow = Scanner.SkipSpaceForward(code, close + 1);
            var returns = new List<(int Start, int End)>();
            if (arrow + 1 < code.Length && code[arrow] == '=' && code[arrow + 1] == '>')
            {
                returns.Add((arrow + 2, bodyEnd - 1));
            }
            else
            {
                for (var ret = Return.Match(code, close); ret.Success && ret.Index < bodyEnd; ret = ret.NextMatch())
                    returns.Add((ret.Index + ret.Length, Scanner.StatementEnd(code, ret.Index) - 1));
            }

            if (returns.Count == 0)
                return $"'{name}' returns nothing the route can read";

            foreach (var (s, e) in returns)
            {
                var (rs, re) = Trim(code, s, e);
                if (CreateClientEvidence(m, rs, re) is null)
                    continue;
                var id = Identifier.Match(code[rs..re]);
                if (id.Success && LocalEvidence(m, rs, id.Groups[1].Value, allowMethod: false) is { Length: 0 })
                    continue;
                return $"same-file method '{name}' returns '{Collapse(code[rs..re])}' at {m.Path}:{m.LineOf(rs)}, something other than a factory client";
            }

            return null;
        }

        /// <summary>
        /// (ii): for a bare identifier at a site, the nearest declaration <c>var|HttpClient v = …</c> earlier in the
        /// same member, in the site's block or an enclosing one. Null when there is none; empty when its initializer
        /// is (i) (or (iii) with <paramref name="allowMethod"/>) and v is never reassigned in its block afterwards;
        /// otherwise the reason.
        /// </summary>
        private static string? LocalEvidence(SourceModel m, int site, string v, bool allowMethod)
        {
            var code = m.Code;
            var (memberStart, _) = m.Member(site);
            var siteBlock = m.Innermost(site);
            var pattern = new Regex(
                @"(?<![A-Za-z0-9_.])(?:var|(?:global::)?(?:System\s*\.\s*Net\s*\.\s*Http\s*\.\s*)?HttpClient\s*\??)\s+@?" + Regex.Escape(v) + @"\s*=(?![=>])",
                RegexOptions.CultureInvariant);
            Match? best = null;
            foreach (Match d in pattern.Matches(code))
            {
                if (d.Index < memberStart || d.Index >= site || !m.IsAncestorOrSelf(m.Innermost(d.Index), siteBlock))
                    continue;
                best = d;
            }

            if (best is null)
                return null;

            var eq = best.Index + best.Length - 1;
            var stop = Scanner.StatementEnd(code, best.Index);
            if (stop <= eq + 1 || code[stop - 1] != ';')
                return $"the local '{v}' at {m.Path}:{m.LineOf(best.Index)} is not a plain declaration";
            var (s, e) = Trim(code, eq + 1, stop - 1);
            var initialized = CreateClientEvidence(m, s, e) is null || (allowMethod && PrivateFactoryMethodEvidence(m, s, e) is null);
            if (!initialized)
                return $"the local '{v}' at {m.Path}:{m.LineOf(best.Index)} is initialized by '{Collapse(code[s..e])}', not a factory client";

            var block = m.Innermost(best.Index);
            var scopeEnd = block < 0 ? code.Length : m.Blocks[block].Close;
            var reassigned = new Regex(
                @"(?<![A-Za-z0-9_.@])@?" + Regex.Escape(v) + @"\s*(?:\?\?)?=(?![=>])|\b(?:ref|out)\s+@?" + Regex.Escape(v) + @"(?![A-Za-z0-9_])",
                RegexOptions.CultureInvariant).Match(code, stop);
            return reassigned.Success && reassigned.Index < scopeEnd
                ? $"the local '{v}' is reassigned at {m.Path}:{m.LineOf(reassigned.Index)}, so it may not be the factory client"
                : string.Empty;
        }

        /// <summary>(iv): the innermost receiving member around the site whose HttpClient parameter is named <paramref name="v"/>.</summary>
        private Receiver? EnclosingParameter(SourceModel m, int site, string v)
        {
            Receiver? best = null;
            foreach (var o in scan.Occurrences.Where(o => o.Path == m.Path && o.Marker == Marker.HttpParam))
            {
                if (Scanner.ParameterName(m.Code, o.Offset) != v)
                    continue;
                var r = ReceiverAt(m, o.Offset);
                if (r.ListClose < site && site < r.BodyEnd && (best is null || r.ListClose > best.ListClose))
                    best = r;
            }

            return best;
        }

        // ── Supply sites ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// S(R) on cleaned production code. A constructor: <c>new [ns.]T(</c>, <c>T[?] id = new(</c>, and <c>: this(</c>
        /// inside T. A method N: <c>.N(</c>/<c>.N&lt;…&gt;(</c> anywhere, plus a bare <c>N(</c> in the declaring file
        /// (and in any file when N is not private and T is neither sealed nor static, where a derived type calls it
        /// bare), <c>nameof(</c> excluded; a private method of a non-partial type only in its declaring file. Unknown
        /// suppliers (named, with file:line): <c>CreateInstance&lt;T&gt;</c>, <c>typeof(T)</c>, an empty-argument generic
        /// registration of T, a method group of N. Typed: each <c>.AddHttpClient&lt;…T&gt;(</c> and whether D1 made it Factory.
        /// </summary>
        private (List<Site> Sites, List<string> Unknown, List<(string Path, int Line, bool Factory)> Typed) SupplySites(Receiver r)
        {
            var sites = new List<Site>();
            var unknown = new List<string>();
            var typed = new List<(string, int, bool)>();

            if (r.IsConstructor)
            {
                foreach (var (path, at) in scan.Mentions(r.TypeName))
                {
                    var m = scan.Model(path)!;
                    var code = m.Code;
                    var before = code[Math.Max(0, at - 400)..at];
                    var after = at + r.TypeName.Length;
                    string Line() => $"{path}:{m.LineOf(at)}";
                    if (NewBefore.IsMatch(before) && OpenAfter.Match(code, after) is { Success: true } open)
                    {
                        sites.Add(new Site(path, at, open.Index + open.Length - 1, "new T(…)"));
                    }
                    else if (TargetTypedAfter.Match(code, after) is { Success: true } target && !NewBefore.IsMatch(before))
                    {
                        sites.Add(new Site(path, at, target.Index + target.Length - 1, "T x = new(…)"));
                    }
                    else if (CreateInstanceBefore.IsMatch(before) && CloseAngleAfter.Match(code, after).Success)
                    {
                        unknown.Add($"{Line()}: unknown supplier CreateInstance<{r.TypeName}>; the scan cannot see which client it passes");
                    }
                    else if (TypeofBefore.IsMatch(before) && CloseParenAfter.Match(code, after).Success)
                    {
                        unknown.Add($"{Line()}: unknown supplier typeof({r.TypeName}); activation by type passes a client the scan cannot see");
                    }
                    else if (EmptyRegistrationBefore.Match(before) is { Success: true } registration && EmptyRegistrationAfter.Match(code, after).Success)
                    {
                        unknown.Add($"{Line()}: unknown supplier '{Whitespace.Replace(registration.Value, " ").TrimStart()}{r.TypeName}>()', "
                            + "an empty-argument generic registration: the container picks the client");
                    }
                    else if (TypedRegistrationBefore.Match(before) is { Success: true } typedRegistration && TypedRegistrationAfter.Match(code, after).Success)
                    {
                        var offset = at - before.Length + typedRegistration.Index;
                        var factory = scan.Occurrences.Any(o => o.Path == path && o.Marker == Marker.HttpRegister && o.Offset == offset && o.Guard == GuardKind.Factory);
                        typed.Add((path, m.LineOf(at), factory));
                    }
                }

                if (r.Type is not null)
                {
                    var m = scan.Model(r.Path)!;
                    var body = m.Blocks[r.Type.Body];
                    for (var t = ThisInitializer.Match(m.Code, body.Open); t.Success && t.Index < body.Close; t = t.NextMatch())
                        sites.Add(new Site(r.Path, t.Index, t.Index + t.Length - 1, ": this(…)"));
                }

                return (sites, unknown, typed);
            }

            var everywhere = !(r.IsPrivate && !r.Partial);
            foreach (var (path, at) in scan.Mentions(r.Member))
            {
                if (!everywhere && path != r.Path)
                    continue;
                var m = scan.Model(path)!;
                var code = m.Code;
                if (Scanner.InNameof(code, at))
                    continue;
                var j = Scanner.SkipSpaceBack(code, at - 1);
                var call = CallAfter.Match(code, at + r.Member.Length);
                if (call.Success)
                {
                    if (j >= 0 && code[j] == '.' && Scanner.IsQualifiedDeclaration(code, j))
                        continue;
                    if (j >= 0 && code[j] == '.')
                        sites.Add(new Site(path, at, call.Index + call.Length - 1, ".N(…)"));
                    else if (j >= 0 && Scanner.EndsAType(code, j))
                        continue;
                    else if (path == r.Path || (!r.IsPrivate && !r.Sealed))
                        sites.Add(new Site(path, at, call.Index + call.Length - 1, "N(…)"));
                }
                else if (!(j >= 0 && Scanner.EndsAType(code, j)))
                {
                    unknown.Add($"{path}:{m.LineOf(at)}: unknown supplier, a method group or other mention of '{r.Member}' the scan cannot follow");
                }
            }

            return (sites, unknown, typed);
        }

        // ── Upstream ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 8.4a: the row under check is excluded from its own chain and grounding. The named file is scanned
        /// production; every supply site of every unguarded occurrence lies in it (a constructor's type sealed);
        /// it builds no unguarded http.new; its http.param row, if it is another row, is fully guarded or routed
        /// (Factory, Upstream, Governance; never Exempt) and holds; its HttpClient fields and properties are private
        /// and none of its types is partial; and it is grounded by a Wrapped http.new or that routed row.
        /// </summary>
        private List<string> UpstreamParamProblems(Pin pin, string target)
        {
            if (!scan.Files.Contains(target, StringComparer.Ordinal))
                return [$"{UpstreamPrefix}{target}: not a scanned production file"];
            if (!observed.TryGetValue(pin.Key, out var row))
                return ["the scan sees no occurrence to route"];

            var problems = new List<string>();
            var suppliesTarget = false;
            foreach (var o in row.Occurrences.Where(o => !o.Guarded))
            {
                var r = ReceiverAt(scan.Model(o.Path)!, o.Offset);
                if (r.IsConstructor && !r.Sealed)
                    problems.Add($"{o.Where}: {r.TypeName} is not sealed: a derived type's base(…) call would be a supply site the scan cannot list");
                if (r.Partial)
                    problems.Add($"{o.Where}: {r.TypeName} is partial");

                var (sites, unknown, typed) = SupplySites(r);
                problems.AddRange(unknown.Select(u => $"{o.Where}: {u}"));
                problems.AddRange(typed.Select(t => $"{o.Where}: {t.Path}:{t.Line} supplies {r.Name} through AddHttpClient<…>, not {target}"));
                if (sites.Count == 0)
                    problems.Add($"{o.Where}: nothing supplies {r.Name}: no construction, initializer or call in production");
                foreach (var site in sites.Where(s => s.Path != target))
                    problems.Add($"{o.Where}: {Where(site)} ({site.Kind}) supplies {r.Name} outside {target}");
                suppliesTarget |= sites.Any(s => s.Path == target);
            }

            if (!suppliesTarget)
                problems.Add($"{target} supplies nothing to this row");

            var newRow = observed.GetValueOrDefault(target + "\t" + Marker.HttpNew);
            if (newRow is { Unguarded: > 0 })
                problems.Add($"{target} builds {newRow.Unguarded} unguarded http.new ({newRow.Lines}); it cannot vouch for what it supplies");

            var chain = target == pin.Path ? null : observed.GetValueOrDefault(target + "\t" + Marker.HttpParam);
            var chainHolds = false;
            if (chain is { Unguarded: > 0 })
            {
                var chainPin = pins.FirstOrDefault(p => p.Key == chain.Key);
                if (chainPin is null)
                {
                    problems.Add($"{target} [http.param] is not pinned");
                }
                else if (!(chainPin.Reason == FactoryReason || chainPin.Reason.StartsWith(UpstreamPrefix, StringComparison.Ordinal) || chainPin.Reason == GovernanceReason))
                {
                    problems.Add($"{target} [http.param] is pinned '{chainPin.Reason}', not Factory, Upstream or Governance: the chain is not routed");
                }
                else
                {
                    var chainProblems = RowProblems(chainPin);
                    chainHolds = chainProblems.Count == 0;
                    if (!chainHolds)
                        problems.Add($"{target} [http.param] is pinned '{chainPin.Reason}', but its route does not hold: {chainProblems[0]}");
                }
            }

            var model = scan.Model(target)!;
            problems.AddRange(ExposedClients(model));

            var grounded = observed.GetValueOrDefault(target + "\t" + Marker.HttpNew) is { Guarded: > 0 } || chainHolds;
            if (!grounded)
                problems.Add($"{target} is not grounded: it holds no Wrapped http.new and no other http.param row whose route holds");

            return problems;
        }

        /// <summary>Every non-private HttpClient field or property, and every partial type, in the named file.</summary>
        private static IEnumerable<string> ExposedClients(SourceModel m)
        {
            var code = m.Code;
            foreach (var block in Enumerable.Range(0, m.Blocks.Count).Where(b => m.Blocks[b].IsTypeBody))
            {
                var header = code[m.Blocks[block].HeaderStart..m.Blocks[block].Open];
                if (!Scanner.TypeHeader.IsMatch(header))
                    continue;
                if (Regex.IsMatch(header, @"\bpartial\b"))
                    yield return $"{m.Path}:{m.LineOf(m.Blocks[block].Open)}: a partial type; another part could hand its client out";
            }

            foreach (Match f in HttpClientMember.Matches(code))
            {
                var owner = m.Innermost(f.Index);
                if (owner < 0 || !m.Blocks[owner].IsTypeBody || Scanner.OpeningParenthesis(code, f.Index) >= 0)
                    continue;
                if (!IsPrivateHeader(code[Scanner.StatementStart(code, f.Index)..f.Index]))
                    yield return $"{m.Path}:{m.LineOf(f.Index)}: the HttpClient member '{f.Groups[1].Value}' is not private; another file could take the client";
            }
        }

        /// <summary>
        /// 8.4b: the row's project cannot reach Ashlar.Infrastructure; the named file is scanned, in a production
        /// executable whose closure holds the row's project, and calls AddAshlarEgressGuard; and every production
        /// executable whose closure holds the row's project calls it in one of its scanned files.
        /// </summary>
        private List<string> UpstreamRegisterProblems(Pin pin, string target)
        {
            var problems = new List<string>();
            var project = scan.ProjectOf(pin.Path);
            if (project is null)
                return [$"{pin.Path} belongs to no project the scan can read"];
            if (scan.Closure(project).Contains(InfrastructureProject))
                problems.Add($"{project} reaches {InfrastructureProject}: this project can reach AddAshlarEgressGuard; call it after the registration");

            if (!scan.Files.Contains(target, StringComparer.Ordinal))
            {
                problems.Add($"{UpstreamPrefix}{target}: not a scanned production file");
            }
            else
            {
                var host = scan.ProjectOf(target);
                if (host is null || !scan.Projects[host].IsExe || scan.Projects[host].IsTest)
                    problems.Add($"{target} is not in a production executable project");
                else if (!scan.Closure(host).Contains(project))
                    problems.Add($"{host} does not reference {project}, so {target} does not compose this registration");
                if (!scan.GuardCalls.Any(c => c.Path == target))
                    problems.Add($"{target} never calls AddAshlarEgressGuard");
            }

            foreach (var (exe, _) in scan.Projects.Where(p => p.Value.IsExe && !p.Value.IsTest && scan.Closure(p.Key).Contains(project)))
            {
                if (!scan.GuardCalls.Any(c => scan.ProjectOf(c.Path) == exe))
                    problems.Add($"{exe} composes {project} and calls AddAshlarEgressGuard in none of its files");
            }

            return problems;
        }

        // ── The receiving member ───────────────────────────────────────────────────────────────────────────

        /// <summary>The declaration whose parameter list holds the http.param type token at <paramref name="typeAt"/>.</summary>
        private static Receiver ReceiverAt(SourceModel m, int typeAt)
        {
            var code = m.Code;
            var open = Scanner.OpeningParenthesis(code, typeAt);
            var close = Scanner.ClosingParen(code, open);
            var param = Scanner.ParameterName(code, typeAt) ?? "?";
            var arguments = Scanner.SplitArguments(code, open);
            var position = arguments.FindIndex(a => a.Start <= typeAt && typeAt < a.End);
            var httpParams = 0;
            for (var p = Scanner.HttpClientParameter.Match(code, open); p.Success && p.Index < close; p = p.NextMatch())
            {
                if (Scanner.OpeningParenthesis(code, p.Groups[1].Index) == open)
                    httpParams++;
            }

            var j = Scanner.SkipSpaceBack(code, open - 1);
            var member = Scanner.ReadNameBack(code, ref j) ?? "?";
            var qualified = j >= 0 && code[j] == '.';
            var w = j;
            while (w >= 0 && Scanner.IsIdentifierChar(code[w]))
                w--;
            var previous = j >= 0 ? code[(w + 1)..(j + 1)] : string.Empty;
            var isExtension = Regex.IsMatch(code[(open + 1)..close], @"^\s*(?:\[[^\]]*\]\s*)*this\s");

            if (previous is "class" or "struct" or "record")
            {
                var header = code[Scanner.StatementStart(code, open)..open];
                return new Receiver(m.Path, typeAt, param, position, httpParams, member, IsConstructor: true, member, Type: null,
                    Sealed: Regex.IsMatch(header, @"\bsealed\b"), Partial: Regex.IsMatch(header, @"\bpartial\b"),
                    IsPrivate: false, isExtension, close, Scanner.DeclarationBodyEnd(m, close));
            }

            var type = Scanner.EnclosingType(m, open);
            var owner = m.Innermost(open);
            var isLocal = owner >= 0 && !m.Blocks[owner].IsTypeBody;
            var isPrivate = isLocal || (type is not null && type.Keyword != "interface" && !qualified
                && IsPrivateHeader(code[Scanner.StatementStart(code, open)..open]));
            return new Receiver(m.Path, typeAt, param, position, httpParams, member,
                IsConstructor: !qualified && !isLocal && type is not null && member == type.Name,
                type?.Name ?? string.Empty, type, type?.Sealed ?? false, type?.Partial ?? false, isPrivate, isExtension,
                close, Scanner.DeclarationBodyEnd(m, close));
        }

        /// <summary>Declared <c>private</c> and not <c>protected</c>, or with no access modifier at all.</summary>
        private static bool IsPrivateHeader(string header) =>
            (Regex.IsMatch(header, @"\bprivate\b") && !Regex.IsMatch(header, @"\bprotected\b"))
            || !Regex.IsMatch(header, @"\b(?:public|protected|internal|private)\b");

        private static (int Start, int End) Trim(string code, int start, int end)
        {
            while (start < end && char.IsWhiteSpace(code[start]))
                start++;
            while (end > start && char.IsWhiteSpace(code[end - 1]))
                end--;
            return (start, end);
        }

        private static string Collapse(string text) => Whitespace.Replace(text, " ").Trim();
    }

    // ── Route controls ─────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> RouteControlNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in RouteControls.Keys.OrderBy(k => k, StringComparer.Ordinal))
            data.Add(name);
        return data;
    }

    /// <summary>
    /// Each route control is a small production tree (files, csproj texts) and a TSV. The theory scans the files
    /// with the same classifier as the tree (<see cref="TreeScan.FromSources"/>), requires every pinned row to equal
    /// what the scan sees (so the route runs on the rows it really reads), and runs <see cref="ReasonProblems"/> on
    /// every row with something unguarded. A control that holds must produce no problem; a control that fails must
    /// produce one containing its declared clause.
    /// </summary>
    [Theory]
    [MemberData(nameof(RouteControlNames))]
    public void F9_each_route_control_holds_or_fails_as_declared(string name)
    {
        var control = RouteControls[name];
        var scan = TreeScan.FromSources(control.Files, control.Csprojs ?? new Dictionary<string, string>());
        var pins = ParsePins(control.Tsv.Split('\n'), "route-control.tsv");
        pins.Errors.Should().BeEmpty("route control '{0}' must parse", name);
        pins.Rows.Should().NotBeEmpty("route control '{0}' pins nothing, so it checks nothing", name);

        var observed = Observed(scan).ToDictionary(r => r.Key, StringComparer.Ordinal);
        foreach (var pin in pins.Rows)
        {
            observed.Should().ContainKey(pin.Key, "route control '{0}' pins {1} [{2}], which the scan must see", name, pin.Path, pin.Marker);
            $"{observed[pin.Key].Total}/{observed[pin.Key].Guarded}".Should().Be($"{pin.Total}/{pin.Guarded}",
                "route control '{0}' pins {1} [{2}]; the scan read it as:\n{3}", name, pin.Path, pin.Marker, string.Join("\n", observed[pin.Key].Occurrences));
        }

        var problems = pins.Rows.Where(p => p.Unguarded > 0).SelectMany(p => ReasonProblems(p, scan, pins.Rows, observed)).ToList();
        if (control.Expect is null)
        {
            problems.Should().BeEmpty("route control '{0}' declares that its routes hold; problems:\n{1}", name, string.Join("\n", problems));
        }
        else
        {
            problems.Should().Contain(p => p.Contains(control.Expect, StringComparison.Ordinal),
                "route control '{0}' must fail with '{1}'; problems:\n{2}", name, control.Expect, string.Join("\n", problems));
        }
    }

    private sealed record RouteControl(
        IReadOnlyDictionary<string, string> Files, string Tsv, string? Expect, IReadOnlyDictionary<string, string>? Csprojs = null);

    private static string Row(string path, string marker, int total, int guarded, string reason, string guardedBy = "-") =>
        string.Join('\t', path, marker, total, guarded, guardedBy, reason, "-", "route control");

    private static Dictionary<string, string> Sources(params (string Path, string Source)[] files) =>
        files.ToDictionary(f => f.Path, f => f.Source, StringComparer.Ordinal);

    private const string ConsumerFile = "src/Lib/Consumer.cs";

    private const string ConsumerSource = """
        namespace Lib;

        public sealed class Consumer
        {
            private readonly HttpClient _http;

            public Consumer(string name, HttpClient http)
            {
                _http = http;
            }

            public Task<string> GetAsync() => _http.GetStringAsync("/x");
        }
        """;

    private const string FactorySupplierFile = "src/Lib/RegB.cs";

    private const string FactorySupplierSource = """
        namespace Lib;

        public static class RegB
        {
            public static Consumer Build(IHttpClientFactory factory) => new Consumer("b", factory.CreateClient("b"));
        }
        """;

    private const string WorkerSource = """
        namespace Lib;

        public sealed class Worker
        {
            private readonly IHttpClientFactory _httpClientFactory;

            public Worker(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

            public async Task<int> RunAsync(CancellationToken ct)
            {
                var director = CreateDirectorClient("https://director.invalid/");
                var tasks = await ListAsync(director, ct);
                return await ExecuteAsync(director, tasks, ct) ? 1 : 0;
            }

            private async Task<bool> ExecuteAsync(HttpClient director, string tasks, CancellationToken ct)
            {
                if (!await PatchAsync(director, tasks, ct))
                    return false;
                return await PatchAsync(director, "done", ct);
            }

            private static async Task<bool> PatchAsync(HttpClient director, string body, CancellationToken ct)
            {
                using var response = await director.PostAsync("api/tasks", new StringContent(body), ct);
                return response.IsSuccessStatusCode;
            }

            private static async Task<string> ListAsync(HttpClient director, CancellationToken ct) => await director.GetStringAsync("api/tasks", ct);

            private HttpClient CreateDirectorClient(string baseUrl)
            {
                var client = _httpClientFactory.CreateClient("worker");
                client.BaseAddress = new Uri(baseUrl);
                return client;
            }
        }
        """;

    private const string RunPodClientFile = "src/Lib/RunPodHttpClient.cs";

    private const string RunPodClientSource = """
        namespace Lib;

        public sealed class RunPodHttpClient : IRunPodClient
        {
            private readonly HttpClient _httpClient;
            private readonly ILogger<RunPodHttpClient> _logger;

            public RunPodHttpClient(HttpClient httpClient, ILogger<RunPodHttpClient> logger)
            {
                _httpClient = httpClient;
                _logger = logger;
            }

            public Task<string> PingAsync() => _httpClient.GetStringAsync("ping");
        }
        """;

    private const string RunPodExtensionsFile = "src/Lib/RunPodExtensions.cs";

    private const string RunPodExtensionsSource = """
        namespace Lib;

        public static class RunPodExtensions
        {
            public static IServiceCollection AddRunPod(this IServiceCollection services)
            {
                services.AddHttpClient<IRunPodClient, RunPodHttpClient>((sp, client) =>
                {
                    client.BaseAddress = new Uri("https://api.runpod.invalid/");
                });
                services.AddAshlarEgressGuard();
                return services;
            }
        }
        """;

    private const string OllamaProviderFile = "src/Lib/OllamaProvider.cs";

    private const string OllamaProviderSource = """
        namespace Lib;

        public sealed class OllamaProvider
        {
            private readonly HttpClient _httpClient;

            public OllamaProvider(HttpClient httpClient, string baseUrl)
            {
                _httpClient = httpClient;
                _httpClient.BaseAddress = new Uri(baseUrl);
            }

            public Task<string> TagsAsync() => _httpClient.GetStringAsync("api/tags");
        }
        """;

    private const string ProviderFactoryFile = "src/Lib/ProviderFactory.cs";

    private const string ProviderFactorySource = """
        namespace Lib;

        public class ProviderFactory
        {
            private static readonly HttpClient Http = EgressHttp.CreateClient(EgressFamilies.ModelLegacy, "EG-MDL-03");
            private HttpClient? _ollamaHttpClient;
            private OllamaProvider? _ollama;

            public Task<string> ProbeAsync() => Http.GetStringAsync("https://example.invalid/");

            public OllamaProvider GetOrCreate(string baseUrl)
            {
                var httpClient = EgressHttp.CreateClient(EgressFamilies.ModelLegacy, "EG-MDL-07");
                httpClient.Timeout = TimeSpan.FromSeconds(30);
                _ollamaHttpClient?.Dispose();
                _ollamaHttpClient = httpClient;
                _ollama = new OllamaProvider(httpClient, baseUrl);
                return _ollama;
            }
        }
        """;

    private const string RemoteBrickFile = "src/Lib/RemoteBrick.cs";

    private const string RemoteBrickSource = """
        namespace Lib;

        public sealed class RemoteBrick
        {
            private readonly HttpClient _http;

            public RemoteBrick(string id, HttpClient httpClient)
            {
                _http = httpClient;
            }

            public Task<string> RunAsync() => _http.GetStringAsync("run");
        }
        """;

    private const string CompositeRegistryFile = "src/Lib/CompositeRegistry.cs";

    private const string CompositeRegistrySource = """
        namespace Lib;

        public sealed class CompositeRegistry
        {
            private readonly HttpClient _httpClient;

            public CompositeRegistry(string name, HttpClient httpClient)
            {
                _httpClient = httpClient;
            }

            public RemoteBrick Resolve(string id) => new RemoteBrick(id, _httpClient);
        }
        """;

    private const string MeshFile = "src/Lib/Mesh.cs";

    private const string MeshSource = """
        namespace Lib;

        public static class Mesh
        {
            public static CompositeRegistry Build(IServiceProvider sp)
            {
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                return new CompositeRegistry("mesh", factory.CreateClient());
            }
        }
        """;

    private const string FleetExtensionsFile = "commercial/src/Fleet.Infrastructure/FleetExtensions.cs";

    private const string FleetExtensionsSource = """
        namespace Fleet.Infrastructure;

        public static class FleetExtensions
        {
            public static IServiceCollection AddFleet(this IServiceCollection services)
            {
                services.AddHttpClient("mesh-lab-worker-executor");
                return services;
            }
        }
        """;

    private const string FleetHostProgramFile = "commercial/src/Fleet.Host/Program.cs";

    private const string FleetHostProgramSource = """
        using Fleet.Infrastructure;

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHttpClient("ashlar-sns-signing", c => c.Timeout = TimeSpan.FromSeconds(15));
        builder.Services.AddAshlar(options =>
        {
            options.Profile = "fleet";
        });
        builder.Services.AddAshlarEgressGuard();
        builder.Services.AddFleet();
        var app = builder.Build();
        app.Run();
        """;

    private const string FleetInfrastructureCsproj = "commercial/src/Fleet.Infrastructure/Fleet.Infrastructure.csproj";

    private const string FleetHostCsproj = "commercial/src/Fleet.Host/Fleet.Host.csproj";

    private const string FleetRow = "commercial/src/Fleet.Infrastructure/FleetExtensions.cs\thttp.register\t1\t0\t-\tUpstream:commercial/src/Fleet.Host/Program.cs\t-\troute control";

    private static Dictionary<string, string> FleetCsprojs(string? infrastructure = null, string? host = null) => new(StringComparer.Ordinal)
    {
        [FleetInfrastructureCsproj] = infrastructure ?? """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../../../src/Ashlar.Core/Ashlar.Core.csproj" />
              </ItemGroup>
            </Project>
            """,
        [FleetHostCsproj] = host ?? """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <ProjectReference Include="..\Fleet.Infrastructure\Fleet.Infrastructure.csproj" />
                <ProjectReference Include="..\..\..\src\Ashlar.Infrastructure\Ashlar.Infrastructure.csproj" />
              </ItemGroup>
            </Project>
            """,
        ["commercial/tests/Fleet.Tests/Fleet.Tests.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Fleet.Host/Fleet.Host.csproj" />
              </ItemGroup>
            </Project>
            """,
        [InfrastructureProject] = """<Project Sdk="Microsoft.NET.Sdk" />""",
        ["src/Ashlar.Core/Ashlar.Core.csproj"] = """<Project Sdk="Microsoft.NET.Sdk" />""",
    };

    private const string OllamaChatClientSource = """
        namespace Ashlar.AI.Pipeline.Clients;

        public sealed class OllamaHttpChatClient
        {
            private readonly HttpClient _http;

            public OllamaHttpChatClient(string baseUrl)
            {
                _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            }
        }
        """;

    private const string GovernedMeaiSource = """
        namespace Ashlar.AI.Pipeline;

        public static class MeaiPipelineServiceCollectionExtensions
        {
            public static IServiceCollection AddPipeline(this IServiceCollection services)
            {
                services.TryAddSingleton<IBedrockChatClientFactory, AwsBedrockChatClientFactory>();
                Func<IServiceProvider, IChatClient> defaultOllama = sp =>
                    new OllamaHttpChatClient("http://localhost:11434");

                services.AddKeyedChatClient(MeaiTargetKeys.LocalOllama, sp => defaultOllama(sp))
                    .UseAshlarGovernance(MeaiTargetKeys.LocalOllama);
                services.AddKeyedChatClient(MeaiTargetKeys.LocalOnnx, sp => new LlamaSharpChatClient())
                    .UseAshlarGovernance(MeaiTargetKeys.LocalOnnx);
                services.AddKeyedChatClient("cloud:bedrock:fast", sp => sp.GetRequiredService<IBedrockChatClientFactory>().Create("m"))
                    .UseAshlarGovernance("cloud:bedrock:fast");
                //BEDROCK-OUTSIDE
                return services;
            }

            public static ChatClientBuilder AddAshlarGovernedChatClient(this IServiceCollection services, string targetKey, Func<IServiceProvider, IChatClient> innerFactory) =>
                services.AddKeyedChatClient(targetKey, innerFactory).UseAshlarGovernance(targetKey);
        }
        """;

    private const string GovernanceSource = """
        namespace Ashlar.AI.Pipeline.Governance;

        public static class AshlarGovernanceChatClientBuilderExtensions
        {
            public static ChatClientBuilder UseAshlarGovernance(this ChatClientBuilder builder, string targetKey)
            {
                builder.Use((inner, sp) => new EgressGuardChatClient(inner, null, null));
                builder.Use((inner, sp) => new PolicyGateChatClient(inner, targetKey));
                builder.Use((inner, sp) => new SanitizingChatClient(inner, targetKey));
                builder.Use((inner, sp) => new AuditingChatClient(inner, targetKey));
                return builder;
            }
        }
        """;

    private static string GovernedTsv => string.Join('\n',
        Row(OllamaChatClientFile, Marker.HttpNew, 1, 0, GovernanceReason),
        Row(MeaiRegistrationFile, Marker.ChatRegister, 6, 0, GovernanceReason));

    private const string ProbeFile = "src/Lib/Probe.cs";

    private const string ProbeSource = """
        namespace Lib;

        public sealed class Probe
        {
            private readonly HttpClient? _http;

            public Probe(HttpClient? http = null) => _http = http;

            public Task<string>? PingAsync() => _http?.GetStringAsync("/ping");
        }
        """;

    // A property, not a field: it reads constants and helpers from other parts, and the order of static
    // initializers across partial declarations is unspecified.
    private static Dictionary<string, RouteControl> RouteControls => LazyRouteControls.Value;

    private static readonly Lazy<Dictionary<string, RouteControl>> LazyRouteControls = new(BuildRouteControls);

    private static Dictionary<string, RouteControl> BuildRouteControls() => new(StringComparer.Ordinal)
    {
        // ── Factory (call): hold ────────────────────────────────────────────────────────────────────────────
        ["Factory holds: an inline factory client, two suppliers"] = new(
            Sources(
                (ConsumerFile, ConsumerSource),
                ("src/Lib/RegA.cs", """
                    namespace Lib;

                    public static class RegA
                    {
                        public static Consumer Build(IServiceProvider sp)
                        {
                            var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
                            return new Consumer("a", clientFactory.CreateClient());
                        }
                    }
                    """),
                (FactorySupplierFile, FactorySupplierSource)),
            Row(ConsumerFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: null),
        ["Factory holds: a configured local"] = new(
            Sources(
                (ConsumerFile, ConsumerSource),
                ("src/Lib/Reg.cs", """
                    namespace Lib;

                    public static class Reg
                    {
                        public static Consumer Build(IHttpClientFactory factory)
                        {
                            var client = factory.CreateClient("x");
                            client.BaseAddress = new Uri("https://example.invalid/");
                            client.DefaultRequestHeaders.Add("k", "v");
                            return new Consumer("a", client);
                        }
                    }
                    """)),
            Row(ConsumerFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: null),
        ["Factory holds: a namespace-qualified new over an explicitly typed local and a GetRequiredService<IHttpClientFactory> receiver (Phase19)"] = new(
            Sources(
                ("src/Lib/Remote/Consumer.cs", ConsumerSource.Replace("namespace Lib;", "namespace Lib.Remote;", StringComparison.Ordinal)),
                ("src/Hosting/Phases.cs", """
                    namespace Hosting;

                    internal static partial class Phases
                    {
                        public static void Register(IServiceCollection services, string url)
                        {
                            if (!string.IsNullOrEmpty(url))
                            {
                                services.AddSingleton<Lib.Remote.IExecutionPlatform>(sp =>
                                {
                                    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
                                    HttpClient client = factory.CreateClient("AshlarExecution");
                                    return new Lib.Remote.Consumer("remote", client);
                                });
                            }
                        }
                    }
                    """)),
            Row("src/Lib/Remote/Consumer.cs", Marker.HttpParam, 1, 0, FactoryReason),
            Expect: null),
        ["Factory holds: an interface method invoked only by a factory-fed caller (ISnsSignatureVerifier)"] = new(
            Sources(
                ("src/Lib/IVerifier.cs", """
                    namespace Lib;

                    public interface IVerifier
                    {
                        Task<bool> VerifyAsync(string message, HttpClient http, CancellationToken ct = default);
                    }
                    """),
                ("src/Lib/Verifier.cs", """
                    namespace Lib;

                    public sealed class Verifier : IVerifier
                    {
                        public async Task<bool> VerifyAsync(string message, HttpClient http, CancellationToken ct = default)
                        {
                            using var response = await http.GetAsync(message, ct);
                            return response.IsSuccessStatusCode;
                        }
                    }
                    """),
                ("src/Lib/Webhook.cs", """
                    namespace Lib;

                    public static class Webhook
                    {
                        public static async Task<bool> HandleAsync([FromServices] IVerifier verifier, [FromServices] IHttpClientFactory httpClientFactory, string body)
                        {
                            var http = httpClientFactory.CreateClient("signing");
                            return await verifier.VerifyAsync(body, http);
                        }
                    }
                    """)),
            string.Join('\n',
                Row("src/Lib/IVerifier.cs", Marker.HttpParam, 1, 0, FactoryReason),
                Row("src/Lib/Verifier.cs", Marker.HttpParam, 1, 0, FactoryReason)),
            Expect: null),
        ["Factory holds: private methods fed by a same-file factory-returning method, with a forwarded parameter (MeshLab)"] = new(
            Sources(("src/Lib/Worker.cs", WorkerSource)),
            Row("src/Lib/Worker.cs", Marker.HttpParam, 3, 0, FactoryReason),
            Expect: null),
        ["Factory holds: a private static method fed by a local in an enclosing using block from a switch-expression block (SNS)"] = new(
            Sources(("src/Lib/SnsWebhook.cs", """
                namespace Lib;

                public static class SnsWebhook
                {
                    public static async Task<IResult> HandleAsync(
                        HttpContext context,
                        [FromServices] IHttpClientFactory httpClientFactory,
                        CancellationToken cancellationToken)
                    {
                        JsonDocument doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
                        using (doc)
                        {
                            var type = doc.RootElement.GetProperty("Type").GetString() ?? string.Empty;
                            var http = httpClientFactory.CreateClient("ashlar-sns-signing");
                            return type switch
                            {
                                "SubscriptionConfirmation" => await ConfirmAsync(doc.RootElement, type, http, cancellationToken),
                                _ => Results.Ok(),
                            };
                        }
                    }

                    private static async Task<IResult> ConfirmAsync(JsonElement root, string type, HttpClient http, CancellationToken cancellationToken)
                    {
                        var url = root.GetProperty("SubscribeURL").GetString();
                        using var response = await http.GetAsync(url, cancellationToken);
                        return Results.Ok(new { type });
                    }
                }
                """)),
            Row("src/Lib/SnsWebhook.cs", Marker.HttpParam, 1, 0, FactoryReason),
            Expect: null),

        // ── Factory (call): fail ────────────────────────────────────────────────────────────────────────────
        ["Factory fails: the local is reassigned"] = new(
            Sources(
                (ConsumerFile, ConsumerSource),
                ("src/Lib/Reg.cs", """
                    namespace Lib;

                    public static class Reg
                    {
                        public static Consumer Build(IHttpClientFactory factory, bool raw)
                        {
                            var client = factory.CreateClient("x");
                            if (raw)
                                client = new HttpClient();
                            return new Consumer("a", client);
                        }
                    }
                    """)),
            Row(ConsumerFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "the local 'client' is reassigned"),
        ["Factory fails: new T(_field)"] = new(
            Sources(
                (ConsumerFile, ConsumerSource),
                ("src/Lib/Holder.cs", """
                    namespace Lib;

                    public sealed class Holder
                    {
                        private readonly HttpClient _shared;

                        public Holder(IHttpClientFactory factory)
                        {
                            _shared = factory.CreateClient("x");
                        }

                        public Consumer Make() => new Consumer("a", _shared);
                    }
                    """)),
            Row(ConsumerFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "'_shared' is not r.CreateClient"),
        ["Factory fails: a second file passes new HttpClient()"] = new(
            Sources(
                (ConsumerFile, ConsumerSource),
                (FactorySupplierFile, FactorySupplierSource),
                ("src/Lib/RegRaw.cs", """
                    namespace Lib;

                    public static class RegRaw
                    {
                        public static Consumer Build() => new Consumer("raw", new HttpClient());
                    }
                    """)),
            Row(ConsumerFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "'new HttpClient()' is not r.CreateClient"),
        ["Factory fails: the receiver is a StubHttpMessageHandler, not an IHttpClientFactory"] = new(
            Sources(
                (ConsumerFile, ConsumerSource),
                ("src/Lib/StubReg.cs", """
                    namespace Lib;

                    public static class StubReg
                    {
                        public static Consumer Build()
                        {
                            var stub = new StubHttpMessageHandler();
                            return new Consumer("a", stub.CreateClient());
                        }
                    }
                    """)),
            Row(ConsumerFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "'stub' is not declared as IHttpClientFactory"),
        ["Factory fails: the constructor takes two HttpClient parameters"] = new(
            Sources(
                ("src/Lib/Two.cs", """
                    namespace Lib;

                    public sealed class Two
                    {
                        private readonly HttpClient _a;
                        private readonly HttpClient _b;

                        public Two(HttpClient a, HttpClient b)
                        {
                            _a = a;
                            _b = b;
                        }

                        public Task<string> GetAsync() => _a.GetStringAsync("/a");
                    }
                    """),
                ("src/Lib/Reg.cs", """
                    namespace Lib;

                    public static class Reg
                    {
                        public static Two Build(IHttpClientFactory factory) => new Two(factory.CreateClient("a"), factory.CreateClient("b"));
                    }
                    """)),
            Row("src/Lib/Two.cs", Marker.HttpParam, 2, 0, FactoryReason),
            Expect: "declares 2 HttpClient parameters"),
        ["Factory fails: a target-typed T x = new(raw)"] = new(
            Sources(
                (ConsumerFile, ConsumerSource),
                ("src/Lib/Reg.cs", """
                    namespace Lib;

                    public static class Reg
                    {
                        public static Consumer Build()
                        {
                            Consumer consumer = new("a", new HttpClient());
                            return consumer;
                        }
                    }
                    """)),
            Row(ConsumerFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "(T x = new(…)): 'new HttpClient()'"),
        ["Factory fails: a method group"] = new(
            Sources(
                ("src/Lib/Fetcher.cs", """
                    namespace Lib;

                    public sealed class Fetcher
                    {
                        public Task<string> FetchAsync(HttpClient http, string path) => http.GetStringAsync(path);
                    }
                    """),
                ("src/Lib/Use.cs", """
                    namespace Lib;

                    public static class Use
                    {
                        public static async Task<string> RunAsync(Fetcher fetcher, IHttpClientFactory factory)
                        {
                            Func<HttpClient, string, Task<string>> fetch = fetcher.FetchAsync;
                            _ = fetch;
                            return await fetcher.FetchAsync(factory.CreateClient(), "/x");
                        }
                    }
                    """)),
            Row("src/Lib/Fetcher.cs", Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "a method group or other mention of 'FetchAsync'"),
        ["Factory fails: a private method fed Shared.Client"] = new(
            Sources(("src/Lib/Puller.cs", """
                namespace Lib;

                public sealed class Puller
                {
                    public Task<int> RunAsync() => PullAsync(Shared.Client, "/x");

                    private static async Task<int> PullAsync(HttpClient http, string path)
                    {
                        var body = await http.GetStringAsync(path);
                        return body.Length;
                    }
                }
                """)),
            Row("src/Lib/Puller.cs", Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "'Shared.Client' is not r.CreateClient"),
        ["Factory fails: a private method fed from a same-file method that returns new HttpClient()"] = new(
            Sources(("src/Lib/Puller.cs", """
                namespace Lib;

                public sealed class Puller
                {
                    public Task<int> RunAsync() => PullAsync(Build(), "/x");

                    private static HttpClient Build() => new HttpClient();

                    private static async Task<int> PullAsync(HttpClient http, string path)
                    {
                        var body = await http.GetStringAsync(path);
                        return body.Length;
                    }
                }
                """)),
            Row("src/Lib/Puller.cs", Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "something other than a factory client"),
        ["Factory fails: the receiving type is unsealed"] = new(
            Sources(
                (ConsumerFile, ConsumerSource.Replace("public sealed class", "public class", StringComparison.Ordinal)),
                (FactorySupplierFile, FactorySupplierSource)),
            Row(ConsumerFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "Consumer is not sealed"),
        ["Factory fails: a : this(raw) initializer"] = new(
            Sources(
                (ConsumerFile, """
                    namespace Lib;

                    public sealed class Consumer
                    {
                        private readonly HttpClient _http;

                        public Consumer()
                            : this("default", new HttpClient())
                        {
                        }

                        public Consumer(string name, HttpClient http)
                        {
                            _http = http;
                        }

                        public Task<string> GetAsync() => _http.GetStringAsync("/x");
                    }
                    """),
                (FactorySupplierFile, FactorySupplierSource)),
            Row(ConsumerFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "(: this(…)): 'new HttpClient()'"),
        ["Factory fails: ActivatorUtilities.CreateInstance<T>(sp, c)"] = new(
            Sources(
                (ConsumerFile, ConsumerSource),
                (FactorySupplierFile, FactorySupplierSource),
                ("src/Lib/Activated.cs", """
                    namespace Lib;

                    public static class Activated
                    {
                        public static Consumer Build(IServiceProvider sp, IHttpClientFactory factory) =>
                            ActivatorUtilities.CreateInstance<Consumer>(sp, "a", factory.CreateClient());
                    }
                    """)),
            Row(ConsumerFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "unknown supplier CreateInstance<Consumer>"),
        ["Factory fails: a partial type"] = new(
            Sources(
                (ConsumerFile, ConsumerSource.Replace("public sealed class", "public sealed partial class", StringComparison.Ordinal)),
                (FactorySupplierFile, FactorySupplierSource)),
            Row(ConsumerFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "Consumer is partial"),

        ["Factory fails: a non-private method of an unsealed type, called bare from a derived type in another file"] = new(
            Sources(
                ("src/Lib/SenderBase.cs", """
                    namespace Lib;

                    public abstract class SenderBase
                    {
                        protected static async Task<string> SendAsync(HttpClient http, string path) => await http.GetStringAsync(path);
                    }
                    """),
                ("src/Lib/FactorySender.cs", """
                    namespace Lib;

                    public sealed class FactorySender : SenderBase
                    {
                        private readonly IHttpClientFactory _factory;

                        public FactorySender(IHttpClientFactory factory) => _factory = factory;

                        public Task<string> RunAsync() => SendAsync(_factory.CreateClient("x"), "/x");
                    }
                    """),
                ("src/Lib/RawSender.cs", """
                    namespace Lib;

                    public sealed class RawSender : SenderBase
                    {
                        public Task<string> RunAsync() => SendAsync(new HttpClient(), "/x");
                    }
                    """)),
            Row("src/Lib/SenderBase.cs", Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "src/Lib/RawSender.cs:5 (N(…)): 'new HttpClient()' is not r.CreateClient"),

        // ── Factory (typed) ─────────────────────────────────────────────────────────────────────────────────
        ["Factory typed holds: a typed client of a Factory-classified registration (RunPod)"] = new(
            Sources((RunPodClientFile, RunPodClientSource), (RunPodExtensionsFile, RunPodExtensionsSource)),
            string.Join('\n',
                Row(RunPodClientFile, Marker.HttpParam, 1, 0, FactoryReason),
                Row(RunPodExtensionsFile, Marker.HttpRegister, 1, 1, "-", guardedBy: "Factory")),
            Expect: null),
        ["Factory typed fails: the registration member has no guard after it"] = new(
            Sources(
                (RunPodClientFile, RunPodClientSource),
                (RunPodExtensionsFile, RunPodExtensionsSource.Replace("services.AddAshlarEgressGuard();", string.Empty, StringComparison.Ordinal))),
            Row(RunPodClientFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "AddHttpClient<…RunPodHttpClient> is not Factory-classified"),
        ["Factory typed fails: also new X(raw)"] = new(
            Sources(
                (RunPodClientFile, RunPodClientSource),
                (RunPodExtensionsFile, RunPodExtensionsSource),
                ("src/Lib/Manual.cs", """
                    namespace Lib;

                    public static class Manual
                    {
                        public static IRunPodClient Make(ILogger<RunPodHttpClient> logger) => new RunPodHttpClient(new HttpClient(), logger);
                    }
                    """)),
            Row(RunPodClientFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "'new HttpClient()' is not r.CreateClient"),
        ["Factory typed fails: also ServiceDescriptor.Singleton<IX, X>()"] = new(
            Sources(
                (RunPodClientFile, RunPodClientSource),
                (RunPodExtensionsFile, RunPodExtensionsSource.Replace(
                    "        return services;",
                    "        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRunPodClient, RunPodHttpClient>());\n        return services;",
                    StringComparison.Ordinal))),
            Row(RunPodClientFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: "an empty-argument generic registration"),

        // ── Upstream (http.param) ───────────────────────────────────────────────────────────────────────────
        ["Upstream holds: the provider factory builds the client with EgressHttp (ProviderFactory)"] = new(
            Sources((OllamaProviderFile, OllamaProviderSource), (ProviderFactoryFile, ProviderFactorySource)),
            Row(OllamaProviderFile, Marker.HttpParam, 1, 0, UpstreamPrefix + ProviderFactoryFile),
            Expect: null),
        ["Upstream holds: self-supplied (MeshAutoPullService)"] = new(
            Sources(("application/src/App/MeshAutoPull.cs", """
                namespace App;

                public sealed class MeshAutoPull
                {
                    private static readonly HttpClient Http = BuildHttp();

                    private static HttpClient BuildHttp()
                    {
                        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
                        var http = EgressHttp.CreateClient(handler, EgressFamilies.MeshPull, "EG-MESH-04");
                        http.Timeout = TimeSpan.FromSeconds(15);
                        return http;
                    }

                    public async Task<int> PullAllAsync(string[] peers, CancellationToken ct)
                    {
                        var n = 0;
                        foreach (var peer in peers)
                            n += await PullPeerOnceAsync(Http, peer, ct);
                        return n;
                    }

                    public static async Task<int> PullPeerOnceAsync(HttpClient http, string peer, CancellationToken ct)
                    {
                        var body = await http.GetStringAsync(peer, ct);
                        return body.Length;
                    }
                }
                """)),
            Row("application/src/App/MeshAutoPull.cs", Marker.HttpParam, 1, 0, UpstreamPrefix + "application/src/App/MeshAutoPull.cs"),
            Expect: null),
        ["Upstream holds: a forwarded routed parameter (RemoteBrick from CompositeBrickRegistry)"] = new(
            Sources((RemoteBrickFile, RemoteBrickSource), (CompositeRegistryFile, CompositeRegistrySource), (MeshFile, MeshSource)),
            string.Join('\n',
                Row(CompositeRegistryFile, Marker.HttpParam, 1, 0, FactoryReason),
                Row(RemoteBrickFile, Marker.HttpParam, 1, 0, UpstreamPrefix + CompositeRegistryFile)),
            Expect: null),
        ["Upstream fails: a cycle"] = new(
            Sources(
                ("src/Lib/A.cs", """
                    namespace Lib;

                    public sealed class A
                    {
                        private readonly HttpClient _h;

                        public A(HttpClient h)
                        {
                            _h = h;
                        }

                        public B Next() => new B(_h);
                    }
                    """),
                ("src/Lib/B.cs", """
                    namespace Lib;

                    public sealed class B
                    {
                        private readonly HttpClient _h;

                        public B(HttpClient h)
                        {
                            _h = h;
                        }

                        public A Back() => new A(_h);
                    }
                    """)),
            string.Join('\n',
                Row("src/Lib/A.cs", Marker.HttpParam, 1, 0, UpstreamPrefix + "src/Lib/B.cs"),
                Row("src/Lib/B.cs", Marker.HttpParam, 1, 0, UpstreamPrefix + "src/Lib/A.cs")),
            Expect: "is part of a route cycle"),
        ["Upstream fails: a second constructing file"] = new(
            Sources(
                (OllamaProviderFile, OllamaProviderSource),
                (ProviderFactoryFile, ProviderFactorySource),
                ("src/Lib/Other.cs", """
                    namespace Lib;

                    public static class Other
                    {
                        public static OllamaProvider Make() =>
                            new OllamaProvider(EgressHttp.CreateClient(EgressFamilies.ModelLegacy, "EG-MDL-07"), "http://localhost:11434");
                    }
                    """)),
            Row(OllamaProviderFile, Marker.HttpParam, 1, 0, UpstreamPrefix + ProviderFactoryFile),
            Expect: "supplies OllamaProvider constructor outside src/Lib/ProviderFactory.cs"),
        ["Upstream fails: the named file builds a raw client"] = new(
            Sources(
                (OllamaProviderFile, OllamaProviderSource),
                (ProviderFactoryFile, ProviderFactorySource.Replace(
                    "var httpClient = EgressHttp.CreateClient(EgressFamilies.ModelLegacy, \"EG-MDL-07\");",
                    "var httpClient = new HttpClient();",
                    StringComparison.Ordinal))),
            Row(OllamaProviderFile, Marker.HttpParam, 1, 0, UpstreamPrefix + ProviderFactoryFile),
            Expect: "builds 1 unguarded http.new"),
        ["Upstream fails: the named file supplies nothing"] = new(
            Sources(
                (OllamaProviderFile, OllamaProviderSource),
                (ProviderFactoryFile, """
                    namespace Lib;

                    public class ProviderFactory
                    {
                        private static readonly HttpClient Http = EgressHttp.CreateClient(EgressFamilies.ModelLegacy, "EG-MDL-03");

                        public Task<string> ProbeAsync() => Http.GetStringAsync("https://example.invalid/");
                    }
                    """),
                ("src/Lib/Other.cs", """
                    namespace Lib;

                    public static class Other
                    {
                        public static OllamaProvider Make() =>
                            new OllamaProvider(EgressHttp.CreateClient(EgressFamilies.ModelLegacy, "EG-MDL-07"), "http://localhost:11434");
                    }
                    """)),
            Row(OllamaProviderFile, Marker.HttpParam, 1, 0, UpstreamPrefix + ProviderFactoryFile),
            Expect: "src/Lib/ProviderFactory.cs supplies nothing to this row"),
        ["Upstream fails: the named file's http.param row is Exempt:TestSeam"] = new(
            Sources((RemoteBrickFile, RemoteBrickSource), (CompositeRegistryFile, CompositeRegistrySource), (MeshFile, MeshSource)),
            string.Join('\n',
                Row(CompositeRegistryFile, Marker.HttpParam, 1, 0, "Exempt:TestSeam"),
                Row(RemoteBrickFile, Marker.HttpParam, 1, 0, UpstreamPrefix + CompositeRegistryFile)),
            Expect: "is pinned 'Exempt:TestSeam', not Factory, Upstream or Governance"),
        ["Upstream fails: the named file has public HttpClient Client { get; set; }"] = new(
            Sources(
                (OllamaProviderFile, OllamaProviderSource),
                (ProviderFactoryFile, ProviderFactorySource.Replace(
                    "    private HttpClient? _ollamaHttpClient;",
                    "    private HttpClient? _ollamaHttpClient;\n\n    public HttpClient? Client { get; set; }",
                    StringComparison.Ordinal))),
            Row(OllamaProviderFile, Marker.HttpParam, 1, 0, UpstreamPrefix + ProviderFactoryFile),
            Expect: "the HttpClient member 'Client' is not private"),

        // ── Upstream (http.register) ────────────────────────────────────────────────────────────────────────
        ["Upstream holds: a registration in a project that cannot reach Infrastructure, composed by Fleet.Host"] = new(
            Sources((FleetExtensionsFile, FleetExtensionsSource), (FleetHostProgramFile, FleetHostProgramSource)),
            FleetRow,
            Expect: null,
            FleetCsprojs()),
        ["Upstream fails: the named program lacks the guard"] = new(
            Sources(
                (FleetExtensionsFile, FleetExtensionsSource),
                (FleetHostProgramFile, FleetHostProgramSource.Replace("builder.Services.AddAshlarEgressGuard();", string.Empty, StringComparison.Ordinal))),
            FleetRow,
            Expect: "commercial/src/Fleet.Host/Program.cs never calls AddAshlarEgressGuard",
            FleetCsprojs()),
        ["Upstream fails: the named program's project does not reference the row's project"] = new(
            Sources((FleetExtensionsFile, FleetExtensionsSource), (FleetHostProgramFile, FleetHostProgramSource)),
            FleetRow,
            Expect: "does not reference commercial/src/Fleet.Infrastructure/Fleet.Infrastructure.csproj",
            FleetCsprojs(host: """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <ItemGroup>
                    <ProjectReference Include="..\..\..\src\Ashlar.Infrastructure\Ashlar.Infrastructure.csproj" />
                  </ItemGroup>
                </Project>
                """)),
        ["Upstream fails: a second production executable composes the project without the guard"] = new(
            Sources(
                (FleetExtensionsFile, FleetExtensionsSource),
                (FleetHostProgramFile, FleetHostProgramSource),
                ("commercial/src/Fleet.Worker/Program.cs", """
                    using Fleet.Infrastructure;

                    var builder = Host.CreateApplicationBuilder(args);
                    builder.Services.AddFleet();
                    builder.Build().Run();
                    """)),
            FleetRow,
            Expect: "commercial/src/Fleet.Worker/Fleet.Worker.csproj composes commercial/src/Fleet.Infrastructure/Fleet.Infrastructure.csproj and calls AddAshlarEgressGuard in none of its files",
            new Dictionary<string, string>(FleetCsprojs(), StringComparer.Ordinal)
            {
                ["commercial/src/Fleet.Worker/Fleet.Worker.csproj"] = """
                    <Project Sdk="Microsoft.NET.Sdk.Worker">
                      <ItemGroup>
                        <ProjectReference Include="../Fleet.Infrastructure/Fleet.Infrastructure.csproj" />
                      </ItemGroup>
                    </Project>
                    """,
            }),
        ["Upstream fails: the row's project can reach Ashlar.Infrastructure"] = new(
            Sources((FleetExtensionsFile, FleetExtensionsSource), (FleetHostProgramFile, FleetHostProgramSource)),
            FleetRow,
            Expect: "this project can reach AddAshlarEgressGuard; call it after the registration",
            FleetCsprojs(infrastructure: """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="../../../src/Ashlar.Infrastructure/Ashlar.Infrastructure.csproj" />
                  </ItemGroup>
                </Project>
                """)),

        // ── Governance ──────────────────────────────────────────────────────────────────────────────────────
        ["Governance holds: a governed pair while F5 is green"] = new(
            Sources(
                (OllamaChatClientFile, OllamaChatClientSource),
                (MeaiRegistrationFile, GovernedMeaiSource),
                (GovernanceFile, GovernanceSource)),
            GovernedTsv,
            Expect: null),
        ["Governance fails outside the four governed pairs"] = new(
            Sources((ProbeFile, ProbeSource)),
            Row(ProbeFile, Marker.HttpParam, 1, 0, GovernanceReason),
            Expect: "Governance is allowed only for the four governed MEAI pairs"),
        ["Governance fails while F5 is red: a keyed client with no UseAshlarGovernance"] = new(
            Sources(
                (OllamaChatClientFile, OllamaChatClientSource),
                (MeaiRegistrationFile, GovernedMeaiSource.Replace(
                    ".UseAshlarGovernance(MeaiTargetKeys.LocalOnnx);", ";", StringComparison.Ordinal)),
                (GovernanceFile, GovernanceSource)),
            Row(OllamaChatClientFile, Marker.HttpNew, 1, 0, GovernanceReason),
            Expect: "Governance needs F5 green:"),

        // ── F5 confinement, through a Governance row ────────────────────────────────────────────────────────
        ["Governance fails while F5 is red: OllamaHttpChatClient named outside the MEAI file"] = new(
            Sources(
                (OllamaChatClientFile, OllamaChatClientSource),
                (MeaiRegistrationFile, GovernedMeaiSource),
                (GovernanceFile, GovernanceSource),
                ("src/Lib/Rogue.cs", """
                    namespace Lib;

                    public static class Rogue
                    {
                        public static Type Kind => typeof(OllamaHttpChatClient);
                    }
                    """)),
            GovernedTsv,
            Expect: "src/Lib/Rogue.cs:5: 'OllamaHttpChatClient' outside"),
        ["Governance fails while F5 is red: GetRequiredService<IBedrockChatClientFactory>() outside a governed statement"] = new(
            Sources(
                (OllamaChatClientFile, OllamaChatClientSource),
                (MeaiRegistrationFile, GovernedMeaiSource.Replace(
                    "//BEDROCK-OUTSIDE",
                    "var bedrock = services.BuildServiceProvider().GetRequiredService<IBedrockChatClientFactory>();",
                    StringComparison.Ordinal)),
                (GovernanceFile, GovernanceSource)),
            GovernedTsv,
            Expect: "'IBedrockChatClientFactory' outside"),

        // ── Reason checks ───────────────────────────────────────────────────────────────────────────────────
        ["Reason fails: ConsumerSdk outside src/Ashlar.Client/"] = new(
            Sources((ProbeFile, ProbeSource)),
            Row(ProbeFile, Marker.HttpParam, 1, 0, "Exempt:ConsumerSdk"),
            Expect: "Exempt:ConsumerSdk only under src/Ashlar.Client/"),
        ["Reason holds: ConsumerSdk under src/Ashlar.Client/"] = new(
            Sources(("src/Ashlar.Client/Probe.cs", ProbeSource)),
            Row("src/Ashlar.Client/Probe.cs", Marker.HttpParam, 1, 0, "Exempt:ConsumerSdk"),
            Expect: null),
        ["Reason fails: Unrouted"] = new(
            Sources((ProbeFile, ProbeSource)),
            Row(ProbeFile, Marker.HttpParam, 1, 0, "Unrouted"),
            Expect: "`Unrouted` was removed by SPEC-007 PR 3b: route the site"),
        ["Reason fails: Factory on an http.new row"] = new(
            Sources(("src/Lib/Raw.cs", """
                namespace Lib;

                public static class Raw
                {
                    public static HttpClient Make() => new HttpClient();
                }
                """)),
            Row("src/Lib/Raw.cs", Marker.HttpNew, 1, 0, FactoryReason),
            Expect: "Factory is for http.param"),
        ["Reason fails: Factory on an http.register row"] = new(
            Sources(("src/Lib/Registration.cs", """
                namespace Lib;

                public static class Registration
                {
                    public static IServiceCollection AddThing(this IServiceCollection services)
                    {
                        services.AddHttpClient("x");
                        return services;
                    }
                }
                """)),
            Row("src/Lib/Registration.cs", Marker.HttpRegister, 1, 0, FactoryReason),
            Expect: "Factory is for http.param"),

        ["Reason fails: an http.register pinned with an Exempt reason other than ConsumerSdk"] = new(
            Sources(("src/Lib/Registration.cs", """
                namespace Lib;

                public static class Registration
                {
                    public static IServiceCollection AddThing(this IServiceCollection services)
                    {
                        services.AddHttpClient("x");
                        return services;
                    }
                }
                """)),
            Row("src/Lib/Registration.cs", Marker.HttpRegister, 1, 0, "Exempt:Operator"),
            Expect: "unguarded_reason 'Exempt:Operator' is not allowed for http.register"),
        ["Reason fails: Exempt:GuardImpl outside the guard's folder"] = new(
            Sources(("src/Lib/Raw.cs", """
                namespace Lib;

                public static class Raw
                {
                    public static HttpClient Make() => new HttpClient();
                }
                """)),
            Row("src/Lib/Raw.cs", Marker.HttpNew, 1, 0, GuardImplReason),
            Expect: "Exempt:GuardImpl is accepted only for http.new under src/Ashlar.Abstractions/Security/Egress/"),
        ["Reason holds: Exempt:GuardImpl for the guard's own construction"] = new(
            Sources(("src/Ashlar.Abstractions/Security/Egress/EgressHttp.cs", """
                namespace Ashlar.Abstractions.Security.Egress;

                public static class EgressHttp
                {
                    public static HttpClient CreateClient(HttpMessageHandler guarded) => new HttpClient(guarded);
                }
                """)),
            Row("src/Ashlar.Abstractions/Security/Egress/EgressHttp.cs", Marker.HttpNew, 1, 0, GuardImplReason),
            Expect: null),
        ["Upstream fails: the named file is not grounded"] = new(
            Sources(
                (OllamaProviderFile, OllamaProviderSource),
                (ProviderFactoryFile, """
                    namespace Lib;

                    public class ProviderFactory
                    {
                        public OllamaProvider Create(string baseUrl) => new OllamaProvider(Shared.Client, baseUrl);
                    }
                    """),
                ("src/Lib/Shared.cs", """
                    namespace Lib;

                    public static class Shared
                    {
                        public static readonly HttpClient Client = new HttpClient();
                    }
                    """)),
            Row(OllamaProviderFile, Marker.HttpParam, 1, 0, UpstreamPrefix + ProviderFactoryFile),
            Expect: "src/Lib/ProviderFactory.cs is not grounded"),

        // ── Known misses: hold, pinned, so the change that teaches the scan must flip them ──────────────────
        ["known miss: an Upstream supplier passes a static client defined in another file"] = new(
            Sources(
                (OllamaProviderFile, OllamaProviderSource),
                (ProviderFactoryFile, ProviderFactorySource.Replace(
                    "_ollama = new OllamaProvider(httpClient, baseUrl);",
                    "_ollama = new OllamaProvider(Shared.Client, baseUrl);",
                    StringComparison.Ordinal)),
                ("src/Lib/Shared.cs", """
                    namespace Lib;

                    public static class Shared
                    {
                        public static readonly HttpClient Client = new HttpClient();
                    }
                    """)),
            Row(OllamaProviderFile, Marker.HttpParam, 1, 0, UpstreamPrefix + ProviderFactoryFile),
            Expect: null),
        ["known miss: an IHttpClientFactory-typed receiver backed by a custom implementation (only F4 (D) catches it)"] = new(
            Sources(
                (ConsumerFile, ConsumerSource),
                (FactorySupplierFile, FactorySupplierSource),
                ("src/Lib/MyFactory.cs", """
                    namespace Lib;

                    public sealed class MyFactory : IHttpClientFactory
                    {
                        public HttpClient CreateClient(string name) => new HttpClient();
                    }
                    """)),
            Row(ConsumerFile, Marker.HttpParam, 1, 0, FactoryReason),
            Expect: null),
    };
}
