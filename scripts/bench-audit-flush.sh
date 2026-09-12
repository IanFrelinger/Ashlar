#!/usr/bin/env bash
# bench-audit-flush.sh — measure LiteDbDataDecisionAuditLog append throughput and print the numbers.
#
# WHY THIS IS A SCRIPT AND NOT A TEST
# -----------------------------------
# The flush is the one place Shared mode costs something measurable: LiteDB takes its named mutex
# and opens an engine PER OPERATION, and the flush writes ten documents per batch. Wrapping the
# insert loop in one transaction pays that cost once instead of ten times. That is worth measuring
# and it is NOT worth asserting: a wall-clock threshold on a shared CI runner over a mutex-and-fsync
# path whose cost differs by an order of magnitude across ext4, APFS and NTFS-with-Defender cannot
# be both sensitive and stable, and this repository's demonstrated response to a flaky gate is to
# mute it — taking whatever else shares that lane with it. So this prints; nothing here fails a
# build, and nothing in CI runs it.
#
# The correctness half IS asserted, in Ashlar.Tests.BackgroundAgents:
# LiteDbDataDecisionAuditLogFlushTests pins that every append reaches disk exactly once and that a
# flush which cannot open the database strands nothing.
#
# Usage:
#   scripts/bench-audit-flush.sh                 # defaults: 8 threads x 250 appends
#   BENCH_THREADS=1 BENCH_APPENDS=2000 scripts/bench-audit-flush.sh
#   BENCH_TFM=net10.0 scripts/bench-audit-flush.sh
#
# Run it in the devtest container, not on the host:
#   actest.sh <clone> --dirty -- 'bash scripts/bench-audit-flush.sh'
#
# Report the platform with any number this prints. Everything measured here so far is Linux.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
THREADS="${BENCH_THREADS:-8}"
APPENDS="${BENCH_APPENDS:-250}"
TFM="${BENCH_TFM:-net8.0}"
WORKDIR="${BENCH_WORKDIR:-$(mktemp -d)}"

mkdir -p "${WORKDIR}/bench"

cat > "${WORKDIR}/bench/bench.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>${TFM}</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="${ROOT}/src/Ashlar.BackgroundAgents/Ashlar.BackgroundAgents.csproj" />
  </ItemGroup>
</Project>
EOF

cat > "${WORKDIR}/bench/Program.cs" <<'EOF'
using System.Diagnostics;
using Ashlar.BackgroundAgents.Trust;
using Ashlar.Core.Application.Trust.Ports;

var threads = int.Parse(args[0]);
var appends = int.Parse(args[1]);
var dir = Path.Combine(Path.GetTempPath(), "ashlar-bench-audit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var path = Path.Combine(dir, "audit.db");

var log = new LiteDbDataDecisionAuditLog(path);

// One append outside the measurement so the first flush's index declaration, the mapper warm-up and
// the JIT are not counted as throughput.
log.LogSanitization(new SanitizationAuditEntryDto(DateTimeOffset.UtcNow, "v1", "warmup", "Kept", null));

var ready = new Barrier(threads);
var latencies = new List<double>[threads];
var sw = Stopwatch.StartNew();

var workers = Enumerable.Range(0, threads).Select(t => Task.Factory.StartNew(() =>
{
    var mine = new List<double>(appends);
    latencies[t] = mine;
    ready.SignalAndWait();
    for (var i = 0; i < appends; i++)
    {
        var one = Stopwatch.StartNew();
        log.LogSanitization(new SanitizationAuditEntryDto(
            DateTimeOffset.UtcNow, "v1", $"field-{t}-{i}", "Redacted", "bench"));
        one.Stop();
        mine.Add(one.Elapsed.TotalMilliseconds);
    }
}, TaskCreationOptions.LongRunning)).ToArray();

Task.WaitAll(workers);
sw.Stop();

// Drain the sub-threshold tail so the readback counts everything that was accepted. GetRecent
// flushes first, under the same gate.
var total = threads * appends + 1;
var readable = log.GetRecent(total + 10).Count;

var all = latencies.SelectMany(x => x).OrderBy(x => x).ToArray();
double Pct(double p) => all[Math.Min(all.Length - 1, (int)(all.Length * p))];

Console.WriteLine($"threads={threads} appendsPerThread={appends} totalAppends={threads * appends}");
Console.WriteLine($"elapsedMs={sw.Elapsed.TotalMilliseconds:F0}");
Console.WriteLine($"appendsPerSecond={(threads * appends) / sw.Elapsed.TotalSeconds:F0}");
Console.WriteLine($"appendP50Ms={Pct(0.50):F3} appendP95Ms={Pct(0.95):F3} appendP99Ms={Pct(0.99):F3} appendMaxMs={all[^1]:F3}");
Console.WriteLine($"readableEntries={readable} expectedEntries={total}");
Console.WriteLine(readable == total ? "loss=none" : $"loss={total - readable}");

try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
EOF

echo "bench-audit-flush: tfm=${TFM} threads=${THREADS} appendsPerThread=${APPENDS}"
dotnet run --project "${WORKDIR}/bench/bench.csproj" -c Release -v quiet -- "${THREADS}" "${APPENDS}"
