using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ashlar.Mcp.Bricks;

/// <summary>
/// Checks the hash chain of <see cref="BrickMcpToolContributor"/> records in File-sink audit lines.
/// </summary>
/// <remarks>
/// <para>Per <c>session</c> (one server process): <c>seq</c> starts at 1 and only increases, the first record's
/// <c>prevSha256</c> is <see cref="Genesis"/>, each later one's is the SHA-256 (lower-case hex, UTF-8) of the
/// previous record's <c>detail</c> string exactly as written, and the detail's <c>auditId</c> equals the line's
/// <c>correlationId</c>. A gap in <c>seq</c> with an intact link is a record the server could not write (it said so
/// to the caller); it is reported in <see cref="McpBrickAuditChainResult.MissingSequenceNumbers"/>, not as a break.</para>
/// <para>What a chain cannot show on its own: removing the last records of a session, or a whole file, and
/// rewriting a session from the edited record onwards. The server logs each record's hash
/// (<c>RecordSha256</c>) to its log as it writes it; comparing the last logged hash with the last record covers
/// the first, and a copy of the files elsewhere covers the rest.</para>
/// </remarks>
public static class McpBrickAuditChain
{
    /// <summary><c>prevSha256</c> of the first record of a session.</summary>
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Verifies the audit lines of one or more files, given in file-name order (rotated files of one session follow
    /// each other). Lines of other event types and blank lines are skipped.
    /// </summary>
    /// <param name="lines">The NDJSON lines, in order.</param>
    public static McpBrickAuditChainResult Verify(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var sessions = new Dictionary<string, (long Seq, string Sha256)>(StringComparer.Ordinal);
        var problems = new List<string>();
        var missing = new List<long>();
        var records = 0;
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? correlationId;
            string? detail;
            try
            {
                using var outer = JsonDocument.Parse(line);
                var root = outer.RootElement;
                if (!root.TryGetProperty("eventType", out var type) || type.GetString() != BrickMcpToolContributor.EventType)
                {
                    continue;
                }

                correlationId = root.TryGetProperty("correlationId", out var c) ? c.GetString() : null;
                detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
            }
            catch (JsonException)
            {
                problems.Add($"line {lineNumber}: not a JSON object");
                continue;
            }

            records++;
            if (detail is null)
            {
                problems.Add($"line {lineNumber}: no detail");
                continue;
            }

            string? auditId, session, prev;
            long seq;
            try
            {
                using var inner = JsonDocument.Parse(detail);
                var d = inner.RootElement;
                auditId = d.TryGetProperty("auditId", out var a) ? a.GetString() : null;
                session = d.TryGetProperty("session", out var s) ? s.GetString() : null;
                prev = d.TryGetProperty("prevSha256", out var p) ? p.GetString() : null;
                seq = d.TryGetProperty("seq", out var q) && q.TryGetInt64(out var n) ? n : 0;
            }
            catch (JsonException)
            {
                problems.Add($"line {lineNumber}: detail is not a JSON object");
                continue;
            }

            if (session is null || prev is null || seq <= 0)
            {
                problems.Add($"line {lineNumber}: record has no session, seq or prevSha256");
                continue;
            }

            if (!string.Equals(auditId, correlationId, StringComparison.Ordinal))
            {
                problems.Add($"line {lineNumber}: auditId in the detail does not match correlationId");
            }

            var (lastSeq, lastSha256) = sessions.TryGetValue(session, out var last) ? last : (0L, Genesis);
            if (seq <= lastSeq)
            {
                problems.Add($"line {lineNumber}: seq {seq} does not follow {lastSeq} in session {session}");
            }
            else
            {
                for (var gap = lastSeq + 1; gap < seq; gap++)
                {
                    missing.Add(gap);
                }
            }

            if (!string.Equals(prev, lastSha256, StringComparison.Ordinal))
            {
                problems.Add(lastSeq == 0
                    ? $"line {lineNumber}: session {session} starts at seq {seq} without the genesis link (earlier records are missing)"
                    : $"line {lineNumber}: prevSha256 does not match the record before it (seq {lastSeq}): a record was changed, removed or reordered");
            }

            sessions[session] = (Math.Max(seq, lastSeq), Sha256(detail));
        }

        return new McpBrickAuditChainResult(problems.Count == 0, records, sessions.Count, problems, missing);
    }

    private static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>Outcome of <see cref="McpBrickAuditChain.Verify"/>.</summary>
/// <param name="Intact">No problem found.</param>
/// <param name="Records">Brick-call records read.</param>
/// <param name="Sessions">Distinct sessions (server processes) seen.</param>
/// <param name="Problems">Each break, with its line number.</param>
/// <param name="MissingSequenceNumbers">Sequence numbers skipped with the chain intact: records the server could not write.</param>
public sealed record McpBrickAuditChainResult(
    bool Intact,
    int Records,
    int Sessions,
    IReadOnlyList<string> Problems,
    IReadOnlyList<long> MissingSequenceNumbers);
