using System.Text.RegularExpressions;
using FluentAssertions;
using Ashlar.Core.Application.Paths;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// A store method that reads a document and then writes one derived from it must do both inside a
/// single LiteDB transaction.
///
/// <para><b>Why this blocks a merge.</b> #594 made every store open <c>Connection=Shared</c>, which
/// serialises the engine through a named mutex — but that mutex is taken and released PER OPERATION,
/// not for the lifetime of the <c>LiteDatabase</c>. A read followed by a write is two operations, so
/// another writer commits in between and both writers then persist a value derived from the same
/// snapshot. Measured in the Linux devtest container on
/// <c>LiteDbUserKnowledgeLogStore.UpsertAsync</c>, 4 threads x 100 updates to ONE id, counting the
/// surviving <c>Version</c>: Direct kept 219 of 400 and Shared kept 358 of 400, with ZERO exceptions
/// in either mode. Shared did not fix this; it narrowed it, which is worse, because the remaining
/// loss looks like nothing at all.</para>
///
/// <para><b>Why a convention test and not seven edits.</b> This is the fourth round of a
/// concurrency fix in the LiteDB stores. #586 converted one store's mapper usage and left the rest,
/// #591 had to finish it, #594 had to do the connection mode for all twelve at once, and each time
/// the shape of the failure was the same: the edits landed, nothing froze them, and the next store
/// to arrive was written the old way. The seven edits are the smaller half of this fix.</para>
///
/// <para><b>Four facts.</b> <see cref="Every_read_modify_write_is_inventoried"/> fails when a new
/// store method pairs a read with a write and is not on the list — that is the new-offender half.
/// <see cref="No_inventory_row_has_stopped_doing_a_read_modify_write"/> fails on a stale row, because
/// a row describing a pair that no longer exists reads as accounted-for debt and quietly turns a
/// frozen inventory into a blanket approval — that is the stale-row half.
/// <see cref="Every_inventoried_read_modify_write_opens_a_transaction"/> is the one the inventory
/// cannot express: being on the list is an admission that the pair exists, not permission to leave it
/// unguarded, so each listed member must reach <c>LiteDbAtomic.Mutate</c>.
/// <see cref="No_store_chains_a_call_straight_onto_GetCollection"/> closes this scan's only blind
/// spot — it attributes reads and writes by the collection VARIABLE they are called on, so a store
/// that wrote <c>db.GetCollection&lt;T&gt;("c").Update(doc)</c> inline would be invisible to the
/// other three. Binding the collection to a local is therefore part of the convention.</para>
///
/// <para><b>What this guard does NOT catch.</b> It reads text within a single member, so it only sees
/// a pair whose halves are both inside one store method. The other shape is a caller that reads
/// through one store method, decides, and writes through another — the database is opened and closed
/// in between, so no transaction can span it and no text scan of the stores can see it. Those sites
/// are real and they are still open at the time of writing:
/// <c>CommercialFleetEndpoints.PatchMeshTaskStatusAsync</c> (reads and COMPARES
/// <c>MeshTaskDoc.LeaseToken</c>, then writes without it as a precondition),
/// <c>MeshTaskPlacementService.TryPlaceAsync</c>, <c>MeshTaskExecutionService.ExtendLeaseAsync</c> and
/// <c>MigrateForCheckpointAsync</c>, <c>MeshLeaseSweepBackgroundService</c>,
/// <c>MeshPendingTaskRebalancerBackgroundService</c>, <c>PipelineOrchestrator</c>'s resume path, and
/// <c>SelfImprovementLoop</c>'s <c>IsProcessed</c>/<c>MarkProcessed</c> guard. Closing those needs a
/// compare-and-swap precondition on the port (<c>LeaseToken</c> is the natural token for the mesh
/// ones; <c>LiteDbPatternProcessedStore</c> wants a unique index instead), which is a port change
/// rather than a store change. Do not read this test's silence about them as a claim they are safe.
/// The behavioural half of THIS fix lives in
/// <c>Tests/Persistence/LiteDbAtomicReadModifyWriteTests</c>, which races N threads over one id and
/// counts surviving updates — assert on counts there, never on an expected exception, because this
/// race throws nothing on any platform.</para>
///
/// <para>Hermetic: pure file reads, no build, no network, no SDK — the same discipline and the same
/// directory pruning as <see cref="LiteDbSharedModeConventionTests"/>, whose shape this mirrors.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class LiteDbAtomicWriteConventionTests
{
    /// <summary>Every door LiteDB offers into a file; kept identical to the Shared-mode guard's list.</summary>
    private static readonly string[] DatabaseConstructions =
    [
        "new LiteDatabase(",
        "new LiteRepository(",
        "new LiteEngine(",
        "new SharedEngine(",
    ];

    /// <summary>The helper that makes a read and a write one operation.</summary>
    private const string HelperCall = "LiteDbAtomic.Mutate";

    /// <summary>Calls that read a document out of a collection.</summary>
    private static readonly string[] ReadCalls =
    [
        "FindById", "FindOne", "FindAll", "Find", "Query", "Exists", "Count", "LongCount",
    ];

    /// <summary>Calls that put one back.</summary>
    private static readonly string[] WriteCalls =
    [
        "Insert", "InsertBulk", "Update", "UpdateMany", "Upsert", "Delete", "DeleteMany", "DeleteAll",
    ];

    /// <summary>Same production trees as the Shared-mode guard, for the same reason.</summary>
    private static readonly string[] ProductionRoots =
    [
        "src", "application", "applications", "commercial",
        "tools", "products", "extensions", "apps", "samples", "spikes", "consumer-template",
    ];

    /// <summary>
    /// Every production store method that reads a document and writes one derived from it, known on
    /// 2026-09-11, as <c>repo-root-relative-path::MemberName</c>. Seven pairs across three files.
    /// This list is allowed to go DOWN — a method that stops pairing a read with a write deletes its
    /// row — and it may not go up by accident: a new row is a new lost-update surface and it must be
    /// argued for in a diff a reviewer sees.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "commercial/src/Ashlar.Commercial.Fleet.Infrastructure/LiteDbFleetNodeRegistry.cs::HeartbeatAsync",
        "commercial/src/Ashlar.Commercial.Fleet.Infrastructure/LiteDbFleetNodeRegistry.cs::SetAdmittedAsync",
        "commercial/src/Ashlar.Commercial.Fleet.Infrastructure/LiteDbFleetNodeRegistry.cs::SetDrainedAsync",
        "commercial/src/Ashlar.Commercial.Fleet.Infrastructure/LiteDbMeshTaskRegistry.cs::CreateAsync",
        "commercial/src/Ashlar.Commercial.Fleet.Infrastructure/LiteDbMeshTaskRegistry.cs::UpdateAsync",
        "src/Ashlar.Infrastructure/Trust/LiteDbUserKnowledgeLogStore.cs::DeleteAsync",
        "src/Ashlar.Infrastructure/Trust/LiteDbUserKnowledgeLogStore.cs::UpsertAsync",
    };

    [Fact]
    public void Every_read_modify_write_is_inventoried()
    {
        var root = RepoPathResolver.FindRepoRoot();

        var unlisted = Pairs(root).Keys
            .Where(key => !Allowed.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        unlisted.Should().BeEmpty(
            "a read whose value decides the write that follows it is a lost update unless the two are "
            + "one operation. Shared mode releases its mutex between them and nothing throws when the "
            + "update vanishes — measured at 4 threads x 100 updates to one id, 358 of 400 survived. "
            + "Wrap the pair in {0}, then add the member to the inventory in this test and say why in "
            + "the pull request. Unlisted: {1}",
            HelperCall,
            string.Join(", ", unlisted));
    }

    /// <summary>
    /// A stale inventory row is its own failure: it reads as a known, accounted-for pair that is in
    /// fact gone, which is how a frozen inventory quietly becomes a blanket approval.
    /// </summary>
    [Fact]
    public void No_inventory_row_has_stopped_doing_a_read_modify_write()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var actual = Pairs(root);

        var stale = Allowed.Where(a => !actual.ContainsKey(a))
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        stale.Should().BeEmpty(
            "these members no longer read a document and then write one, so their rows describe a "
            + "hazard that is not there. Delete the rows with the pairs. Stale: {0}",
            string.Join(", ", stale));
    }

    /// <summary>
    /// The invariant the inventory cannot express. Being on the list admits the pair exists; it is
    /// not permission to leave it separable.
    /// </summary>
    [Fact]
    public void Every_inventoried_read_modify_write_opens_a_transaction()
    {
        var root = RepoPathResolver.FindRepoRoot();

        var offenders = Pairs(root)
            .Where(pair => !pair.Value.Contains(HelperCall, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "the read and the write have to be one operation, and {0} is the only place that is "
            + "spelled. LiteDB's SharedEngine acquires its named mutex on BeginTrans and holds it "
            + "until Commit, so the pair becomes atomic against another thread, another store "
            + "instance, and the CLI running in a second process on the same state directory. "
            + "Offenders: {1}",
            HelperCall,
            string.Join(", ", offenders));
    }

    /// <summary>
    /// This scan attributes a call to the collection VARIABLE it is made on, so an inline
    /// <c>db.GetCollection&lt;T&gt;("c").Update(doc)</c> would be a read-modify-write none of the
    /// other three facts could see. Binding the collection to a local is part of the convention, not
    /// a style preference.
    /// </summary>
    [Fact]
    public void No_store_chains_a_call_straight_onto_GetCollection()
    {
        var root = RepoPathResolver.FindRepoRoot();

        var offenders = new List<string>();
        foreach (var relative in Stores(root))
        {
            var text = File.ReadAllText(Path.Combine(root, relative));
            if (ChainsOntoGetCollection(text))
                offenders.Add(relative);
        }

        offenders.Should().BeEmpty(
            "assign the collection to a local first. A call chained straight onto GetCollection is "
            + "invisible to the read-modify-write inventory in this file, which resolves reads and "
            + "writes through the variable they are called on. Offenders: {0}",
            string.Join(", ", offenders));
    }

    /// <summary>
    /// Maps <c>path::Member</c> to that member's source text, for every member that both reads and
    /// writes a LiteDB collection.
    /// </summary>
    private static Dictionary<string, string> Pairs(string root)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var relative in Stores(root))
        {
            var text = File.ReadAllText(Path.Combine(root, relative));
            var collections = CollectionVariables(text);
            if (collections.Count == 0)
                continue;

            foreach (var (member, body) in Members(text))
            {
                var reads = ReadCalls.Any(call => Calls(body, collections, call));
                if (!reads) continue;

                var writes = WriteCalls.Any(call => Calls(body, collections, call));
                if (!writes) continue;

                // A member name is unique enough within one store; an overload pair would collapse
                // into one row, and that is the safe direction — the row still has to be guarded.
                pairs[$"{relative}::{member}"] = body;
            }
        }

        return pairs;
    }

    /// <summary>Whether <paramref name="body"/> calls <paramref name="call"/> on a LiteDB collection.</summary>
    private static bool Calls(string body, IReadOnlyCollection<string> collections, string call)
        => collections.Any(name => body.Contains($"{name}.{call}(", StringComparison.Ordinal));

    /// <summary>
    /// Locals and parameters that hold a LiteDB collection. Both spellings are in the tree today:
    /// <c>var col = db.GetCollection&lt;T&gt;(...)</c> in every store, and
    /// <c>ILiteCollection&lt;T&gt; col</c> as a parameter on the index helpers.
    /// </summary>
    private static HashSet<string> CollectionVariables(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in Regex.Matches(text, @"(?:var|ILiteCollection<[^>]*>)\s+(\w+)\s*=\s*[\w\.]*GetCollection"))
            names.Add(m.Groups[1].Value);

        foreach (Match m in Regex.Matches(text, @"ILiteCollection<[^>]*>\s+(\w+)\s*[,)]"))
            names.Add(m.Groups[1].Value);

        return names;
    }

    /// <summary>
    /// Splits a file into members by declaration line: a member owns every line from its own
    /// declaration up to the next one. Every member in every store here is declared with an explicit
    /// accessibility modifier on one line, which is what makes this reliable without a parser — and
    /// an unparsed member simply folds into the one above it, which can only ever widen a body and so
    /// can only ever report a pair, never hide one.
    /// </summary>
    private static IEnumerable<(string Member, string Body)> Members(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var declaration = new Regex(@"^\s*(?:public|private|protected|internal)\b[^=;]*?\b(\w+)\s*(?:<[^>()]*>)?\s*\(");

        var current = "<file>";
        var body = new List<string>();

        foreach (var line in lines)
        {
            var m = declaration.Match(line);
            if (m.Success)
            {
                if (body.Count > 0)
                    yield return (current, string.Join("\n", body));

                current = m.Groups[1].Value;
                body.Clear();
            }

            body.Add(line);
        }

        if (body.Count > 0)
            yield return (current, string.Join("\n", body));
    }

    /// <summary>
    /// Whether any <c>GetCollection(...)</c> call is followed by a <c>.</c> rather than ending the
    /// expression, i.e. a call chained onto the collection instead of onto a named local.
    /// </summary>
    private static bool ChainsOntoGetCollection(string text)
    {
        foreach (Match m in Regex.Matches(text, @"GetCollection\s*(?:<[^>]*>)?\s*\("))
        {
            var i = m.Index + m.Length - 1; // the '(' itself
            var depth = 0;

            for (; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')')
                {
                    depth--;
                    if (depth == 0) break;
                }
            }

            for (i++; i < text.Length && char.IsWhiteSpace(text[i]); i++)
            {
                // Skip to the first thing that follows the call.
            }

            if (i < text.Length && text[i] == '.')
                return true;
        }

        return false;
    }

    /// <summary>Repo-root-relative paths of production files that open a LiteDB database.</summary>
    private static IEnumerable<string> Stores(string root)
    {
        var found = new List<string>();
        foreach (var top in ProductionRoots)
        {
            var dir = Path.Combine(root, top);
            if (Directory.Exists(dir))
                Collect(root, dir, found);
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static void Collect(string root, string directory, List<string> found)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var text = File.ReadAllText(file);
            if (DatabaseConstructions.Any(call => text.Contains(call, StringComparison.Ordinal)))
                found.Add(Normalize(Path.GetRelativePath(root, file)));
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IsPruned(child))
                continue;

            Collect(root, child, found);
        }
    }

    /// <summary>
    /// Build output, agent scratch space, test projects, and the root of any nested checkout — the
    /// same rule and the same reason as <see cref="LiteDbSharedModeConventionTests"/>: a second copy
    /// of every store in a worktree would turn the only required check on master red on a developer's
    /// machine while CI stayed green.
    /// </summary>
    private static bool IsPruned(string directory)
    {
        var name = Path.GetFileName(directory);

        if (string.Equals(name, "bin", StringComparison.Ordinal)
            || string.Equals(name, "obj", StringComparison.Ordinal)
            || string.Equals(name, ".claude", StringComparison.Ordinal))
        {
            return true;
        }

        if (name.Contains("Tests", StringComparison.Ordinal))
            return true;

        var git = Path.Combine(directory, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}
