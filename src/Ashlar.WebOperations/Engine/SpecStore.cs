using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Ashlar.WebOperations.Specs;

namespace Ashlar.WebOperations;

/// <summary>
/// Where specs live: the ones bundled with this assembly, plus learned and repaired versions in a local directory
/// (<c>{dir}/{site}/v{N}.json</c>). The highest version wins; <see cref="Revert"/> drops the newest local version, so a
/// bad repair is one call from undone. Without a directory, learned and repaired versions live in memory only.
/// </summary>
public sealed class SpecStore
{
    private readonly string? _dir;
    private readonly ConcurrentDictionary<string, SortedList<int, SiteSpec>> _versions = new(StringComparer.Ordinal);
    private readonly object _write = new();

    /// <summary>Creates a store with the bundled specs and, when given, the specs saved under <paramref name="directory"/>.</summary>
    public SpecStore(string? directory = null, bool includeBundled = true)
    {
        _dir = directory;
        if (includeBundled)
            foreach (var spec in Bundled())
                Add(spec);
        if (_dir is not null && Directory.Exists(_dir))
            foreach (var file in Directory.EnumerateFiles(_dir, "v*.json", SearchOption.AllDirectories))
                Add(SiteSpec.Parse(File.ReadAllText(file)));
    }

    /// <summary>The specs shipped in this assembly.</summary>
    public static IReadOnlyList<SiteSpec> Bundled()
    {
        var asm = typeof(SpecStore).Assembly;
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("Ashlar.WebOperations.Specs.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var s = asm.GetManifestResourceStream(n)!;
                using var r = new StreamReader(s);
                return SiteSpec.Parse(r.ReadToEnd());
            }).ToList();
    }

    private void Add(SiteSpec spec) =>
        _versions.GetOrAdd(spec.Site, _ => new SortedList<int, SiteSpec>())[spec.Version] = spec;

    /// <summary>The newest version of a site, or null.</summary>
    public SiteSpec? Get(string site)
    {
        lock (_write)
            return _versions.TryGetValue(site, out var v) && v.Count > 0 ? v.Values[^1] : null;
    }

    /// <summary>Every site's newest version.</summary>
    public IReadOnlyList<SiteSpec> All()
    {
        lock (_write)
            return _versions.Values.Where(v => v.Count > 0).Select(v => v.Values[^1]).OrderBy(s => s.Site, StringComparer.Ordinal).ToList();
    }

    /// <summary>Saves a new version of a site (version = newest + 1).</summary>
    public SiteSpec Save(SiteSpec spec)
    {
        lock (_write)
        {
            var next = spec with { Version = (Get(spec.Site)?.Version ?? 0) + 1 };
            next.Validate();
            Add(next);
            if (_dir is not null)
            {
                var folder = Path.Combine(_dir, next.Site);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, $"v{next.Version}.json"), next.ToJson());
            }
            return next;
        }
    }

    /// <summary>Saves a repaired operation as a new version of its site.</summary>
    public SiteSpec SaveRepair(SiteSpec current, string operation, OperationSpec repaired, string change)
    {
        var ops = new Dictionary<string, OperationSpec>(current.Operations, StringComparer.Ordinal) { [operation] = repaired };
        return Save(current with { Operations = ops, Source = $"{current.Source}; repaired v{current.Version} {operation}: {change}" });
    }

    /// <summary>Removes the newest version of a site (never a bundled one). Returns the version now in force, or null.</summary>
    public SiteSpec? Revert(string site)
    {
        lock (_write)
        {
            if (!_versions.TryGetValue(site, out var v) || v.Count < 2)
                throw new InvalidOperationException($"{site} has no newer version to revert");
            var newest = v.Values[^1];
            v.RemoveAt(v.Count - 1);
            if (_dir is not null)
            {
                var file = Path.Combine(_dir, site, $"v{newest.Version}.json");
                if (File.Exists(file)) File.Move(file, file + ".reverted", overwrite: true); // kept, not deleted
            }
            return Get(site);
        }
    }
}

/// <summary>
/// Repair without a second request: when the data an operation reads has moved inside the response, find the array of
/// objects that still carries every field the spec's shape requires, and point the operation at it. Only the response
/// that was just received is read, so a repair can never return another request's data.
/// </summary>
public static class Healer
{
    /// <summary>A repaired copy of <paramref name="op"/>, or null when nothing in the document fits its shape.</summary>
    public static OperationSpec? TryRepairExtract(OperationSpec op, JsonNode document, IReadOnlyDictionary<string, string> args)
    {
        if (!op.Response.List || op.Response.Shape.Count == 0)
            return null; // a single object or an operation with no declared shape cannot be re-identified safely
        foreach (var (path, array) in JsonPath.ObjectArrays(document))
        {
            if (path == op.Response.Extract)
                continue;
            if (array.All(item => op.Response.Shape.All(k => JsonPath.Exists(item, k))))
                return op with { Response = op.Response with { Extract = path } };
        }
        return null;
    }
}
