using System.Text;
using System.Text.RegularExpressions;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Reading <c>ci/egress-inventory.tsv</c> and <c>docs/EgressInventory.md</c>, and printing the observed rows
/// in the TSV's own form so a maintainer can update the pins mechanically.
/// </summary>
public sealed partial class EgressGuardConventionTests
{
    private const string InventoryRelativePath = "ci/egress-inventory.tsv";

    private const string DocsRelativePath = "docs/EgressInventory.md";

    private static readonly string[] Columns =
        ["path", "marker", "total", "guarded", "guarded_by", "unguarded_reason", "ids", "note"];

    /// <summary>
    /// The 3a reason set. <c>Unrouted</c> is temporary: "listed, not routed yet; 3b routes it", and 3b deletes
    /// it from this set. The <c>Exempt:</c> reasons are final. <c>Governance</c>, <c>Factory</c> and
    /// <c>Upstream:&lt;path&gt;</c> arrive with 3b, together with the F3 checks that verify each of them
    /// (convention_test.md, F3 bullets 2-4); until then a row that names one fails F3.
    /// </summary>
    private const string Unrouted = "Unrouted";

    private static readonly string[] ExemptReasons =
        ["LocalOnly", "Operator", "Inbound", "DataStore", "LocalDaemon", "ConsumerSdk", "TestDouble", "TestSeam"];

    /// <summary>
    /// Option (b) of SCOPE-3a for the guard's own constructions: <c>EgressHttp.cs</c> must build
    /// <c>new HttpClient(</c> and <c>new HttpClientHandler(</c> to hand anyone a guarded client, and those are
    /// not a bypass. They are pinned as <c>Exempt:GuardImpl</c>, which F3 accepts only under this folder.
    /// </summary>
    private const string GuardImplReason = "Exempt:GuardImpl";

    private const string GuardImplFolder = "src/Ashlar.Abstractions/Security/Egress/";

    private static readonly Regex EgId = new(@"^EG-[A-Z]+-\d+$", RegexOptions.CultureInvariant);

    /// <summary>One data row of the TSV, as written.</summary>
    private sealed record Pin(
        int Line, string Path, string Marker, int Total, int Guarded, string GuardedBy, string Reason, string[] Ids, string IdsText, string Note)
    {
        public int Unguarded => Total - Guarded;

        public string Key => Path + "\t" + Marker;
    }

    private sealed record PinFile(List<Pin> Rows, List<string> Errors);

    /// <summary>One observed (path, marker) pair, from the scan.</summary>
    private sealed record ObservedRow(string Path, string Marker, List<Occurrence> Occurrences)
    {
        public int Total => Occurrences.Count;

        public int Guarded => Occurrences.Count(o => o.Guarded);

        public int Unguarded => Total - Guarded;

        public string Key => Path + "\t" + Marker;

        public string GuardedBy =>
            string.Join("+", Occurrences.Where(o => o.Guarded).Select(o => o.Guard.ToString()).Distinct().OrderBy(s => s, StringComparer.Ordinal)) is { Length: > 0 } kinds
                ? kinds
                : "-";

        public string Lines => string.Join(",", Occurrences.Select(o => o.Line));
    }

    /// <summary>Every observed pair except the banned marker, which is never pinned (F4 holds it at zero).</summary>
    private static List<ObservedRow> Observed(TreeScan scan) =>
        scan.Occurrences
            .Where(o => o.Marker != Marker.Banned)
            .GroupBy(o => (o.Path, o.Marker))
            .Select(g => new ObservedRow(g.Key.Path, g.Key.Marker, [.. g.OrderBy(o => o.Offset)]))
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.Marker, StringComparer.Ordinal)
            .ToList();

    private static PinFile ReadPins(string root)
    {
        var rows = new List<Pin>();
        var errors = new List<string>();
        var path = Path.Combine(root, InventoryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            errors.Add($"{InventoryRelativePath} does not exist");
            return new PinFile(rows, errors);
        }

        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var where = $"{InventoryRelativePath}:{i + 1}";
            var parts = line.Split('\t');
            if (parts.Length != Columns.Length)
            {
                errors.Add($"{where}: {parts.Length} tab-separated fields, expected {Columns.Length} ({string.Join(", ", Columns)})");
                continue;
            }

            if (!int.TryParse(parts[2], out var total) || !int.TryParse(parts[3], out var guarded))
            {
                errors.Add($"{where}: total '{parts[2]}' and guarded '{parts[3]}' must be integers");
                continue;
            }

            var ids = parts[6] == "-"
                ? Array.Empty<string>()
                : parts[6].Split(',').Select(s => s.Trim()).ToArray();
            rows.Add(new Pin(i + 1, parts[0], parts[1], total, guarded, parts[4], parts[5], ids, parts[6], parts[7]));
        }

        return new PinFile(rows, errors);
    }

    /// <summary>
    /// The whole observed inventory in TSV form. Pinned rows keep their reason, ids and note; a new row gets
    /// <c>?</c> where a human decision is needed, which F3 and F8 refuse until someone makes it.
    /// </summary>
    private static string RenderObserved(IEnumerable<ObservedRow> observed, IReadOnlyList<Pin> pins)
    {
        var byKey = pins.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var text = new StringBuilder();
        text.AppendLine("---- observed rows (paste over the data rows of " + InventoryRelativePath + ", then review every '?') ----");
        text.AppendLine(string.Join('\t', Columns));
        foreach (var row in observed)
        {
            byKey.TryGetValue(row.Key, out var pin);
            var reason = row.Unguarded == 0 ? "-" : pin is not null && pin.Reason != "-" ? pin.Reason : "?";
            var ids = pin?.IdsText ?? "?";
            var note = pin?.Note ?? "? lines " + row.Lines;
            text.AppendLine(string.Join('\t', row.Path, row.Marker, row.Total, row.Guarded, row.GuardedBy, reason, ids, note));
        }

        text.Append("---- end of observed rows ----");
        return text.ToString();
    }

    /// <summary>One <c>| EG-… |</c> row of the inventory document and its route cell.</summary>
    private sealed record DocRow(string Id, string? Route, int Line);

    private static readonly Regex TableSeparator = new(@"^\|\s*:?-{3,}", RegexOptions.CultureInvariant);

    private static readonly Regex UnescapedPipe = new(@"(?<!\\)\|", RegexOptions.CultureInvariant);

    /// <summary>
    /// The EG rows of <c>docs/EgressInventory.md</c>. The route is the cell under the header that says
    /// "route" ("PR 3 route", "Route (3b)"). The inbound-server table has no route column because the document
    /// states, once, that all of its rows are <c>Unscanned:Inbound</c>; so an <c>EG-SRV-</c> row there reads as
    /// that route, and any other row without a route is reported, not guessed.
    /// </summary>
    private static (List<DocRow> Rows, List<string> Errors) ReadDocs(string root)
    {
        var rows = new List<DocRow>();
        var errors = new List<string>();
        var path = Path.Combine(root, DocsRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            errors.Add($"{DocsRelativePath} does not exist; it is the written half of the inventory and every TSV id must be a row in it");
            return (rows, errors);
        }

        var lines = File.ReadAllLines(path);
        for (var i = 0; i + 1 < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith('|') || !TableSeparator.IsMatch(lines[i + 1].TrimStart()))
                continue;

            var header = Cells(lines[i]);
            var routeColumn = header.FindIndex(h => h.Contains("route", StringComparison.OrdinalIgnoreCase));
            var k = i + 2;
            for (; k < lines.Length && lines[k].TrimStart().StartsWith('|'); k++)
            {
                var cells = Cells(lines[k]);
                if (cells.Count == 0 || !EgId.IsMatch(cells[0]))
                    continue;

                string? route = routeColumn >= 0 && routeColumn < cells.Count
                    ? cells[routeColumn]
                    : routeColumn < 0 && cells[0].StartsWith("EG-SRV-", StringComparison.Ordinal) ? "Unscanned:Inbound" : null;
                if (route is null)
                    errors.Add($"{DocsRelativePath}:{k + 1}: {cells[0]} has no route cell");
                rows.Add(new DocRow(cells[0], route, k + 1));
            }

            i = k - 1;
        }

        foreach (var duplicate in rows.GroupBy(r => r.Id).Where(g => g.Count() > 1))
            errors.Add($"{DocsRelativePath}: {duplicate.Key} is defined on lines {string.Join(", ", duplicate.Select(r => r.Line))}");

        return (rows, errors);
    }

    private static List<string> Cells(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
            trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal))
            trimmed = trimmed[..^1];
        return UnescapedPipe.Split(trimmed).Select(c => c.Trim()).ToList();
    }

    private static bool IsUnscanned(string? route) =>
        route is not null && route.Replace("`", string.Empty, StringComparison.Ordinal).TrimStart()
            .StartsWith("Unscanned:", StringComparison.Ordinal);
}
