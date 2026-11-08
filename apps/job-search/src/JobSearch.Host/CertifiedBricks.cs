using System.Runtime.Loader;
using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;
using JobSearch.Certified;
using DomainBrick = Ashlar.Core.Domain.Bricks.Brick;

namespace JobSearch.Host;

/// <summary>
/// Loads the app's certified bricks the way Ashlar's verified consumer does (samples/verified-brick-consumer): for each
/// brick folder under <c>certified/</c>, it reads the certification record, the single source file and the gate-emitted
/// assembly; verifies the record strictly (HMAC, content hash, artifact hash, and an Ed25519 signature from the pinned
/// signer key in <c>trusted-signer.ed25519.pub</c>); runs the IL import fence over the bytes; and only then loads those
/// same bytes into a collectible load context and instantiates the type the signed record names. Any failure throws,
/// so the app does not start with an unverified rule. Nothing here compiles source.
/// </summary>
public sealed class CertifiedBricks : ICertifiedRules, IDisposable
{
    /// <summary>The pinned signer key file, next to the brick folders.</summary>
    public const string TrustedKeyFile = "trusted-signer.ed25519.pub";
    /// <summary>The record file in each brick folder.</summary>
    public const string RecordFile = "certification.json";

    private readonly Dictionary<string, (DomainBrick Brick, string ContentHash, AssemblyLoadContext Context)> _bricks = new(StringComparer.Ordinal);

    private CertifiedBricks() { }

    /// <summary>The folder the build copies apps/job-search/certified into.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "certified");

    /// <summary>Loads and verifies every brick in <see cref="CertifiedIds.All"/> from <paramref name="directory"/>.</summary>
    public static CertifiedBricks Load(string directory)
    {
        var keyPath = Path.Combine(directory, TrustedKeyFile);
        var keys = File.ReadAllLines(keyPath).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        if (keys.Count == 0)
            throw new CertificationTrustConfigurationException($"{keyPath} lists no trusted Ed25519 public key.");
        var policy = CertificationTrustPolicy.FromTrustedKeys(keys);

        var loaded = new CertifiedBricks();
        try
        {
            var folders = Directory.GetDirectories(directory).Where(d => File.Exists(Path.Combine(d, RecordFile))).ToList();
            foreach (var id in CertifiedIds.All)
            {
                var folder = folders.SingleOrDefault(d => RecordBrickId(d) == id)
                    ?? throw new InvalidOperationException($"certified brick {id}: no certification record under {directory}");
                loaded._bricks[id] = LoadOne(id, folder, policy);
            }
            return loaded;
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
    }

    private static string? RecordBrickId(string folder) =>
        ReadRecord(Path.Combine(folder, RecordFile)).BrickId;

    private static CertificationRecordData ReadRecord(string path) =>
        JsonSerializer.Deserialize<CertificationRecordData>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException($"certification-record-invalid: {path} is empty");

    private static (DomainBrick, string, AssemblyLoadContext) LoadOne(string id, string folder, CertificationTrustPolicy policy)
    {
        var record = ReadRecord(Path.Combine(folder, RecordFile));
        var sources = Directory.GetFiles(folder, "*.cs");
        if (sources.Length != 1)
            throw new InvalidOperationException($"certified brick {id}: expected exactly one source file in {folder}, found {sources.Length}");
        var source = File.ReadAllText(sources[0]);
        var assemblyBytes = File.ReadAllBytes(Path.Combine(folder, CertifiedArtifactExporter.ArtifactFileName));

        var trust = CertificationTrustVerifier.Verify(record, source, assemblyBytes, null, policy.Strict);
        if (!trust.Trusted)
            throw new InvalidOperationException($"certified brick {id} refused: {trust.FailureCode}: {trust.Reason}");

        var bindings = record.Inputs.Where(i => string.Equals(i.Kind, CertificationInputKinds.GateEmittedArtifact, StringComparison.Ordinal)).ToArray();
        if (bindings.Length != 1 || string.IsNullOrWhiteSpace(bindings[0].Id))
            throw new InvalidOperationException($"certified brick {id}: the record must bind exactly one gate-emitted artifact with a type name");

        // Inspect before loading: not even a module initializer may run before the import fence passes.
        IlImportFence.Inspect(assemblyBytes);
        var context = new AssemblyLoadContext($"jobsearch-certified-{id}", isCollectible: true);
        try
        {
            using var stream = new MemoryStream(assemblyBytes, writable: false);
            var assembly = context.LoadFromStream(stream);
            var type = assembly.GetType(bindings[0].Id, throwOnError: false);
            if (type is null || !typeof(DomainBrick).IsAssignableFrom(type) || type.IsAbstract || type.ContainsGenericParameters)
                throw new InvalidOperationException($"certified brick {id}: the record must name a concrete brick type in the verified assembly");
            var brick = (DomainBrick)Activator.CreateInstance(type)!;
            if (!string.Equals(brick.Id, id, StringComparison.Ordinal))
                throw new InvalidOperationException($"certified brick {id}: the loaded brick reports id {brick.Id}");
            return (brick, record.ContentHash ?? "", context);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    /// <inheritdoc />
    public DomainBrick Get(string brickId) =>
        _bricks.TryGetValue(brickId, out var b) ? b.Brick : throw new KeyNotFoundException($"no certified brick {brickId}");

    /// <inheritdoc />
    public string ContentHash(string brickId) =>
        _bricks.TryGetValue(brickId, out var b) ? b.ContentHash : throw new KeyNotFoundException($"no certified brick {brickId}");

    /// <summary>The loaded bricks.</summary>
    public IReadOnlyList<DomainBrick> All => [.. _bricks.Values.Select(b => b.Brick)];

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var (_, _, context) in _bricks.Values)
            context.Unload();
        _bricks.Clear();
    }
}

/// <summary>
/// The brick registry the workflow executor sees: Ashlar's own registry, plus the verified certified instances. Ashlar's
/// <c>CertifiedBrickRegistry</c> admits bricks only through its internal API, so an application that loads certified
/// bricks itself has to put them in front of the executor this way (recorded as F10 in docs/DOGFOOD-FINDINGS.md).
/// </summary>
public sealed class CertifiedOverlayRegistry(IBrickRegistry inner, CertifiedBricks certified) : IBrickRegistry
{
    /// <inheritdoc />
    public DomainBrick? GetBrick(string id) =>
        CertifiedIds.All.Contains(id, StringComparer.Ordinal) ? certified.Get(id) : inner.GetBrick(id);

    /// <inheritdoc />
    public IReadOnlyList<DomainBrick> GetAllBricks() =>
        [.. inner.GetAllBricks().Where(b => !CertifiedIds.All.Contains(b.Id, StringComparer.Ordinal)), .. certified.All];
}
