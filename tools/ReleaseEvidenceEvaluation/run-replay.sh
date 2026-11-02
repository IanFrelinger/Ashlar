#!/usr/bin/env bash
# Build the production candidate from its frozen commit, overlay only this evaluation tool and
# frozen inputs, then replay through the repository's supported Linux devtest container harness.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CANDIDATE=""
OUTPUT=""
MANIFEST=""
TRUTH=""
ALLOW_RERUN=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --candidate-commit) CANDIDATE="${2:?missing candidate commit}"; shift 2 ;;
    --output) OUTPUT="${2:?missing output path}"; shift 2 ;;
    --fixture-manifest) MANIFEST="${2:?missing fixture manifest}"; shift 2 ;;
    --ground-truth) TRUTH="${2:?missing frozen ground truth}"; shift 2 ;;
    --allow-candidate-rerun) ALLOW_RERUN=1; shift ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
if [[ ! "$CANDIDATE" =~ ^[0-9a-fA-F]{40}$ || -z "$OUTPUT" || -z "$MANIFEST" || -z "$TRUTH" ]]; then
  echo 'Require --candidate-commit <full SHA> --fixture-manifest <JSON> --ground-truth <JSON> --output <new directory>.' >&2
  exit 2
fi
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
# An actual clone is required by test-in-container.sh; production sources are checked out at the
# candidate, not whichever later commit contains the evaluation dataset or report.
# Do not use --shared: an absolute objects/alternates path would not exist inside the
# read-only container mount used by test-in-container.sh.
git clone -q --no-hardlinks "$ROOT" "$WORK/repo"
git -C "$WORK/repo" checkout -q --detach "$CANDIDATE"
mkdir -p "$WORK/repo/tools/ReleaseEvidenceEvaluation/replay-inputs/cases"
cp "$ROOT/tools/ReleaseEvidenceEvaluation/ReleaseEvidenceEvaluation.csproj" "$ROOT/tools/ReleaseEvidenceEvaluation/Program.cs" \
  "$WORK/repo/tools/ReleaseEvidenceEvaluation/"
python3 - "$ROOT" "$MANIFEST" "$TRUTH" "$OUTPUT" "$WORK" "$CANDIDATE" "$ALLOW_RERUN" <<'PY'
from pathlib import Path
import hashlib,json,shlex,sys
root,manifest,truth,output,work,candidate,allow_rerun=sys.argv[1:]
root,work=Path(root),Path(work)
manifest,truth,output=Path(manifest).resolve(),Path(truth).resolve(),Path(output).absolute()
if output.exists() or output.is_symlink():
    raise SystemExit('Refusing to replace an existing output directory or link')
output.parent.mkdir(parents=True,exist_ok=True)
output_parent=output.parent.resolve()
manifest_bytes=manifest.read_bytes(); truth_bytes=truth.read_bytes()
data=json.loads(manifest_bytes)
manifest_candidate=data.get('candidate_commit') or data.get('candidateCommit') or data.get('baselineCommit')
if manifest_candidate != candidate and allow_rerun != '1':
    raise SystemExit('Candidate differs from frozen baseline; use explicit --allow-candidate-rerun')
inputs=work/'repo/tools/ReleaseEvidenceEvaluation/replay-inputs'
(inputs/'fixture-manifest.json').write_bytes(manifest_bytes)
(inputs/'ground-truth.json').write_bytes(truth_bytes)
case_args=[]; ids=set()
for i,case in enumerate(data['cases']):
    if case['id'] in ids: raise SystemExit('Duplicate fixture manifest case ID')
    ids.add(case['id'])
    # Combined phase manifests use repository-relative paths; standalone author manifests use
    # their own directory. Resolve only one existing path; ambiguous alternatives are refused.
    rel=Path(case['path'])
    possible=[p.resolve() for p in (root/rel,manifest.parent/rel) if p.is_file()]
    possible=list(dict.fromkeys(possible))
    if len(possible)!=1: raise SystemExit(f'Unresolved or ambiguous input path: {rel}')
    content=possible[0].read_bytes()
    if hashlib.sha256(content).hexdigest()!=case['sha256']:
        raise SystemExit(f'Frozen input changed: {case["id"]}')
    if json.loads(content).get('id')!=case['id']:
        raise SystemExit('Fixture ID differs from manifest')
    name=f'{i:04d}.json'
    (inputs/'cases'/name).write_bytes(content)
    case_args+=['--case','tools/ReleaseEvidenceEvaluation/replay-inputs/cases/'+name]
if not case_args: raise SystemExit('Frozen fixture manifest has no cases')
args=['dotnet','tools/ReleaseEvidenceEvaluation/bin/Debug/net10.0/Ashlar.ReleaseEvidenceEvaluation.dll',
      '--candidate-commit',candidate,'--fixture-manifest','tools/ReleaseEvidenceEvaluation/replay-inputs/fixture-manifest.json',
      '--ground-truth','tools/ReleaseEvidenceEvaluation/replay-inputs/ground-truth.json',
      '--output','/evaluation-output/'+output.name,*case_args]
if allow_rerun == '1': args+=['--allow-candidate-rerun']
build=['dotnet','build','tools/ReleaseEvidenceEvaluation/ReleaseEvidenceEvaluation.csproj',
       '--nologo','-v','minimal','-p:NuGetAudit=false']
# shlex quotes each argument as code, so paths with spaces/metacharacters remain data. The
# repository container shell uses set -e: an explicit conditional must capture an expected
# nonzero collector status before ownership repair, rather than exiting early.
command='if '+shlex.join(build)+' && '+shlex.join(args)+'; then evaluation_status=0; else evaluation_status=$?; fi; '
# Results are new operator-owned files. Preserve their accessibility across the root-run container,
# including failed evaluations. An ownership-repair failure is a distinct hard I/O error.
import os
destination=shlex.quote('/evaluation-output/'+output.name)
command+='if [ -d '+destination+' ]; then if chown -R '+str(os.getuid())+':'+str(os.getgid())+' '+destination+'; then :; else echo "Evaluation output ownership repair failed." >&2; exit 73; fi; fi; exit "$evaluation_status"'
(work/'command').write_text(command)
(work/'output-parent').write_text(str(output_parent))
PY
mkdir -p "$WORK/docker-wrapper"
cat > "$WORK/docker-wrapper/docker" <<'DOCKER'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == run ]]; then
  shift
  exec "$EVALUATION_DOCKER" run --mount "type=bind,src=$EVALUATION_OUTPUT_PARENT,dst=/evaluation-output" "$@"
fi
exec "$EVALUATION_DOCKER" "$@"
DOCKER
chmod +x "$WORK/docker-wrapper/docker"
export EVALUATION_DOCKER
EVALUATION_DOCKER="$(command -v docker)"
export EVALUATION_OUTPUT_PARENT
EVALUATION_OUTPUT_PARENT="$(cat "$WORK/output-parent")"
PATH="$WORK/docker-wrapper:$PATH" bash "$WORK/repo/scripts/test-in-container.sh" \
  --repo "$WORK/repo" --ref "$CANDIDATE" --dirty -- "$(cat "$WORK/command")"
