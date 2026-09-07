using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Scheduling;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Infrastructure.Certification;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect.Models;
using Ashlar.Runtime;
using Ashlar.Tests.BackgroundAgents.TestHelpers;

namespace Ashlar.Tests.BackgroundAgents.SelfExtend;

/// <summary>
/// Shared helpers for SX-AUDIT / SX-ENFORCE invariant characterization tests.
/// </summary>
internal static class SelfExtendAuditTestSupport
{
    internal static IReadOnlyList<IPolicy> GetPolicies(PolicyEngine engine)
    {
        var field = typeof(PolicyEngine).GetField("_policies", BindingFlags.NonPublic | BindingFlags.Instance);
        return (IReadOnlyList<IPolicy>)field!.GetValue(engine)!;
    }

    internal static BackgroundAgentRegistry CreateRegistry(
        ISelfExtendRunner? selfExtendRunner = null,
        IAggressivenessModeStore? modeStore = null,
        IApprovalGate? approvalGate = null,
        IDataSensitivityRegistry? sensitivityRegistry = null,
        Ashlar.BackgroundAgents.Logging.IBackgroundAgentLogStore? logStore = null,
        Ashlar.BackgroundAgents.Observations.IObservationStore? observations = null,
        ExtensionCeiling? extensionCeiling = null)
    {
        var scheduler = new AgentScheduler(new ScheduleExecutor(), NullLogger<AgentScheduler>.Instance);
        return new BackgroundAgentRegistry(
            scheduler,
            NullLogger<BackgroundAgentRegistry>.Instance,
            logStore: logStore,
            selfExtendRunner: selfExtendRunner,
            modeStore: modeStore,
            approvalGate: approvalGate,
            sensitivityRegistry: sensitivityRegistry ?? new DataSensitivityRegistry(),
            observations: observations,
            extensionCeiling: extensionCeiling);
    }

    /// <summary>Observation store that keeps everything in a list — enough to see refusals.</summary>
    internal sealed class ListObservationStore : Ashlar.BackgroundAgents.Observations.IObservationStore
    {
        public List<Ashlar.BackgroundAgents.Observations.RuntimeObservation> All { get; } = new();

        public string Location => "in-memory://sx-audit";

        public void Append(Ashlar.BackgroundAgents.Observations.RuntimeObservation observation) => All.Add(observation);

        public IEnumerable<Ashlar.BackgroundAgents.Observations.RuntimeObservation> ReadSince(
            DateTimeOffset? since = null,
            Ashlar.BackgroundAgents.Observations.ObservationKind? kind = null,
            int? limit = null) => All;
    }

    internal static InMemoryAggressivenessModeStore ActiveModeStore()
    {
        var store = new InMemoryAggressivenessModeStore();
        store.SetMode(BackgroundAgentAggressivenessMode.Active);
        return store;
    }

    internal static AgentSpawnSpec BuildSpec(BackgroundAgentConfig config) =>
        new BackgroundAgentSpecBuilder(new DataSensitivityRegistry(), null).BuildSpec(config).ToOrchestrationSpec();

    internal static BackgroundAgentConfig ExtenderConfig(string id, string repoRoot) => new()
    {
        Id = id,
        Role = "extender",
        Enabled = true,
        MaxDataSensitivity = "Public",
        Commands = ["extend"],
        Parameters = new Dictionary<string, object> { ["RepoRoot"] = repoRoot },
        Schedule = new BackgroundAgentSchedule { Type = ScheduleType.Interval, Interval = TimeSpan.FromMinutes(1) },
    };

    internal static (ICertificationRecordStore store, string brickId, string source) SeedCertifiedBrickAdmission(
        string className,
        string? namespaceName = "Ashlar.Bricks.AuditGap")
    {
        var source = $"namespace {namespaceName}; public sealed class {className} {{ }}";
        var brickId = Ashlar.BackgroundAgents.Security.BrickAdmissionPathHelper.ClassNameToBrickId(className);
        var hash = BrickContentHasher.ComputeSha256(source);
        var data = new CertificationRecordData
        {
            Status = "PASS",
            Stage = "admit",
            Admitted = true,
            Signed = true,
            Timestamp = DateTimeOffset.UtcNow,
            BrickId = brickId,
            ContentHash = hash,
            SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
            Inputs =
            [
                new CertificationInput
                {
                    Kind = CertificationInputKinds.GateEmittedArtifact,
                    Id = className,
                    Hash = hash
                },
                CertifierIdentity.ToInput()
            ]
        };
        data = data with { Signature = CertificationRecordSigning.Sign(data) };

        var store = new InMemoryCertificationRecordStore();
        store.Save(new CertificationRecord
        {
            Status = data.Status,
            Stage = data.Stage,
            Admitted = data.Admitted,
            Signed = data.Signed,
            Timestamp = data.Timestamp,
            BrickId = data.BrickId,
            ContentHash = data.ContentHash,
            Signature = data.Signature,
            SchemaVersion = data.SchemaVersion,
            Inputs = data.Inputs
        });

        return (store, brickId, source);
    }

    internal sealed class CountingSelfExtendRunner : ISelfExtendRunner
    {
        public int CallCount { get; private set; }

        public Task<SelfExtendRunResult> RunAsync(string repoRoot, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new SelfExtendRunResult(true, 0, 0, "counted"));
        }

        public Task<SelfExtendRunResult> RunAsync(
            string repoRoot,
            string? objective,
            string? agentName,
            string? modelProvider,
            string? modelName,
            CancellationToken cancellationToken = default) =>
            RunAsync(repoRoot, cancellationToken);

        public Task<SelfExtendRunResult> RunAsync(
            string repoRoot,
            string? objective,
            string? agentName,
            string? modelProvider,
            string? modelName,
            string? agentId,
            CancellationToken cancellationToken = default) =>
            RunAsync(repoRoot, cancellationToken);
    }
}
