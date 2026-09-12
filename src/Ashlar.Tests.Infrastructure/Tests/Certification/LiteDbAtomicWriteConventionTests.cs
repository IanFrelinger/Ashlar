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
/// <para><b>Six facts.</b> <see cref="Every_read_modify_write_is_inventoried"/> fails when a new
/// store method pairs a read with a write and is not on the list — that is the new-offender half.
/// <see cref="No_inventory_row_has_stopped_doing_a_read_modify_write"/> fails on a stale row, because
/// a row describing a pair that no longer exists reads as accounted-for debt and quietly turns a
/// frozen inventory into a blanket approval — that is the stale-row half.
/// <see cref="Every_inventoried_read_modify_write_opens_a_transaction"/> is the one the inventory
/// cannot express: being on the list is an admission that the pair exists, not permission to leave it
/// unguarded, so each listed member must reach <c>LiteDbAtomic.Mutate</c>.</para>
///
/// <para>Those three freeze the CALL SITES. The remaining three keep the scan honest about what it
/// can see and about what the call reaches.
/// <see cref="Every_inventoried_read_modify_write_opens_a_transaction"/> matches the TEXT
/// <c>LiteDbAtomic.Mutate</c>, so on its own it would stay green if the helper's body were
/// "simplified" to <c>=&gt; readModifyWrite();</c> — and the two copies of that helper
/// (<c>src/</c> and <c>commercial/</c>) share no code, so a rebase can resolve <c>BeginTrans</c> away
/// in one of them alone. <see cref="Both_copies_of_the_helper_still_open_a_transaction"/> therefore
/// asserts the mechanism itself, in both files.
/// <see cref="No_store_chains_a_call_straight_onto_GetCollection"/> and
/// <see cref="Every_GetCollection_call_binds_its_collection_to_a_local"/> together close the
/// attribution blind spot: reads and writes are attributed through the collection VARIABLE they are
/// called on, so <c>db.GetCollection&lt;T&gt;("c").Update(doc)</c> inline, or a collection reached
/// through a helper (<c>var col = Fleet(db);</c>), would be invisible to the first three. The only
/// admitted spelling is a local bound directly from <c>GetCollection</c>; that is what makes an
/// <c>ILiteCollection</c> impossible to obtain without the scan seeing it, which is a convention and
/// not a style preference.</para>
///
/// <para><b>What this guard does NOT catch.</b> It reads text within a single member, so it only sees
/// a pair whose halves are both inside one store method. The other shape is a caller that reads
/// through one store method, decides, and writes through another — the database is opened and closed
/// in between, so no transaction can span it and no text scan of the stores can see it. Those sites
/// are real and they are still open at the time of writing:
/// <c>CommercialFleetEndpoints.PatchMeshTaskStatusAsync</c> (reads and COMPARES
/// <c>MeshTaskDoc.LeaseToken</c>, then writes without it as a precondition),
/// <c>CommercialFleetEndpoints.RegisterFleetNodeAsync</c> (reads the node through
/// <c>IFleetNodeRegistry.GetAsync</c>, derives <c>Admitted</c> from that snapshot, and then writes the
/// WHOLE document back through <c>RegisterOrUpdateAsync</c> — so a <c>revoke</c> landing in the window
/// is reverted; the <c>Drained</c> half of the same write was NOT a race and is fixed, see below),
/// <c>MeshTaskPlacementService.TryPlaceAsync</c>, <c>MeshTaskExecutionService.ExtendLeaseAsync</c> and
/// <c>MigrateForCheckpointAsync</c>, <c>MeshLeaseSweepBackgroundService</c>,
/// <c>MeshPendingTaskRebalancerBackgroundService</c>, <c>PipelineOrchestrator</c>'s resume path, and
/// <c>SelfImprovementLoop</c>'s <c>IsProcessed</c>/<c>MarkProcessed</c> guard. Closing those needs a
/// compare-and-swap precondition on the port (<c>LeaseToken</c> is the natural token for the mesh
/// ones; <c>LiteDbPatternProcessedStore</c> wants a unique index instead), which is a port change
/// rather than a store change. Do not read this test's silence about them as a claim they are safe.
/// None of them can get an inventory row either: <c>RegisterOrUpdateAsync</c>, for instance, is
/// write-only inside its own method, so the scan below sees nothing to inventory. This paragraph is
/// the only place a reviewer learns of them.</para>
///
/// <para><b>One of those was not a race at all, and is fixed.</b> <c>RegisterFleetNodeAsync</c> built
/// its <c>MeshFleetNodeState</c> with <c>Drained: body.Drained</c> off a non-nullable
/// <c>bool Drained = false</c> on <c>MeshFleetNodeRequest</c>, so a node re-registering on its normal
/// reconnect cycle with no <c>drained</c> field in the body reset an operator's drain with no
/// concurrency involved — and placement selects on <c>Admitted &amp;&amp; !Drained</c>. The field is
/// now <c>bool?</c> and falls back to the stored value, the same shape <c>Admitted</c> already used.
/// <c>FleetHostEndpointTests.Re_registering_a_node_does_not_clear_a_drain</c> covers it through the
/// real HTTP pipeline — and that project is in no solution and no lane, which
/// <c>ci/test-ownership.tsv</c> already tracks, so read that test as a regression record rather than
/// as a guard.</para>
///
/// <para>The behavioural half of THIS fix lives in
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
    /// <summary>
    /// What makes a production file a store for this scan. Deliberately WIDER than the Shared-mode
    /// guard's construction list, which only asks who OPENS a file: a class that is handed an already
    /// open <c>ILiteDatabase</c> — the shape
    /// <c>LiteDbDataDecisionAuditLog.InsertBufferInOneTransaction</c> is already one call away from —
    /// or an already open <c>ILiteCollection&lt;T&gt;</c> constructs nothing, and would otherwise never
    /// be enumerated at all while doing a textbook lost update. <c>LiteDatabase</c> as a bare substring
    /// covers <c>new LiteDatabase(</c>, an <c>ILiteDatabase</c> parameter and a <c>LiteDatabase</c>
    /// field alike; there is no way to reach a collection without naming one of these types.
    /// </summary>
    private static readonly string[] LiteDbMarkers =
    [
        "LiteDatabase",
        "LiteRepository",
        "LiteEngine",
        "SharedEngine",
        "ILiteCollection<",
    ];

    /// <summary>
    /// The two copies of the helper. They share no code — the fleet assembly does not reference
    /// <c>Ashlar.Infrastructure</c> — so nothing but this list pins them to each other.
    /// </summary>
    private static readonly string[] HelperFiles =
    [
        "commercial/src/Ashlar.Commercial.Fleet.Infrastructure/LiteDbAtomic.cs",
        "src/Ashlar.Infrastructure/Persistence/LiteDbAtomic.cs",
    ];

    /// <summary>
    /// What each copy of the helper has to still DO, as opposed to still be called. The first three
    /// are the transaction; the last three are the two shapes the helper refuses because they fail
    /// silently — a nested <c>Mutate</c> (LiteDB 5.0.21 ends the outer transaction and discards both
    /// scopes' writes) and an async body (commits at the first <c>await</c>, with the write landing
    /// outside the transaction).
    /// </summary>
    private static readonly string[] HelperMechanism =
    [
        "db.BeginTrans()",
        "db.Commit()",
        "db.Rollback()",
        "throw new InvalidOperationException(NestedMessage)",
        "Func<Task<T>> readModifyWrite",
        "Func<Task> readModifyWrite",
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
    /// The other three facts freeze the call sites; this one freezes what the call reaches. Every one
    /// of them matches TEXT, so replacing either helper's body with <c>=&gt; readModifyWrite();</c>
    /// leaves them green while every guarded pair silently goes back to two engine operations — and
    /// the two copies are independent files, so a merge can do it to one of them alone.
    /// </summary>
    [Fact]
    public void Both_copies_of_the_helper_still_open_a_transaction()
    {
        var root = RepoPathResolver.FindRepoRoot();

        var missing = new List<string>();
        foreach (var relative in HelperFiles)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path))
            {
                missing.Add($"{relative}::<the file itself>");
                continue;
            }

            var text = File.ReadAllText(path);
            missing.AddRange(HelperMechanism
                .Where(call => !text.Contains(call, StringComparison.Ordinal))
                .Select(call => $"{relative}::{call}"));
        }

        missing.Should().BeEmpty(
            "the helper IS the fix; every other fact in this file only checks that it is called. A "
            + "body that no longer begins and commits a transaction turns all seven guarded pairs "
            + "back into two separable engine operations, and nothing else in any automatically "
            + "triggered lane would notice. Missing: {0}",
            string.Join(", ", missing));
    }

    /// <summary>
    /// The companion to <see cref="No_store_chains_a_call_straight_onto_GetCollection"/>. Chaining is
    /// one way a collection escapes attribution; RETURNING one is the other, and it is the more
    /// natural edit — a <c>private static ILiteCollection&lt;T&gt; Fleet(ILiteDatabase db) =&gt;
    /// db.GetCollection&lt;T&gt;(...)</c> next to the existing collection-name constants reads like
    /// tidying up. Its callers would then write <c>var col = Fleet(db);</c>, which no regex over one
    /// file can resolve, and the pair below it would be invisible to all three inventory facts.
    /// Requiring the result of every <c>GetCollection</c> to land directly in a local is what makes an
    /// <c>ILiteCollection</c> impossible to come by without this scan seeing where.
    /// </summary>
    [Fact]
    public void Every_GetCollection_call_binds_its_collection_to_a_local()
    {
        var root = RepoPathResolver.FindRepoRoot();

        var offenders = new List<string>();
        foreach (var relative in Stores(root))
        {
            var text = File.ReadAllText(Path.Combine(root, relative));
            offenders.AddRange(UnboundGetCollectionCalls(text).Select(line => $"{relative}: {line}"));
        }

        offenders.Should().BeEmpty(
            "assign the collection straight to a local: `var col = db.GetCollection<T>(name);`. "
            + "Returning it from a helper, passing it as an argument, or chaining off it hands an "
            + "ILiteCollection to code this scan cannot attribute, and a read-modify-write on that "
            + "collection gets no inventory row and no transaction. Offenders: {0}",
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
    /// Locals, parameters and fields that hold a LiteDB collection. Both spellings are in the tree
    /// today: <c>var col = db.GetCollection&lt;T&gt;(...)</c> in every store, and
    /// <c>ILiteCollection&lt;T&gt; col</c> as a parameter on the index helpers. The declaration form
    /// also admits <c>;</c> and <c>=</c> as terminators so a FIELD holding a collection — a class
    /// handed one in its constructor — is named here too rather than silently contributing nothing.
    /// </summary>
    private static HashSet<string> CollectionVariables(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in Regex.Matches(text, BindingPattern + "GetCollection"))
            names.Add(m.Groups[1].Value);

        foreach (Match m in Regex.Matches(text, @"ILiteCollection<[^>]*>\s+(\w+)\s*[,);=]"))
            names.Add(m.Groups[1].Value);

        return names;
    }

    /// <summary>
    /// A declaration that binds something straight to a local or field: <c>var x = </c> or
    /// <c>ILiteCollection&lt;T&gt; x = </c>, optionally through a dotted receiver. Shared by
    /// <see cref="CollectionVariables"/> and <see cref="UnboundGetCollectionCalls"/> so that the set
    /// of bindings the inventory can SEE and the set the convention ALLOWS are the same set by
    /// construction, rather than two regexes that can drift apart.
    /// </summary>
    private const string BindingPattern = @"(?:var|ILiteCollection<[^>]*>)\s+(\w+)\s*=\s*[\w\.]*";

    /// <summary>
    /// Every <c>GetCollection(...)</c> call in <paramref name="text"/> whose result is not assigned
    /// directly to a local or field, reported as its source line.
    /// </summary>
    private static IEnumerable<string> UnboundGetCollectionCalls(string text)
    {
        var bound = new Regex("^\\s*" + BindingPattern + "$");
        var normalized = text.Replace("\r\n", "\n");

        foreach (Match m in Regex.Matches(normalized, @"GetCollection\s*(?:<[^>]*>)?\s*\("))
        {
            var lineStart = m.Index == 0 ? 0 : normalized.LastIndexOf('\n', m.Index - 1) + 1;
            if (bound.IsMatch(normalized[lineStart..m.Index]))
                continue;

            var lineEnd = normalized.IndexOf('\n', m.Index);
            if (lineEnd < 0) lineEnd = normalized.Length;
            yield return normalized[lineStart..lineEnd].Trim();
        }
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
            if (LiteDbMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal)))
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
