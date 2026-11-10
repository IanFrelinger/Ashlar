using System.Diagnostics;
using System.Text.Json;

namespace Ashlar.BackgroundAgents.Forge;

/// <summary>
/// Filesystem proposal store. Complete JSON is flushed to a private temporary file before
/// publication by rename. Status-folder transitions use a durable intent journal and are
/// completed idempotently on the next operation after an interrupted writer. Corruption and
/// I/O failures are surfaced, never treated as missing proposals or successful empty listings.
/// </summary>
/// <remarks>
/// Cooperating processes serialize on a persistent lock file (never unlinked). This requires
/// a filesystem/runtime that honors exclusive file sharing and atomic same-volume renames.
/// Flush(true) persists file data; sudden power-loss directory durability is filesystem-specific.
/// The journal provides process-crash recovery, not a portable power-loss guarantee.
/// </remarks>
public sealed class ChangeProposalStore : IChangeProposalStore
{
    private readonly string _root;
    private readonly object _gate = new();
    private readonly Action<string>? _fault;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ChangeProposalStore() : this(DefaultRoot()) { }

    public ChangeProposalStore(string root) : this(root, null) { }

    // Deterministic failure seams at real persistence boundaries, never enabled by configuration.
    internal ChangeProposalStore(string root, Action<string>? fault)
    {
        _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
        _fault = fault;
        Directory.CreateDirectory(_root);
        foreach (var status in Enum.GetValues<ChangeProposalStatus>())
            Directory.CreateDirectory(DirectoryFor(status));
        Directory.CreateDirectory(JournalDirectory);
    }

    public string Location => _root;
    private string JournalDirectory => Path.Combine(_root, ".transactions");

    private static string DefaultRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ASHLAR_FORGE_ROOT");
        return !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv :
            Path.Combine(Directory.GetCurrentDirectory(), ".ashlar", "runtime-studio", "forge");
    }

    public IReadOnlyList<ChangeProposal> List(ChangeProposalStatus? status = null)
        => WithStoreLock(() =>
        {
            var rows = new List<ChangeProposal>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            // Inspect every bucket even for filtered queries: a shadow in another bucket must
            // not disappear just because an operator asked to see the pending queue.
            foreach (var s in Enum.GetValues<ChangeProposalStatus>())
            foreach (var path in Directory.EnumerateFiles(DirectoryFor(s), "*.json"))
            {
                var proposal = LoadProposal(path, s);
                if (!ids.Add(proposal.Id))
                    throw new InvalidDataException($"Duplicate proposal id '{proposal.Id}' in '{_root}'.");
                if (status is null || proposal.Status == status) rows.Add(proposal);
            }
            if (status is { } selected && !Enum.IsDefined(selected))
                throw new ArgumentOutOfRangeException(nameof(status));
            return (IReadOnlyList<ChangeProposal>)rows;
        });

    public ChangeProposal? Find(string id) => WithStoreLock(() => FindCore(id));

    private ChangeProposal? FindCore(string id)
    {
        ValidateId(id);
        ChangeProposal? result = null;
        foreach (var status in Enum.GetValues<ChangeProposalStatus>())
        {
            var path = PathFor(id, status);
            if (!Exists(path)) continue;
            var proposal = LoadProposal(path, status);
            if (result is not null)
                throw new InvalidDataException($"Duplicate proposal id '{id}' in '{_root}'.");
            result = proposal;
        }
        return result;
    }

    public ChangeProposal Add(ChangeProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ValidateId(proposal.Id);
        return WithStoreLock(() =>
        {
            // Existence is ownership even when JSON is corrupt. An I/O error is not a collision.
            foreach (var status in Enum.GetValues<ChangeProposalStatus>())
                if (Exists(PathFor(proposal.Id, status)))
                    throw new InvalidOperationException(
                        $"Proposal '{proposal.Id}' already exists ({status}). Ids are append-once "
                        + "across every status — propose under a new id.");
            var stamped = proposal with
            {
                Status = ChangeProposalStatus.Proposed,
                CreatedAt = proposal.CreatedAt == default ? DateTimeOffset.UtcNow : proposal.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            ValidateProposal(stamped);
            WriteAtomic(PathFor(stamped.Id, stamped.Status), stamped, "add");
            return stamped;
        });
    }

    public ChangeProposal Approve(string id, string? approver = null, string? note = null)
        => Transition(id, ChangeProposalStatus.Proposed, ChangeProposalStatus.Approved, approver, note);

    public ChangeProposal Reject(string id, string? reviewer = null, string? note = null)
        => Transition(id, ChangeProposalStatus.Proposed, ChangeProposalStatus.Rejected, reviewer, note);

    public ChangeProposal MarkApplied(string id, string? note = null)
        => Transition(id, ChangeProposalStatus.Approved, ChangeProposalStatus.Applied, null, note);

    public ChangeProposal MarkStale(string id, string? note = null) => WithStoreLock(() =>
    {
        var current = FindCore(id) ?? throw new InvalidOperationException($"Proposal '{id}' not found.");
        if (current.Status is ChangeProposalStatus.Stale or ChangeProposalStatus.Rejected or ChangeProposalStatus.Applied)
            return current;
        return CommitTransition(current, current with
        {
            Status = ChangeProposalStatus.Stale,
            ResolutionNote = note ?? current.ResolutionNote,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    });

    private ChangeProposal Transition(string id, ChangeProposalStatus expected, ChangeProposalStatus next,
        string? resolver, string? note) => WithStoreLock(() =>
    {
        var current = FindCore(id) ?? throw new InvalidOperationException($"Proposal '{id}' not found.");
        if (current.Status != expected)
            throw new InvalidOperationException(
                $"Proposal '{id}' is {current.Status}; expected {expected} for transition to {next}.");
        return CommitTransition(current, current with
        {
            Status = next,
            ResolvedBy = resolver ?? current.ResolvedBy,
            ResolutionNote = note ?? current.ResolutionNote,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    });

    private ChangeProposal CommitTransition(ChangeProposal original, ChangeProposal updated)
    {
        var intent = new TransitionIntent(original, updated);
        var journal = Path.Combine(JournalDirectory, original.Id + ".json");
        // Publication of the intent is the commit point. After this, failures have an uncertain
        // return outcome but the next operation completes this exact decision before serving data.
        WriteAtomic(journal, intent, "journal");
        CompleteTransition(journal, intent);
        return updated;
    }

    private void Recover()
    {
        foreach (var journal in Directory.EnumerateFiles(JournalDirectory, "*.json"))
        {
            var intent = Load<TransitionIntent>(journal);
            ValidateProposal(intent.Original);
            ValidateProposal(intent.Updated);
            if (intent.Original.Id != Path.GetFileNameWithoutExtension(journal)
                || intent.Updated.Id != intent.Original.Id
                || !IsTransition(intent.Original.Status, intent.Updated.Status)
                || (intent.Original with
                {
                    Status = intent.Updated.Status,
                    ResolvedBy = intent.Updated.ResolvedBy,
                    ResolutionNote = intent.Updated.ResolutionNote,
                    UpdatedAt = intent.Updated.UpdatedAt
                } != intent.Updated))
                throw new InvalidDataException($"Invalid proposal transition journal '{journal}'.");
            CompleteTransition(journal, intent);
        }
    }

    private void CompleteTransition(string journal, TransitionIntent intent)
    {
        var source = PathFor(intent.Original.Id, intent.Original.Status);
        var destination = PathFor(intent.Updated.Id, intent.Updated.Status);
        // Verify ALL candidates before changing anything. Never overwrite conflicting/corrupt data.
        var hasSource = Exists(source);
        var hasDestination = Exists(destination);
        foreach (var status in Enum.GetValues<ChangeProposalStatus>())
        {
            var path = PathFor(intent.Original.Id, status);
            if (status != intent.Original.Status && status != intent.Updated.Status && Exists(path))
                throw new InvalidDataException($"Conflicting proposal row '{path}' during recovery.");
        }
        if (hasSource && LoadProposal(source, intent.Original.Status) != intent.Original)
            throw new InvalidDataException($"Conflicting source proposal '{source}' during recovery.");
        if (hasDestination && LoadProposal(destination, intent.Updated.Status) != intent.Updated)
            throw new InvalidDataException($"Conflicting destination proposal '{destination}' during recovery.");
        if (!hasSource && !hasDestination)
            throw new InvalidDataException($"Both proposal rows are missing for transition '{journal}'.");
        if (!hasDestination) WriteAtomic(destination, intent.Updated, "destination");
        _fault?.Invoke("transition.destination-published");
        if (hasSource) File.Delete(source);
        _fault?.Invoke("transition.source-deleted");
        File.Delete(journal);
        _fault?.Invoke("transition.journal-deleted");
    }

    private static bool IsTransition(ChangeProposalStatus from, ChangeProposalStatus to) =>
        (from == ChangeProposalStatus.Proposed && to is ChangeProposalStatus.Approved or ChangeProposalStatus.Rejected or ChangeProposalStatus.Stale)
        || (from == ChangeProposalStatus.Approved && to is ChangeProposalStatus.Applied or ChangeProposalStatus.Stale);

    private void WriteAtomic<T>(string destination, T value, string operation)
    {
        var temp = Path.Combine(Path.GetDirectoryName(destination)!, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                _fault?.Invoke(operation + ".writing");
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
                _fault?.Invoke(operation + ".flushed");
            }
            // No overwrite: only one complete value may own any published destination.
            File.Move(temp, destination);
            _fault?.Invoke(operation + ".published");
        }
        finally
        {
            // Preserve the original error if cleanup also fails. Orphan .tmp files are never read.
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private T WithStoreLock<T>(Func<T> action)
    {
        lock (_gate)
        {
            using var processLock = AcquireLock();
            Recover();
            return action();
        }
    }

    private FileStream AcquireLock()
    {
        var wait = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(Path.Combine(_root, ".store.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsSharingViolation(ex) && wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(10);
            }
        }
    }

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xffff) is 11 or 32 or 33;

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        // Missing bucket, permission denial, and every other I/O failure are store failures.
    }

    private T Load<T>(string path) where T : class
    {
        _fault?.Invoke("read");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<T>(stream, Json)
                ?? throw new InvalidDataException($"Null proposal data in '{path}'.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Corrupt proposal data in '{path}'.", ex);
        }
    }

    private ChangeProposal LoadProposal(string path, ChangeProposalStatus status)
    {
        var proposal = Load<ChangeProposal>(path);
        ValidateProposal(proposal);
        if (proposal.Id != Path.GetFileNameWithoutExtension(path) || proposal.Status != status)
            throw new InvalidDataException($"Proposal identity/status does not match storage path '{path}'.");
        return proposal;
    }

    private static void ValidateProposal(ChangeProposal proposal)
    {
        if (proposal is null || !IsValidId(proposal.Id) || !Enum.IsDefined(proposal.Status)
            || proposal.TargetPath is null || proposal.NewContent is null || proposal.Summary is null)
            throw new InvalidDataException("Invalid proposal identity, status, or required content.");
    }

    private static bool IsValidId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        && !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(id, StringComparer.OrdinalIgnoreCase);

    private static void ValidateId(string id)
    {
        if (!IsValidId(id)) throw new ArgumentException("Proposal id must be a safe, non-reserved ASCII identifier (1-128 letters, digits, '-' or '_').", nameof(id));
    }

    private string DirectoryFor(ChangeProposalStatus status) => Path.Combine(_root, status.ToString().ToLowerInvariant());
    private string PathFor(string id, ChangeProposalStatus status) => Path.Combine(DirectoryFor(status), id + ".json");
    private sealed record TransitionIntent(ChangeProposal Original, ChangeProposal Updated);
}
