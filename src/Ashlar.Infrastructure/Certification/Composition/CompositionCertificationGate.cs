using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Infrastructure.Certification.Composition;

/// <summary>Certification gate for multi-brick composition graphs (topology, seams, mutants).</summary>
public sealed class CompositionCertificationGate : ICompositionCertificationGate
{
    private readonly ICertificationRecordStore _brickCertificationStore;
    private readonly CertificationRecordSigner _brickSigner;
    private readonly IBrickRegistry _brickRegistry;
    private readonly CompositionCertificationRecordSigner _compositionSigner;
    private readonly CompositionGraphMutationEngine _mutationEngine = new();
    private readonly CompositionExecutor _executor = new();
    private readonly ILogger<CompositionCertificationGate>? _logger;

    /// <summary>Initializes a new composition certification gate.</summary>
    public CompositionCertificationGate(
        ICertificationRecordStore brickCertificationStore,
        CertificationRecordSigner brickSigner,
        IBrickRegistry brickRegistry,
        CompositionCertificationRecordSigner compositionSigner,
        ILogger<CompositionCertificationGate>? logger = null)
    {
        _brickCertificationStore = brickCertificationStore ?? throw new ArgumentNullException(nameof(brickCertificationStore));
        _brickSigner = brickSigner ?? throw new ArgumentNullException(nameof(brickSigner));
        _brickRegistry = brickRegistry ?? throw new ArgumentNullException(nameof(brickRegistry));
        _compositionSigner = compositionSigner ?? throw new ArgumentNullException(nameof(compositionSigner));
        _logger = logger;

        // Limitation 9's last residual. This gate holds both signers and, until now, never asked
        // whether they agree — so a host could check constituent atom signatures with one key and mint
        // the composition admission under another, and nothing said so. A composition certificate is
        // supposed to attest the chain its constituents were signed into; under split keys it does not.
        //
        // This WARNS and never refuses, on purpose. The check is reference identity, not a key
        // comparison, so it FALSE-POSITIVES on a host that deliberately built two signers holding the
        // same explicit key: that host is correctly configured and is warned anyway. Refusing on a
        // heuristic would convert a silent security weakness into a startup failure, which is a
        // separate availability decision and not one a key-threading fix gets to make.
        //
        // The dev-key exemption below carries a matching FALSE NEGATIVE, stated here precisely
        // because "provably the same key" overstated it. Both flags are computed at construction.
        // The brick lane is late-binding — CertificationRecordSigning.ResolveKey re-reads
        // ASHLAR_CERT_DEV_HMAC_KEY on every call — while a composition signer built WITHOUT a brick
        // signer freezes its key bytes in its constructor. So if the variable is set after both are
        // built, the lanes diverge and this check stays silent, because both answered "dev key" at
        // the moment it was asked. Delegation does not have this problem: a composition signer that
        // derives from the brick signer inherits its late binding too.
        //
        // Two signers both on the committed dev key are exempt anyway: they agree at construction,
        // and warning there would fire on every host that configured nothing — and a warning that
        // fires on correct configurations stops being read.
        if (!_compositionSigner.SharesKeyHolderWith(_brickSigner)
            && !(_compositionSigner.UsesDevKey && _brickSigner.UsesDevKey))
        {
            _logger?.LogWarning(
                "The composition lane and the brick lane may not be signing under the same key. This gate "
                + "verifies constituent brick records with one signer and mints the composition record with "
                + "another, and they were built independently, so a composition certificate may not attest "
                + "the chain it appears to. Supply one {Signer} and let the composition signer derive from "
                + "it, which is what AddCertificationInfrastructure does. If the two deliberately hold the "
                + "same key, this warning is expected and can be ignored.",
                nameof(CertificationRecordSigner));
        }
    }

    /// <summary>Certify asynchronously.</summary>
    public async Task<CompositionCertificationDecision> CertifyAsync(
        CompositionCertificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var compositionId = request.Spec.CompositionId;
        var timestamp = DateTimeOffset.UtcNow;

        CompositionCertificationRecord Fail(
            string check,
            string reason,
            CompositionMutationTestResult? mutation = null) =>
            BuildRecord(false, false, "FAIL", compositionId, timestamp, reason, mutation);

        var constituents = CompositionConstituentChecker.Check(
            request.Spec,
            _brickCertificationStore,
            _brickSigner);
        if (!constituents.Passed)
        {
            var reason = $"Constituent integrity failed: {string.Join("; ", constituents.Violations)}";
            return Reject("constituents", reason, Fail("constituents", reason));
        }

        var seam = CompositionSeamChecker.Check(request.Spec, _brickRegistry);
        if (!seam.Passed)
        {
            var reason = $"Seam contract failed: {string.Join("; ", seam.Violations)}";
            return Reject("seam", reason, Fail("seam", reason));
        }

        var witnessResult = await _executor.RunWitnessAsync(
            request.Spec,
            request.Witness,
            _brickRegistry,
            new AuditExecutionContext(),
            cancellationToken).ConfigureAwait(false);

        if (!witnessResult.Passed)
        {
            var reason = $"Correctness check failed: {string.Join("; ", witnessResult.Failures)}";
            return Reject("correctness", reason, Fail("correctness", reason));
        }

        var mutationResult = await _mutationEngine.RunAsync(
            request.Spec,
            request.Witness,
            _brickRegistry,
            cancellationToken).ConfigureAwait(false);

        if (mutationResult.TotalMutants == 0)
        {
            var reason =
                "Graph-mutation escape check failed: no structural mutants — composition too trivial or witness shape inadequate";
            return Reject("mutation", reason, Fail("mutation", reason, mutationResult));
        }

        if (mutationResult.EscapeRate > 0)
        {
            var reason =
                $"Graph-mutation escape check failed: composition_escape_rate={mutationResult.EscapeRate:F2}, survivors=[{string.Join(", ", mutationResult.SurvivingMutantIds)}]";
            return Reject("mutation", reason, Fail("mutation", reason, mutationResult));
        }

        var determinism = await _executor.CheckDeterminismAsync(
            request.Spec,
            request.Witness,
            _brickRegistry,
            cancellationToken).ConfigureAwait(false);

        if (!determinism.Identical)
        {
            var reason = "Determinism check failed: composition outputs differ under AuditMode";
            return Reject("determinism", reason, Fail("determinism", reason, mutationResult));
        }

        var dependency = CompositionDependencyChecker.Check(request.WiringMetadata);
        if (!dependency.Passed)
        {
            var reason = $"Dependency-cleanliness failed: {string.Join("; ", dependency.Violations)}";
            return Reject("dependency", reason, Fail("dependency", reason, mutationResult));
        }

        var admittedRecord = BuildRecord(true, true, "PASS", compositionId, timestamp, null, mutationResult);
        admittedRecord = admittedRecord with { Signature = _compositionSigner.Sign(admittedRecord) };

        _logger?.LogInformation("Composition certification ADMIT {CompositionId} composition_escape_rate=0", compositionId);
        return new CompositionCertificationDecision
        {
            Admitted = true,
            Record = admittedRecord
        };
    }

    private CompositionCertificationDecision Reject(
        string check,
        string reason,
        CompositionCertificationRecord record)
    {
        _logger?.LogWarning("Composition certification REJECT: {Reason}", reason);
        return new CompositionCertificationDecision
        {
            Admitted = false,
            FailureCheck = check,
            Record = record
        };
    }

    private static CompositionCertificationRecord BuildRecord(
        bool admitted,
        bool signed,
        string status,
        string compositionId,
        DateTimeOffset timestamp,
        string? reason,
        CompositionMutationTestResult? mutation)
    {
        return new CompositionCertificationRecord
        {
            Status = status,
            Stage = "S0-S2-composition",
            Admitted = admitted,
            Signed = signed,
            Timestamp = timestamp,
            CompositionId = compositionId,
            CompositionEscapeRate = mutation?.EscapeRate ?? 0,
            TotalStructuralMutants = mutation?.TotalMutants ?? 0,
            SurvivingStructuralMutants = mutation?.SurvivingMutantIds.Count ?? 0,
            KilledStructuralMutantIds = mutation?.KilledMutantIds ?? Array.Empty<string>(),
            SurvivingStructuralMutantIds = mutation?.SurvivingMutantIds ?? Array.Empty<string>(),
            Reason = reason,
            Gate = "Ashlar.Infrastructure.Certification.Composition.CompositionCertificationGate"
        };
    }
}
