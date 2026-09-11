#!/usr/bin/env bash
# trim-aot-canonical-bytes-probe.sh — PUBLISH the certification contracts trimmed and
# ahead-of-time, RUN each published binary, and check that the canonical bytes it emits are
# byte-identical to the checked-in golden corpora.
#
# WHY THIS LANE EXISTS
# --------------------
# The canonical payload is the message every certification signature is computed over, and the
# certified transition entry hash is what StateLogVerifier recomputes for every entry in an
# attested state log. Both are produced by code that a host may publish however it likes, and
# `dotnet build` exercises exactly one of those ways. Reflection-based serialization does not
# survive trimming or ahead-of-time publishing: under a trimmed publish a payload could
# serialize to an empty object, with no exception and no warning, and bytes that back a
# signature must never be silently empty. Both producers are written field by field for that
# reason. This probe is the measurement that says so, rather than the claim.
#
# It publishes SELF-CONTAINED linux-x64 and runs the result in place, so the binary under test
# is the trimmed or AOT-compiled one and not a framework-dependent stand-in. The consumer lives
# OUTSIDE the repo so its own build is not rewritten by Directory.Build.props, and it reads the
# corpora with JsonDocument and hand-written property access — a reader that used the
# reflection-based deserializer would fail for its own reasons in exactly the configurations
# under test, and would prove nothing about the writer.
#
# POSITIVE AND NEGATIVE. Byte equality alone is a weak assertion: a build whose verifier had
# been trimmed into unconditional agreement would emit the right bytes and pass. So each
# published binary is also required to say NO - to a tampered signature, to a content hash that
# does not bind, to a schema version that selects no payload lane, to a proposer that supplies
# one parameter key twice, to a double JSON has no number for - and the byte comparison itself
# is shown to be live by feeding it a deliberately mutated expectation. This is the shape
# scripts/portability/net9-probe.sh already uses for the same reason.
#
# THE CONSTANTS ARE NOT DUPLICATED. This probe reads the same
# src/Ashlar.Tests.Infrastructure/Tests/Certification/canonical-payloads.golden.json and
# transition-entry-hashes.golden.json the xunit golden suites and the netstandard2.0 probe read.
#
# NOT COVERED: CompositionCertificationRecordSigner lives in Ashlar.Infrastructure, whose
# dependency graph is not published trimmed or AOT anywhere; its bytes are pinned by
# composition-payloads.golden.json and it is written the same way, but this lane does not
# execute it.
#
# Usage:
#   scripts/portability/trim-aot-canonical-bytes-probe.sh            # every configuration
#   scripts/portability/trim-aot-canonical-bytes-probe.sh trim-full  # one, by name
#
# Configuration names: trim-partial, trim-full, trim-full-reflection-on, aot.
# TRIM_AOT_PROBE_WORKDIR pins the scratch directory; every check line is teed to
# ${TRIM_AOT_PROBE_WORKDIR}/trim-aot-probe.log so a CI job can re-read the results rather than
# re-derive them, and so a summary that reports nothing is not mistaken for a summary of nothing.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GOLDEN="${ROOT}/src/Ashlar.Tests.Infrastructure/Tests/Certification/canonical-payloads.golden.json"
TRANSITIONS="${ROOT}/src/Ashlar.Tests.Infrastructure/Tests/Certification/transition-entry-hashes.golden.json"
RID="${TRIM_AOT_PROBE_RID:-linux-x64}"

for corpus in "${GOLDEN}" "${TRANSITIONS}"; do
  if [[ ! -f "${corpus}" ]]; then
    echo "trim-aot-canonical-bytes-probe: corpus not found at ${corpus}" >&2
    exit 1
  fi
done

SELECTED="${1:-}"

WORK="${TRIM_AOT_PROBE_WORKDIR:-}"
if [[ -z "${WORK}" ]]; then
  WORK="$(mktemp -d)"
  trap 'rm -rf "${WORK}"' EXIT
fi
mkdir -p "${WORK}/consumer"

# One log carrying every check line. A CI job re-reads this rather than re-deriving the result
# from the raw step output - and a summary step that finds no log says so, rather than rendering
# an empty table that reads like a pass.
LOG="${WORK}/trim-aot-probe.log"
: >"${LOG}"

cp "${GOLDEN}" "${WORK}/canonical-payloads.golden.json"
cp "${TRANSITIONS}" "${WORK}/transition-entry-hashes.golden.json"

# The publish settings go INSIDE the consumer project, never on the command line. A -p: switch
# is an MSBuild GLOBAL property and would flow into the referenced projects, whose netstandard2.0
# leg then refuses to build at all (NETSDK1124 / NETSDK1207) - the configuration under test would
# never be reached. Written into the project it applies to the app being published, which is
# where trimming and AOT compilation are decided anyway.
write_consumer_project() {
  cat > "${WORK}/consumer/TrimAotCanonicalBytesProbe.csproj" <<PROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>TrimAotCanonicalBytesProbe</AssemblyName>
    <RootNamespace>TrimAotCanonicalBytesProbe</RootNamespace>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <RuntimeIdentifier>${RID}</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <InvariantGlobalization>true</InvariantGlobalization>
    <!-- Trim and AOT analysis warnings are the point of the report at the end of a run, not a
         reason to fail the build before it can be executed. -->
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
${1}
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="${ROOT}/src/Ashlar.Certification.Contracts/Ashlar.Certification.Contracts.csproj" />
    <ProjectReference Include="${ROOT}/src/Ashlar.Certification.State/Ashlar.Certification.State.csproj" />
  </ItemGroup>
</Project>
PROJ
}

cat > "${WORK}/consumer/Program.cs" <<'PROG'
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Certification.State;

internal static class TrimAotCanonicalBytesProbe
{
    private static int Main(string[] args)
    {
        var goldenPath = args.Length > 0 ? args[0] : "canonical-payloads.golden.json";
        var transitionsPath = args.Length > 1 ? args[1] : "transition-entry-hashes.golden.json";

        Console.WriteLine("configuration: " + (args.Length > 2 ? args[2] : "(unlabelled)"));
        Console.WriteLine("runtime:       " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

        var failures = CheckCanonicalBytes(goldenPath);
        failures += CheckTransitionEntryHashes(transitionsPath);
        failures += CheckRefusals(goldenPath);
        Console.WriteLine(failures == 0 ? "PROBE PASS" : "PROBE FAIL (" + failures + ")");
        return failures == 0 ? 0 : 1;
    }

    private static int CheckCanonicalBytes(string path)
    {
        Console.WriteLine("== canonical signing payloads ==");
        var failures = 0;
        var checkedCases = 0;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var element in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = element.GetProperty("name").GetString();
            var record = ReadRecord(element.GetProperty("record"));
            var expectedPayload = element.GetProperty("payload").GetString();
            var expectedSha = element.GetProperty("payloadSha256").GetString();
            var expectedLength = element.GetProperty("payloadByteLength").GetInt32();

            var payload = CertificationRecordSigning.BuildPayload(record);
            var bytes = Encoding.UTF8.GetBytes(payload);
            var sha = Convert.ToHexString(SHA256.HashData(bytes));

            checkedCases++;
            if (payload == expectedPayload && sha == expectedSha && bytes.Length == expectedLength)
            {
                Console.WriteLine("  OK   " + name + "  " + sha + "  " + bytes.Length + " bytes");
                continue;
            }

            failures++;
            Console.WriteLine("  FAIL " + name);
            Console.WriteLine("    expected " + expectedSha + "  " + expectedLength + " bytes");
            Console.WriteLine("    actual   " + sha + "  " + bytes.Length + " bytes");
            Console.WriteLine("    expected payload " + expectedPayload);
            Console.WriteLine("    actual   payload " + payload);
        }

        if (checkedCases == 0)
        {
            Console.WriteLine("FAIL: the golden corpus is empty; a probe that checks nothing is not a passing probe.");
            return 1;
        }

        Console.WriteLine(failures == 0
            ? "PASS: " + checkedCases + " canonical payloads are byte-identical."
            : "FAIL: " + failures + " of " + checkedCases + " canonical payloads differ.");
        return failures;
    }

    private static int CheckTransitionEntryHashes(string path)
    {
        Console.WriteLine("== certified transition entry hashes ==");
        var builder = new CertifiedTransitionBuilder();
        var failures = 0;
        var checkedCases = 0;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var element in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = element.GetProperty("name").GetString();
            var record = element.GetProperty("record");
            var expected = element.GetProperty("entryHash").GetString();
            var actual = builder.ComputeEntryHash(
                record.GetProperty("priorStateHash").GetString()!,
                record.GetProperty("action").GetString()!,
                record.GetProperty("behaviorCertContentHash").GetString()!,
                record.GetProperty("resultingStateHash").GetString()!,
                record.GetProperty("prevEntryHash").GetString()!);

            checkedCases++;
            if (actual == expected)
            {
                Console.WriteLine("  OK   " + name + "  " + actual);
                continue;
            }

            failures++;
            Console.WriteLine("  FAIL " + name);
            Console.WriteLine("    expected entryHash " + expected);
            Console.WriteLine("    actual   entryHash " + actual);
        }

        if (checkedCases == 0)
        {
            Console.WriteLine("FAIL: the transition corpus is empty; a probe that checks nothing is not a passing probe.");
            return 1;
        }

        Console.WriteLine(failures == 0
            ? "PASS: " + checkedCases + " transition entry hashes are identical."
            : "FAIL: " + failures + " of " + checkedCases + " transition entry hashes differ.");
        return failures;
    }

    // Byte equality says the writer agrees with the corpus. It says nothing about whether this
    // build can still refuse anything: a verifier trimmed into unconditional agreement emits the
    // same bytes and passes every check above. Each of these is a case where the published binary
    // must answer no, and the last one makes the comparison itself prove it is live.
    private static int CheckRefusals(string goldenPath)
    {
        Console.WriteLine("== refusals: the published binary must still be able to say no ==");
        const string hmacKey = "trim-aot-canonical-bytes-probe-hmac";
        const string brickSource = "class TrimAotCanonicalBytesProbeBrick { }";
        var failures = 0;
        var checks = 0;

        var bound = new CertificationRecordData
        {
            Status = "PASS",
            Stage = "S0-S2",
            Admitted = true,
            Signed = true,
            Timestamp = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
            BrickId = "trim-aot-probe-brick",
            ContentHash = BrickContentHasher.ComputeSha256(brickSource),
        };
        bound = bound with { Signature = CertificationRecordSigning.Sign(bound, hmacKey) };

        // The baseline. Without it every refusal below could be produced by a build that refuses
        // everything, which is not the property under test either.
        failures += ExpectVerdict(ref checks, "untampered v1 record under Legacy", bound, brickSource, hmacKey, null);

        var forged = Convert.FromBase64String(bound.Signature!);
        forged[0] ^= 0x01;
        failures += ExpectVerdict(ref checks, "signature altered by one bit",
            bound with { Signature = Convert.ToBase64String(forged) }, brickSource, hmacKey, "signature-invalid");
        failures += ExpectVerdict(ref checks, "signature over other bytes (wrong key)",
            bound, brickSource, "another-key", "signature-invalid");
        failures += ExpectVerdict(ref checks, "content hash no longer binds",
            bound, brickSource + " ", hmacKey, "content-hash-mismatch");

        // A version that selects no payload lane has no bytes for a signature to cover, so it must
        // be refused with its own code rather than serialized under a lane chosen by guesswork.
        var unknownVersion = bound with { SchemaVersion = 3 };
        failures += ExpectVerdict(ref checks, "schemaVersion 3 selects no lane",
            unknownVersion, brickSource, hmacKey, "schema-version-unknown");
        failures += ExpectRefusesToBuild(ref checks, "schemaVersion 3 payload", unknownVersion);

        // A JSON writer does not reject a duplicate property name, so this payload would be signed
        // happily and would describe no single record. Only a custom IReadOnlyDictionary can
        // produce it - and it is exactly the kind of refusal a trimmed publish could drop.
        var duplicateKey = bound with
        {
            SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
            Proposer = new CertificationProposer
            {
                Identity = "agent://trim-aot-probe",
                Parameters = new RepeatedKeyParameters(
                    new KeyValuePair<string, string>("seed", "1"),
                    new KeyValuePair<string, string>("seed", "2")),
            },
        };
        failures += ExpectRefusesToBuild(ref checks, "repeated proposer parameter key", duplicateKey);
        failures += Expect(ref checks, "repeated key refuses verification rather than throwing",
            !CertificationRecordSigning.VerifySignature(duplicateKey, hmacKey));

        // JSON has no number for these, so refusal is the canonical answer. Refused as a
        // canonical-payload fault, not left to throw ArgumentException past the verifier's catch.
        foreach (var nonFinite in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            failures += ExpectRefusesToBuild(ref checks, "escapeRate " + nonFinite.ToString(CultureInfo.InvariantCulture),
                bound with { EscapeRate = nonFinite });
        }

        // And the comparison itself. Feed it an expectation that is deliberately wrong: if this
        // reports a match, every OK line above means nothing.
        using (var document = JsonDocument.Parse(File.ReadAllText(goldenPath)))
        {
            var first = document.RootElement.GetProperty("cases")[0];
            var payload = CertificationRecordSigning.BuildPayload(ReadRecord(first.GetProperty("record")));
            var mutated = first.GetProperty("payload").GetString()! + " ";
            failures += Expect(ref checks, "a mutated expectation is not accepted as a match", payload != mutated);
        }

        if (checks == 0)
        {
            Console.WriteLine("FAIL: no refusal was exercised; a probe that checks nothing is not a passing probe.");
            return 1;
        }

        Console.WriteLine(failures == 0
            ? "PASS: " + checks + " refusals answered as they do on a plain build."
            : "FAIL: " + failures + " of " + checks + " refusals did not.");
        return failures;
    }

    private static int Expect(ref int checks, string what, bool ok)
    {
        checks++;
        Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what);
        return ok ? 0 : 1;
    }

    private static int ExpectVerdict(
        ref int checks, string what, CertificationRecordData record, string source, string key, string? expectedCode)
    {
        // Legacy options throughout: this consumer signs HMAC only, and Default would refuse for
        // want of an Ed25519 signature before reaching the code each case is about.
        var result = CertificationTrustVerifier.Verify(record, source, key, CertificationVerifyOptions.Legacy);
        var ok = expectedCode is null ? result.Trusted : !result.Trusted && result.FailureCode == expectedCode;
        checks++;
        Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what + " -> "
            + (result.Trusted ? "TRUSTED" : result.FailureCode) + (ok ? string.Empty : "  (expected " + (expectedCode ?? "TRUSTED") + ")"));
        return ok ? 0 : 1;
    }

    private static int ExpectRefusesToBuild(ref int checks, string what, CertificationRecordData record)
    {
        string outcome;
        bool ok;
        try
        {
            _ = CertificationRecordSigning.BuildPayload(record);
            outcome = "returned a payload";
            ok = false;
        }
        catch (CanonicalPayloadException)
        {
            outcome = "CanonicalPayloadException";
            ok = true;
        }
        catch (Exception ex)
        {
            outcome = ex.GetType().Name + " (not the canonical-payload refusal)";
            ok = false;
        }

        checks++;
        Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what + " -> " + outcome);
        return ok ? 0 : 1;
    }

    // The corpus is read with JsonDocument and hand-written property access on purpose. The
    // reflection-based deserializer is exactly what does not survive these publish modes, so a
    // reader that used it would fail for its own reasons and say nothing about the writer.
    private static CertificationRecordData ReadRecord(JsonElement e) => new()
    {
        Status = Str(e, "status") ?? string.Empty,
        Stage = Str(e, "stage") ?? string.Empty,
        Admitted = Bool(e, "admitted"),
        Signed = Bool(e, "signed"),
        Timestamp = DateTimeOffset.Parse(
            Str(e, "timestamp") ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        BrickId = Str(e, "brickId") ?? string.Empty,
        ContentHash = Str(e, "contentHash"),
        EscapeRate = Dbl(e, "escapeRate"),
        TotalMutants = Int(e, "totalMutants"),
        SurvivingMutants = Int(e, "survivingMutants"),
        KilledMutants = StrArray(e, "killedMutants"),
        SurvivingMutantIds = StrArray(e, "survivingMutantIds"),
        Signature = Str(e, "signature"),
        Reason = Str(e, "reason"),
        Gate = Str(e, "gate"),
        SchemaVersion = Int(e, "schemaVersion"),
        GatesPassed = ReadGatesPassed(e),
        Inputs = ReadInputs(e),
        Proposer = ReadProposer(e),
        Attempts = ReadAttempts(e),
        Ed25519Signature = Str(e, "ed25519Signature"),
        Ed25519PublicKey = Str(e, "ed25519PublicKey"),
    };

    private static IReadOnlyList<CertificationGatePass> ReadGatesPassed(JsonElement e)
    {
        var list = new List<CertificationGatePass>();
        foreach (var item in Array(e, "gatesPassed"))
        {
            list.Add(new CertificationGatePass
            {
                Name = Str(item, "name") ?? string.Empty,
                Version = Str(item, "version"),
                Configuration = Str(item, "configuration"),
            });
        }

        return list;
    }

    private static IReadOnlyList<CertificationInput> ReadInputs(JsonElement e)
    {
        var list = new List<CertificationInput>();
        foreach (var item in Array(e, "inputs"))
        {
            list.Add(new CertificationInput
            {
                Kind = Str(item, "kind") ?? string.Empty,
                Id = Str(item, "id") ?? string.Empty,
                Hash = Str(item, "hash") ?? string.Empty,
            });
        }

        return list;
    }

    private static IReadOnlyList<CertificationAttempt> ReadAttempts(JsonElement e)
    {
        var list = new List<CertificationAttempt>();
        foreach (var item in Array(e, "attempts"))
        {
            list.Add(new CertificationAttempt
            {
                Index = Int(item, "index") ?? 0,
                Outcome = Str(item, "outcome") ?? string.Empty,
                FailureCategory = Str(item, "failureCategory"),
                DurationSeconds = Dbl(item, "durationSeconds"),
            });
        }

        return list;
    }

    private static CertificationProposer? ReadProposer(JsonElement e)
    {
        if (!e.TryGetProperty("proposer", out var proposer) || proposer.ValueKind != JsonValueKind.Object)
            return null;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (proposer.TryGetProperty("parameters", out var raw) && raw.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in raw.EnumerateObject())
                parameters[member.Name] = member.Value.GetString() ?? string.Empty;
        }

        return new CertificationProposer
        {
            Identity = Str(proposer, "identity") ?? string.Empty,
            Parameters = parameters,
            Seed = Str(proposer, "seed"),
        };
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string name)
    {
        if (e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                yield return item;
        }
    }

    private static IReadOnlyList<string> StrArray(JsonElement e, string name)
    {
        var list = new List<string>();
        foreach (var item in Array(e, name))
            list.Add(item.GetString() ?? string.Empty);
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static double? Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    // Yields the same key twice. Dictionary<,> cannot, which is the only reason this exists.
    private sealed class RepeatedKeyParameters : IReadOnlyDictionary<string, string>
    {
        private readonly KeyValuePair<string, string>[] _entries;

        public RepeatedKeyParameters(params KeyValuePair<string, string>[] entries) => _entries = entries;

        public int Count => _entries.Length;

        public IEnumerable<string> Keys => _entries.Select(e => e.Key);

        public IEnumerable<string> Values => _entries.Select(e => e.Value);

        public string this[string key] => _entries.First(e => e.Key == key).Value;

        public bool ContainsKey(string key) => _entries.Any(e => e.Key == key);

        public bool TryGetValue(string key, out string value)
        {
            foreach (var entry in _entries)
            {
                if (entry.Key == key)
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = null!;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, string>>)_entries).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
PROG

run_configuration() {
  local label="$1"
  local properties="$2"
  if [[ -n "${SELECTED}" && "${SELECTED}" != "${label}" ]]; then
    return 0
  fi

  local out="${WORK}/publish/${label}"
  local log="${WORK}/${label}.publish.log"
  echo "configuration: ${label}" >>"${LOG}"
  echo ""
  echo "== ${label}: publish self-contained ${RID} =="
  write_consumer_project "${properties}"
  rm -rf "${out}" "${WORK}/consumer/obj" "${WORK}/consumer/bin"
  dotnet publish "${WORK}/consumer/TrimAotCanonicalBytesProbe.csproj" \
    -c Release -o "${out}" --nologo -v minimal >"${log}" 2>&1 || {
      echo "FAIL ${label}: publish failed; last 40 lines of ${log}"
      tail -40 "${log}"
      return 1
    }

  local warnings
  warnings="$(grep -cE ': warning (IL|AOT|TRIM)[0-9]+' "${log}" || true)"
  echo "   trim/AOT analysis warnings: ${warnings}"
  echo "trim/AOT analysis warnings: ${warnings}" >>"${LOG}"

  echo "== ${label}: run the published binary =="
  # set -o pipefail is in effect, so tee does not swallow a failing binary.
  "${out}/TrimAotCanonicalBytesProbe" \
    "${WORK}/canonical-payloads.golden.json" \
    "${WORK}/transition-entry-hashes.golden.json" \
    "${label}" 2>&1 | tee -a "${LOG}"
}

FAILURES=0
run_configuration "trim-partial" \
  "    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>partial</TrimMode>" || FAILURES=$((FAILURES + 1))
run_configuration "trim-full" \
  "    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>full</TrimMode>" || FAILURES=$((FAILURES + 1))
# The configuration that used to emit the empty object: full trimming with the reflection-based
# serializer explicitly left enabled, so nothing throws and the payload simply comes out short.
run_configuration "trim-full-reflection-on" \
  "    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>full</TrimMode>
    <JsonSerializerIsReflectionEnabledByDefault>true</JsonSerializerIsReflectionEnabledByDefault>" \
  || FAILURES=$((FAILURES + 1))
run_configuration "aot" "    <PublishAot>true</PublishAot>" || FAILURES=$((FAILURES + 1))

echo ""
if [[ "${FAILURES}" -ne 0 ]]; then
  echo "trim-aot-canonical-bytes-probe: ${FAILURES} configuration(s) did not emit the golden bytes" >&2
  exit 1
fi
echo "trim-aot-canonical-bytes-probe: every configuration emitted the golden bytes"
