using System.Text.Json;

namespace Ashlar.BackgroundAgents.Forge;

/// <summary>
/// Filesystem-backed <see cref="IChangeProposalStore"/>. Each proposal lives
/// as a single JSON file; status changes are file moves between sibling
/// folders so an operator can sift the queue with <c>ls</c>:
///
/// <code>
/// .ashlar/runtime-studio/forge/
///     proposed/    abc123.json
///     approved/    def456.json
///     rejected/    ...
///     applied/     ...
///     stale/       ...
/// </code>
///
/// <para>Complete JSON is flushed to a sibling temporary file before atomic publication.
/// Transitions replace the source atomically, then move it without overwriting another record.
/// These are two namespace operations, not a transaction: a crash or move failure between them
/// can leave complete updated JSON in the original status folder. This avoids truncated live
/// records, but does not promise directory-entry durability across power loss.</para>
/// </summary>
public sealed class ChangeProposalStore : IChangeProposalStore
{
    private readonly string _root;
    private readonly object _gate = new();
    private readonly Action<string, string> _writeTemporary;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ChangeProposalStore() : this(DefaultRoot()) { }

    public ChangeProposalStore(string root) : this(root, WriteTemporary) { }

    // Fault seam for deterministic disk-write/flush failure tests; public stores always flush to disk.
    internal ChangeProposalStore(string root, Action<string, string> writeTemporary)
    {
        _writeTemporary = writeTemporary ?? throw new ArgumentNullException(nameof(writeTemporary));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        EnsureFolders();
    }

    public string Location => _root;

    private static string DefaultRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ASHLAR_FORGE_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv);
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".ashlar", "runtime-studio", "forge"));
    }

    private void EnsureFolders()
    {
        Directory.CreateDirectory(_root);
        foreach (var status in Enum.GetValues<ChangeProposalStatus>())
            Directory.CreateDirectory(Path.Combine(_root, status.ToString().ToLowerInvariant()));
    }

    public IReadOnlyList<ChangeProposal> List(ChangeProposalStatus? status = null)
    {
        lock (_gate)
        {
            IEnumerable<ChangeProposalStatus> statuses = status is null
                ? Enum.GetValues<ChangeProposalStatus>()
                : new[] { status.Value };

            var rows = new List<ChangeProposal>();
            foreach (var s in statuses)
            {
                var dir = Path.Combine(_root, s.ToString().ToLowerInvariant());
                if (!Directory.Exists(dir)) continue;
                foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
                {
                    rows.Add(Load(path));
                }
            }
            return rows;
        }
    }

    public ChangeProposal? Find(string id)
    {
        lock (_gate)
        {
            foreach (var s in Enum.GetValues<ChangeProposalStatus>())
            {
                var path = PathFor(id, s);
                if (RecordExists(path)) return Load(path);
            }
            return null;
        }
    }

    public ChangeProposal Add(ChangeProposal proposal)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));
        if (string.IsNullOrWhiteSpace(proposal.Id))
            throw new ArgumentException("Proposal id required.", nameof(proposal));

        lock (_gate)
        {
            // Ids are written once, across EVERY status folder. Re-adding a decided id would
            // park a second row that lookups-by-id (packaging above all) could confuse with the
            // content an admission actually covered — a shadow. The same-status case matters
            // too: silently overwriting a pending proposal is a lost write, not an update.
            // The check is EXISTENCE, not parseability — a corrupt row still owns its id
            // even when parsing that row fails.
            foreach (var status in Enum.GetValues<ChangeProposalStatus>())
            {
                if (RecordExists(PathFor(proposal.Id, status)))
                {
                    throw new InvalidOperationException(
                        $"Proposal '{proposal.Id}' already exists ({status}). Ids are append-once "
                        + "across every status — propose under a new id.");
                }
            }
            var stamped = proposal with
            {
                Status = ChangeProposalStatus.Proposed,
                CreatedAt = proposal.CreatedAt == default ? DateTimeOffset.UtcNow : proposal.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var path = PathFor(stamped.Id, ChangeProposalStatus.Proposed);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Write/flush failures retain their original diagnostics. Only publication can
                // be a collision; a failed write must never be mistaken for an occupied id.
                _writeTemporary(temporary, JsonSerializer.Serialize(stamped, Json));
                try
                {
                    File.Move(temporary, path, overwrite: false);
                }
                catch (IOException ex) when (File.Exists(path))
                {
                    throw new InvalidOperationException(
                        $"Proposal '{proposal.Id}' already exists (Proposed) — another process parked it first. "
                        + "Ids are append-once across every status — propose under a new id.", ex);
                }
            }
            finally { DeleteTemporary(temporary); }
            return stamped;
        }
    }

    public ChangeProposal Approve(string id, string? approver = null, string? note = null)
        => Transition(id, ChangeProposalStatus.Proposed, ChangeProposalStatus.Approved, approver, note);

    public ChangeProposal Reject(string id, string? reviewer = null, string? note = null)
        => Transition(id, ChangeProposalStatus.Proposed, ChangeProposalStatus.Rejected, reviewer, note);

    public ChangeProposal MarkApplied(string id, string? note = null)
        => Transition(id, ChangeProposalStatus.Approved, ChangeProposalStatus.Applied, resolver: null, note: note);

    public ChangeProposal MarkStale(string id, string? note = null)
    {
        // Stale can come from either Proposed or Approved (we don't want to
        // hold an unfulfilled approved proposal forever either).
        lock (_gate)
        {
            var current = Find(id) ?? throw new InvalidOperationException($"Proposal '{id}' not found.");
            if (current.Status is ChangeProposalStatus.Stale or ChangeProposalStatus.Rejected
                or ChangeProposalStatus.Applied)
            {
                return current; // No-op for terminal states.
            }
            var src = PathFor(id, current.Status);
            var dst = PathFor(id, ChangeProposalStatus.Stale);
            var updated = current with
            {
                Status = ChangeProposalStatus.Stale,
                ResolutionNote = note ?? current.ResolutionNote,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            PublishTransition(src, dst, updated);
            return updated;
        }
    }

    private ChangeProposal Transition(
        string id,
        ChangeProposalStatus expected,
        ChangeProposalStatus next,
        string? resolver,
        string? note)
    {
        lock (_gate)
        {
            var current = Find(id) ?? throw new InvalidOperationException($"Proposal '{id}' not found.");
            if (current.Status != expected)
                throw new InvalidOperationException(
                    $"Proposal '{id}' is {current.Status}; expected {expected} for transition to {next}.");

            var src = PathFor(id, current.Status);
            var dst = PathFor(id, next);
            var updated = current with
            {
                Status = next,
                ResolvedBy = resolver ?? current.ResolvedBy,
                ResolutionNote = note ?? current.ResolutionNote,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            PublishTransition(src, dst, updated);
            return updated;
        }
    }

    private string PathFor(string id, ChangeProposalStatus status) =>
        Path.Combine(_root, status.ToString().ToLowerInvariant(), id + ".json");

    private void PublishTransition(string source, string destination, ChangeProposal updated)
    {
        // Refuse an already occupied destination before changing the source. Move also refuses
        // a racing destination; in that case the source remains complete updated JSON.
        if (RecordExists(destination))
            throw new IOException($"Proposal destination '{destination}' already exists.");
        var temporary = source + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            _writeTemporary(temporary, JsonSerializer.Serialize(updated, Json));
            File.Move(temporary, source, overwrite: true);
            File.Move(source, destination, overwrite: false);
        }
        finally { DeleteTemporary(temporary); }
    }

    private static void WriteTemporary(string path, string json)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, 1024, leaveOpen: true);
        writer.Write(json);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteTemporary(string path)
    {
        // Cleanup must not replace the write/move error which explains the failed operation.
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool RecordExists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static ChangeProposal Load(string path)
    {
        // IO/access failures propagate rather than silently dropping a live proposal.
        var json = File.ReadAllText(path);
        try
        {
            return JsonSerializer.Deserialize<ChangeProposal>(json, Json)
                ?? throw new InvalidDataException($"Proposal record '{path}' contains JSON null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Proposal record '{path}' contains invalid JSON.", ex);
        }
    }
}
