namespace JobSearch.Workspace;

/// <summary>A provider failure that may succeed later.</summary>
public class ProviderException(string provider, string message, bool retryable = true) : Exception($"{provider}: {message}")
{
    /// <summary>Which provider.</summary>
    public string Provider { get; } = provider;
    /// <summary>Whether retrying can help.</summary>
    public bool Retryable { get; } = retryable;
}

/// <summary>The user's authorization expired or was revoked; only the user can fix it by reconnecting.</summary>
public sealed class AuthExpiredException(string provider, string message = "authorization expired or revoked; reconnect Google Drive")
    : ProviderException(provider, message, retryable: false);

/// <summary>Rate limited; retry after the wait.</summary>
public sealed class RateLimitedException(string provider, TimeSpan retryAfter) : ProviderException(provider, $"rate limited; retry after {retryAfter.TotalSeconds:0} s")
{
    /// <summary>The wait.</summary>
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>Not found, or not visible to this account (Drive answers the same for both).</summary>
public sealed class NotFoundException(string provider, string what) : ProviderException(provider, $"not found: {what}", retryable: false);

/// <summary>File metadata.</summary>
public sealed record FileMeta(string Id, string Name, IReadOnlyList<string> Parents, long Version, string? ModifiedTime, string Mime);

/// <summary>
/// One user's Google Drive + Sheets, through that user's own authorization. Every file the app creates carries an
/// app key (Drive <c>appProperties</c>); creation first looks the key up, so a run interrupted after an upload but
/// before the database commit finds the file instead of creating a duplicate.
/// </summary>
public interface IGoogleWorkspace
{
    /// <summary>The account the token belongs to.</summary>
    Task<string> AccountEmailAsync(CancellationToken ct = default);
    /// <summary>The file carrying this app key, or null.</summary>
    Task<string?> FindByAppKeyAsync(string appKey, CancellationToken ct = default);
    /// <summary>The folder with this key, created if absent.</summary>
    Task<string> EnsureFolderAsync(string name, string? parentId, string appKey, CancellationToken ct = default);
    /// <summary>An editable Google Doc converted from HTML, unless one with this key exists.</summary>
    Task<string> CreateDocAsync(string name, string parentId, string html, string appKey, CancellationToken ct = default);
    /// <summary>A binary file, unless one with this key exists.</summary>
    Task<string> CreateFileAsync(string name, string parentId, byte[] data, string mime, string appKey, CancellationToken ct = default);
    /// <summary>The file's text (a Google Doc exported as plain text; a PDF's extracted text).</summary>
    Task<string> ReadTextAsync(string fileId, CancellationToken ct = default);
    /// <summary>Replaces a binary file's content; Drive keeps the previous revision.</summary>
    Task UpdateFileAsync(string fileId, byte[] data, string mime, CancellationToken ct = default);
    /// <summary>Metadata.</summary>
    Task<FileMeta> MetaAsync(string fileId, CancellationToken ct = default);
    /// <summary>Moves a file to another folder (never deletes).</summary>
    Task MoveAsync(string fileId, string newParentId, CancellationToken ct = default);
    /// <summary>A copy with this key, unless one exists.</summary>
    Task<string> CopyAsync(string fileId, string name, string parentId, string appKey, CancellationToken ct = default);
    /// <summary>Marks the head revision keep-forever where Drive supports it; returns its id or null.</summary>
    Task<string?> KeepRevisionForeverAsync(string fileId, CancellationToken ct = default);
    /// <summary>A spreadsheet with this header row, unless one with this key exists.</summary>
    Task<string> CreateSheetAsync(string name, string parentId, IReadOnlyList<string> headers, string appKey, CancellationToken ct = default);
    /// <summary>All rows; row 0 is the header.</summary>
    Task<List<List<string>>> ReadRowsAsync(string sheetId, CancellationToken ct = default);
    /// <summary>Writes cells (row index into <see cref="ReadRowsAsync"/>, column header name, value).</summary>
    Task UpdateCellsAsync(string sheetId, IReadOnlyList<(int Row, string Column, string Value)> cells, CancellationToken ct = default);
    /// <summary>Appends rows.</summary>
    Task AppendRowsAsync(string sheetId, IReadOnlyList<IReadOnlyList<string>> rows, CancellationToken ct = default);
    /// <summary>Replaces the header row (used when the app adds a column).</summary>
    Task SetHeadersAsync(string sheetId, IReadOnlyList<string> headers, CancellationToken ct = default);
}

/// <summary>Builds the workspace client for one user from that user's own stored authorization.</summary>
public interface IWorkspaceFactory
{
    /// <summary>The client for <paramref name="userId"/>'s connection; throws <see cref="AuthExpiredException"/> when there is none.</summary>
    IGoogleWorkspace ForUser(string userId);
}
