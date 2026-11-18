using System.Text.Json;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;

namespace Ashlar.Tools.GovernanceBrokerPrototype;

// No trusted evaluator or approval authority is configured: writes are signed rejections.
// Peer identity is supplied by the transport, never by request JSON.
internal sealed class Broker(string project, uint workerUid, GateStore store, TimeProvider clock, int maximumRecords = 128)
{
    private readonly SemaphoreSlim transaction = new(1, 1);
    private readonly int recordLimit = maximumRecords is >= 1 and <= 128
        ? maximumRecords : throw new ArgumentOutOfRangeException(nameof(maximumRecords));
    private readonly AshlarPolicy policy = new()
    {
        SelfExtend = new() { Mode = SelfExtendMode.Sealed, MayAdd = ["brick"] },
    };

    public async Task<BrokerResponse> DispatchAsync(ReadOnlyMemory<byte> json, uint peerUid,
        CancellationToken ct = default)
    {
        if (peerUid != workerUid) return BrokerResponse.Failure("unauthorized");
        BrokerRequest request;
        try { request = BrokerRequest.Parse(json); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        { return BrokerResponse.Failure("invalid-request"); }
        if (!string.Equals(request.Project, project, StringComparison.Ordinal))
            return BrokerResponse.Failure("unknown-project");

        // One service serializes dedup/read/propose. Existing GateStore locks and
        // append-once checks remain in place, including across service instances.
        await transaction.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = "prototype-" + request.Id.ToString("N");
            var previous = await store.GetAsync(id, ct).ConfigureAwait(false);
            if (request.Op == "read")
                return previous is null ? BrokerResponse.Failure("not-found") : BrokerResponse.Success(previous);
            var attribution = "worker-uid:" + peerUid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (previous is not null)
            {
                // Persisted request ID + exact content survive a lost response/restart.
                if (previous.Proposal.Kind != request.Kind || previous.Proposal.Summary != request.Summary
                    || previous.Proposal.Diff != request.Diff || previous.Proposal.ProposedBy != attribution
                    || previous.Proposal.Courses.Count != 0 || previous.Proposal.ForgeProposalIds.Count != 0
                    || previous.Proposal.Files is not null || previous.State != ProposalState.Rejected)
                    return BrokerResponse.Failure("request-conflict");
                return BrokerResponse.Success(previous);
            }
            if ((await store.ListAsync(ct: ct).ConfigureAwait(false)).Count >= recordLimit)
                return BrokerResponse.Failure("prototype-capacity");
            var now = clock.GetUtcNow();
            var proposal = new ExtensionProposal
            {
                Id = id, Kind = request.Kind!, Summary = request.Summary!, Diff = request.Diff!,
                ProposedBy = attribution, ProposedAt = now, Courses = [],
            };
            var record = await store.ProposeAsync(policy, proposal, now, ct).ConfigureAwait(false);
            return BrokerResponse.Success(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // No raw errors, private paths, signing material, or unsigned/local fallback.
            return BrokerResponse.Failure("store-unavailable");
        }
        finally { transaction.Release(); }
    }
}

internal sealed record BrokerRequest(string Op, string Project, Guid Id, string? Kind,
    string? Summary, string? Diff)
{
    public const int MaxBytes = 65536;

    public static BrokerRequest Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new ArgumentException("Request size.");
        using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 4 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Object required.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
        {
            if (!names.Add(field.Name)) throw new ArgumentException("Duplicate field.");
            if (field.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("String required.");
        }
        string Required(string name, int max)
        {
            if (!root.TryGetProperty(name, out var value)) throw new ArgumentException("Missing field.");
            var text = value.GetString()!;
            if (text.Length == 0 || text.Length > max || !ValidUtf16(text))
                throw new ArgumentException("Invalid field.");
            return text;
        }
        var op = Required("op", 16);
        var allowed = op switch
        {
            "submit" => new[] { "op", "project", "requestId", "kind", "summary", "diff" },
            "read" => new[] { "op", "project", "requestId" },
            _ => throw new ArgumentException("Unsupported operation."),
        };
        if (names.Count != allowed.Length || names.Except(allowed, StringComparer.Ordinal).Any())
            throw new ArgumentException("Unexpected fields.");
        var project = Required("project", 64);
        var idText = Required("requestId", 36);
        if (!Guid.TryParseExact(idText, "D", out var id) || id == Guid.Empty)
            throw new ArgumentException("Invalid ID.");
        if (op == "read") return new(op, project, id, null, null, null);
        var kind = Required("kind", 32);
        if (kind != "brick") throw new ArgumentException("Unsupported kind.");
        // Text is never interpreted as a path, command or policy. Preserve Unicode.
        return new(op, project, id, kind, Required("summary", 2048), Required("diff", 24000));
    }

    private static bool ValidUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i >= value.Length || !char.IsLowSurrogate(value[i]))
                return false;
        }
        return true;
    }
}

internal sealed record BrokerResponse(bool Ok, string? Error, GateRecord? Record)
{
    public static BrokerResponse Failure(string error) => new(false, error, null);
    public static BrokerResponse Success(GateRecord record) => new(true, null, record);
}
