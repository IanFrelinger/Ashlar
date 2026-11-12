#!/usr/bin/env python3
"""Plan/reconcile SPEC-007 exact-SHA mutation evidence without running dotnet.

Plan inventory is read with git show from the named commit, never from a dirty
working tree. Each batch contains serial segments; each segment is one invocation
of the committed run-spec007-mutation-batch.py with a separate stdout archive.
Reconcile extracted archives under PLAN.batches[].segments[].evidence_dir.
This program does not publish, dispatch, build, mutate sources, or fetch anything.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import types
import xml.etree.ElementTree as ET

SCHEMA = 1
RUNNER = 'scripts/run-spec007-mutation-batch.py'
MANIFEST_DIR = 'scripts/mutations'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(',', ':')).encode('utf-8')


def git(repo, *args):
    return subprocess.check_output(['git', '-C', str(repo), *args], stderr=subprocess.PIPE)


def safe_relative(value):
    if not isinstance(value, str) or not value or '\\' in value or any(ord(c) < 32 for c in value):
        raise ValueError('unsafe relative path')
    p = PurePosixPath(value)
    if p.is_absolute() or str(p) != value or any(x in ('.', '..', '.git') or ':' in x for x in p.parts):
        raise ValueError('unsafe relative path: ' + value)
    return value


def read_blob(repo, sha, path):
    safe_relative(path)
    return git(repo, 'show', sha + ':' + path)


def require_external(path, repo):
    resolved = path.resolve()
    if resolved == repo.resolve() or resolved.is_relative_to(repo.resolve()):
        raise ValueError('output must be outside source checkout')
    return resolved


def full_sha(repo, sha):
    if not re.fullmatch(r'[0-9a-f]{40}', sha):
        raise ValueError('a full 40-character lowercase commit SHA is required')
    resolved = git(repo, 'rev-parse', '--verify', sha + '^{commit}').decode().strip()
    if resolved != sha:
        raise ValueError('SHA did not resolve to exact commit')
    return sha


def make_plan(repo, sha, batch_size):
    full_sha(repo, sha)
    if batch_size < 1 or batch_size > 60:
        raise ValueError('batch size must be between 1 and 60')
    tracked = git(repo, 'ls-tree', '-r', '--name-only', sha, '--', MANIFEST_DIR).decode().splitlines()
    paths = sorted(p for p in tracked if re.fullmatch(r'scripts/mutations/spec007-[^/]+\.json', p))
    if not paths:
        raise ValueError('no SPEC-007 mutation manifests')
    manifests, flat, global_keys = [], [], set()
    for path in paths:
        raw = read_blob(repo, sha, path)
        manifest = json.loads(raw.decode('utf-8-sig'))
        cases = manifest['mutations']
        if not isinstance(cases, list) or not cases:
            raise ValueError('empty/invalid mutation manifest: ' + path)
        ids = set()
        for case in cases:
            ident = case['id']
            if not isinstance(ident, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]*', ident) or ident in ids:
                raise ValueError('invalid/duplicate mutation identity in ' + path)
            ids.add(ident)
            safe_relative(case['file'])
            safe_relative(case['project'])
            if not case['project'].endswith('.csproj') or not re.fullmatch(r'net[0-9]+\.[0-9]+', case['framework']):
                raise ValueError('invalid project/framework for ' + ident)
            if not isinstance(case['filter'], str) or not case['filter'] or '\0' in case['filter']:
                raise ValueError('invalid filter for ' + ident)
            if not isinstance(case['old'], str) or not case['old'] or not isinstance(case['new'], str) or case['old'] == case['new']:
                raise ValueError('non-mutation for ' + ident)
            original = read_blob(repo, sha, case['file'])
            if original.count(case['old'].encode()) != 1:
                raise ValueError('exact source replacement does not match once: ' + ident)
            read_blob(repo, sha, case['project'])
            expected = case.get('expected_failures', [])
            if ('expected_failures' in case and not expected) or not isinstance(expected, list) or any(not isinstance(x, str) or not x.strip() for x in expected):
                raise ValueError('invalid expected failures for ' + ident)
            key = path + '::' + ident
            if key in global_keys:
                raise ValueError('duplicate global case key')
            global_keys.add(key)
            flat.append({'manifest': path, 'id': ident, 'case_sha256': digest(canonical(case))})
        manifests.append({'path': path, 'sha256': digest(raw), 'count': len(cases), 'document': manifest})
    batches = []
    for manifest in manifests:
        cases = manifest['document']['mutations']
        for offset in range(0, len(cases), batch_size):
            batch_id = f'batch_{len(batches):02d}'
            selected = cases[offset:offset + batch_size]
            segment = {'id': batch_id, 'manifest': manifest['path'],
                       'ids': [case['id'] for case in selected],
                       'evidence_dir': f'{batch_id}/decoded'}
            batches.append({'id': batch_id, 'count': len(selected), 'segments': [segment]})
    return {'schema_version': SCHEMA, 'source_sha': sha, 'batch_size': batch_size,
            'runner_path': RUNNER, 'runner_sha256': digest(read_blob(repo, sha, RUNNER)),
            'manifest_count': len(manifests), 'case_count': len(flat),
            'inventory_sha256': digest(canonical(flat)), 'inventory': flat,
            'manifests': manifests, 'batches': batches}


def load_file(root, relative):
    safe_relative(relative)
    candidate = root
    for part in PurePosixPath(relative).parts:
        candidate = candidate / part
        if candidate.is_symlink():
            raise ValueError('symlink in evidence path: ' + relative)
    if not candidate.is_file() or not candidate.resolve().is_relative_to(root.resolve()):
        raise ValueError('missing regular evidence file: ' + relative)
    return candidate


def read_json(root, relative):
    return json.loads(load_file(root, relative).read_text(encoding='utf-8-sig'))


def check_equal(actual, expected, label):
    # Source blobs are bytes, not JSON. Preserve strict byte equality while JSON
    # comparisons stay type-sensitive (for example, False must not equal 0).
    if isinstance(actual, bytes) or isinstance(expected, bytes):
        equal = type(actual) is bytes and type(expected) is bytes and actual == expected
    else:
        equal = canonical(actual) == canonical(expected)
    if not equal:
        raise ValueError(label + ' does not match')


def verify_diff(raw, path, original, mutated):
    """Apply one exact UTF-8 unified diff in memory and compare to intended bytes."""
    lines = raw.decode('utf-8').splitlines(keepends=True)
    headers = [f'diff --git a/{path} b/{path}\n', f'--- a/{path}\n', f'+++ b/{path}\n']
    if len(lines) < 5 or lines[0] != headers[0] or not re.fullmatch(r'index [0-9a-f]+\.\.[0-9a-f]+ 100(?:644|755)\n', lines[1]) or lines[2:4] != headers[1:]:
        raise ValueError('applied.diff is not a single regular-file source diff')
    source = original.decode('utf-8').splitlines(keepends=True)
    cursor, i, result, hunks = 0, 4, [], 0
    while i < len(lines):
        match = re.fullmatch(r'@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@[^\r\n]*\r?\n', lines[i])
        if match is None:
            raise ValueError('invalid diff hunk header')
        old_start, old_count, new_start, new_count = (int(match[1]), int(match[2] or '1'), int(match[3]), int(match[4] or '1'))
        target = old_start - 1 if old_count else old_start
        if target < cursor or target > len(source):
            raise ValueError('overlapping/outside diff hunk')
        result.extend(source[cursor:target])
        cursor = target
        if len(result) != (new_start - 1 if new_count else new_start):
            raise ValueError('diff new line offset disagrees')
        i += 1
        old_seen = new_seen = 0
        while i < len(lines) and not lines[i].startswith('@@ '):
            line = lines[i]
            if not line or line[0] not in ' +-':
                raise ValueError('invalid diff content line')
            kind, text = line[0], line[1:]
            i += 1
            if i < len(lines) and lines[i] == '\\ No newline at end of file\n':
                text = text[:-1] if text.endswith('\n') else text
                i += 1
            if kind in ' -':
                if cursor >= len(source) or source[cursor] != text:
                    raise ValueError('diff old/context bytes disagree with pinned source')
                cursor += 1
                old_seen += 1
            if kind in ' +':
                result.append(text)
                new_seen += 1
        if (old_seen, new_seen) != (old_count, new_count):
            raise ValueError('diff hunk counts disagree')
        hunks += 1
    result.extend(source[cursor:])
    if not hunks or ''.join(result).encode('utf-8') != mutated:
        raise ValueError('applied.diff does not produce exact intended mutation')


def validate_phase(root, relative, case, reader):
    proc = read_json(root, relative + '/process.json')
    if proc.get('timed_out') is not False or proc.get('interrupted') is not False or type(proc.get('exit')) is not int:
        raise ValueError('phase process timed out/interrupted or lacks integer exit')
    command = read_json(root, relative + '/command.json')
    if not isinstance(command, list) or len(command) != 14:
        raise ValueError('unexpected test command length')
    check_equal(command[:11], ['dotnet', 'test', case['project'], '--framework', case['framework'],
                              '--filter', case['filter'], '--nologo', '-v', 'minimal', '--results-directory'], 'test command')
    check_equal(command[12:], ['--logger', 'trx;LogFileName=result.trx'], 'TRX logger command')
    case_dir, phase = relative.rsplit('/', 2)[-2:]
    if not isinstance(command[11], str) or not command[11].startswith('/') or not command[11].endswith('/' + case_dir + '/' + phase):
        raise ValueError('test result path is inconsistent with case/phase')
    console = load_file(root, relative + '/console.log').read_text(encoding='utf-8')
    observed = reader(load_file(root, relative + '/result.trx'), proc['exit'], console)
    check_equal(read_json(root, relative + '/counts.json'), observed, 'counts.json versus actual TRX')
    return observed


def reconcile(repo, plan, evidence_root):
    canonical_plan = make_plan(repo, plan['source_sha'], plan['batch_size'])
    check_equal(plan, canonical_plan, 'plan versus pinned source inventory')
    module = types.ModuleType('pinned_mutation_reader')
    exec(compile(read_blob(repo, plan['source_sha'], RUNNER), RUNNER, 'exec'), module.__dict__)
    evidence_root = evidence_root.resolve()
    expected_summary = {s['evidence_dir'] + '/summary.json' for b in plan['batches'] for s in b['segments']}
    observed_summary = {str(p.relative_to(evidence_root)) for p in evidence_root.rglob('summary.json')}
    issues = []
    for extra in sorted(observed_summary - expected_summary):
        issues.append('unexpected batch summary: ' + extra)
    manifests = {m['path']: m['document'] for m in plan['manifests']}
    originals = {}
    rows = []
    seen = []
    for batch in plan['batches']:
        for segment in batch['segments']:
            prefix = segment['evidence_dir']
            document = manifests[segment['manifest']]
            cases_by_id = {c['id']: c for c in document['mutations']}
            chosen = [c for c in document['mutations'] if c['id'] in segment['ids']]
            segment_issues = []
            records = []
            try:
                check_equal(read_json(evidence_root, prefix + '/manifest.json'), document, 'archived manifest')
                summary = read_json(evidence_root, prefix + '/summary.json')
                check_equal(summary.get('ref'), plan['source_sha'], 'batch SHA')
                check_equal(summary.get('verdict'), 'KILLED', 'batch verdict')
                records = summary['records']
                check_equal([r.get('id') for r in records], segment['ids'], 'batch record identities/order')
            except (OSError, ValueError, KeyError, TypeError) as error:
                segment_issues.append(str(error))
            baselines = {}
            for index, case in enumerate(chosen):
                ident = case['id']
                key = segment['manifest'] + '::' + ident
                seen.append(key)
                directory = prefix + f'/{index:03d}-{ident}'
                row = {'manifest': segment['manifest'], 'id': ident, 'batch': batch['id'],
                       'segment': segment['id'], 'framework': case['framework'],
                       'evidence_dir': directory, 'verified': False,
                       'expected_failures': case.get('expected_failures', []), 'errors': list(segment_issues)}
                try:
                    check_equal(read_json(evidence_root, directory + '/case.json'), case, 'case.json')
                    verdict = read_json(evidence_root, directory + '/verdict.json')
                    if index >= len(records):
                        raise ValueError('missing summary record')
                    check_equal(verdict, records[index], 'case verdict versus summary record')
                    check_equal(verdict.get('id'), ident, 'case identity')
                    check_equal(verdict.get('ref'), plan['source_sha'], 'case SHA')
                    check_equal(verdict.get('case'), case, 'embedded exact case')
                    check_equal(verdict.get('verdict'), 'KILLED', 'case verdict')
                    if verdict.get('restored_clean') is not True or verdict.get('final_clean') is not True or verdict.get('interrupted'):
                        raise ValueError('case lacks clean restoration/final green')
                    source = originals.setdefault(case['file'], read_blob(repo, plan['source_sha'], case['file']))
                    original = load_file(evidence_root, directory + '/original.bin').read_bytes()
                    check_equal(original, source, 'saved original bytes versus pinned source')
                    expected_hash = digest(source)
                    check_equal(verdict.get('original_sha256'), expected_hash, 'original source SHA-256')
                    check_equal(verdict.get('restored_sha256'), expected_hash, 'restored source SHA-256')
                    mutated = source.replace(case['old'].encode(), case['new'].encode(), 1)
                    verify_diff(load_file(evidence_root, directory + '/applied.diff').read_bytes(), case['file'], source, mutated)
                    selection = tuple(case[k] for k in ('project', 'framework', 'filter'))
                    if selection not in baselines:
                        baseline = validate_phase(evidence_root, directory + '/baseline', case, module.read_result)
                        baselines[selection] = (baseline, directory + '/baseline')
                    else:
                        if (evidence_root / directory / 'baseline').exists():
                            raise ValueError('unexpected duplicate baseline directory')
                        baseline, _ = baselines[selection]
                    baseline, baseline_ref = baselines[selection]
                    check_equal(verdict.get('baseline'), baseline, 'baseline counts')
                    if baseline['failed'] or baseline['exit']:
                        raise ValueError('baseline did not pass')
                    red = validate_phase(evidence_root, directory + '/red', case, module.read_result)
                    green = validate_phase(evidence_root, directory + '/green', case, module.read_result)
                    check_equal(verdict.get('red'), red, 'red counts')
                    check_equal(verdict.get('green'), green, 'green counts')
                    if red['failed'] < 1 or red['exit'] != 1:
                        raise ValueError('no actual red assertion/exception failures')
                    if green['failed'] or green['exit']:
                        raise ValueError('restored green did not pass')
                    for count in ('total', 'executed', 'passed', 'notExecuted'):
                        check_equal(green[count], baseline[count], 'restored baseline ' + count)
                    for fragment in case.get('expected_failures', []):
                        if not any(fragment in name for name in red['failures']):
                            raise ValueError('intended assertion did not fail: ' + fragment)
                    row.update({'baseline_ref': baseline_ref, 'baseline': baseline, 'red': red, 'green': green,
                                'original_sha256': expected_hash, 'restored_sha256': expected_hash,
                                'observed_failure_names': red['failures'], 'verified': not row['errors']})
                except (OSError, ValueError, KeyError, TypeError, ET.ParseError, module.InvalidRun) as error:
                    row['errors'].append(str(error))
                rows.append(row)
    expected_keys = [x['manifest'] + '::' + x['id'] for x in plan['inventory']]
    if seen != expected_keys or len(seen) != len(set(seen)):
        issues.append('observed evidence population is not the exact ordered inventory')
    verified = sum(row['verified'] for row in rows)
    result = {'schema_version': SCHEMA, 'source_sha': plan['source_sha'],
              'inventory_sha256': plan['inventory_sha256'], 'manifest_count': plan['manifest_count'],
              'expected_cases': plan['case_count'], 'verified_cases': verified,
              'all_killed': not issues and verified == plan['case_count'],
              'issues': issues, 'records': rows,
              'failure_relevance_review': [row['manifest'] + '::' + row['id'] for row in rows if not row['expected_failures']]}
    result['failure_relevance_review_status'] = 'pending' if result['failure_relevance_review'] else 'not-required'
    result['ready_for_package_rehearsal'] = result['all_killed'] and not result['failure_relevance_review']
    return result


def markdown(result):
    status = 'ALL KILLED — evidence reconciled' if result['all_killed'] else 'INCOMPLETE / INVALID — package rehearsal blocked'
    lines = ['# SPEC-007 mutation evidence reconciliation', '', f'Source SHA: `{result["source_sha"]}`', '',
             f'**{status}**: {result["verified_cases"]}/{result["expected_cases"]} cases verified across {result["manifest_count"]} manifests.', '',
             f'Inventory SHA-256: `{result["inventory_sha256"]}`', '',
             f'Failure relevance review: **{result["failure_relevance_review_status"]}** for {len(result["failure_relevance_review"])} cases. Package rehearsal is blocked until all required reviews are completed and retained; a machine all_killed flag alone is insufficient.', '',
             'Per-case commands, TRX, console, original source, applied diff and restoration attestations remain in the evidence directories below. The cached baseline reference is explicit.', '',
             '| Manifest / case | TFM | Verdict | Red failed/total | Restored passed/total | Baseline evidence |',
             '|---|---|---|---:|---:|---|']
    for row in result['records']:
        red, green = row.get('red'), row.get('green')
        lines.append(f'| {Path(row["manifest"]).name} / `{row["id"]}` | {row["framework"]} | {"KILLED" if row["verified"] else "INVALID"} | {str(red["failed"])+"/"+str(red["total"]) if red else "—"} | {str(green["passed"])+"/"+str(green["total"]) if green else "—"} | `{row.get("baseline_ref", "missing")}` |')
    lines += ['', '## Actual failing test names', '', 'Cases marked **relevance review** have no `expected_failures` in the committed manifest. Their failure relevance must be assessed from the names and retained TRX messages; the runner cannot infer the intended assertion.', '']
    for row in result['records']:
        suffix = ' — **relevance review**' if not row['expected_failures'] else ''
        lines += [f'### {Path(row["manifest"]).name} / {row["id"]}{suffix}', '', f'Evidence: `{row["evidence_dir"]}`', '']
        for name in row.get('observed_failure_names', []):
            lines.append('- `' + name.replace('`', '\\`').replace('\n', ' ') + '`')
        for error in row['errors']:
            lines.append('- ERROR: ' + error)
        lines.append('')
    if result['issues']:
        lines += ['## Reconciliation errors', ''] + ['- ' + issue for issue in result['issues']] + ['']
    lines += ['No package publication, deployment, tagging, or release workflow execution is implied by this evidence.', '']
    return '\n'.join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    p = sub.add_parser('plan')
    p.add_argument('--repo', type=Path, required=True)
    p.add_argument('--sha', required=True)
    p.add_argument('--batch-size', type=int, default=20)
    p.add_argument('--output', type=Path, required=True)
    r = sub.add_parser('reconcile')
    r.add_argument('--repo', type=Path, required=True)
    r.add_argument('--plan', type=Path, required=True)
    r.add_argument('--evidence-root', type=Path, required=True)
    r.add_argument('--output', type=Path, required=True, help='new external directory')
    args = parser.parse_args()
    output = require_external(args.output, args.repo)
    if output.exists():
        raise ValueError('output already exists; preserve earlier evidence')
    if args.command == 'plan':
        result = make_plan(args.repo, args.sha, args.batch_size)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(result, indent=2) + '\n')
        print(f'PLAN {result["source_sha"]}: {result["manifest_count"]} manifests, {result["case_count"]} cases, {len(result["batches"])} serial batches -> {output}')
        return 0
    plan = json.loads(args.plan.read_text(encoding='utf-8-sig'))
    result = reconcile(args.repo, plan, args.evidence_root)
    output.mkdir(parents=True)
    (output / 'reconciliation.json').write_text(json.dumps(result, indent=2) + '\n')
    (output / 'per-case-evidence.md').write_text(markdown(result))
    print(f'RECONCILIATION {result["source_sha"]}: {result["verified_cases"]}/{result["expected_cases"]} verified; all_killed={result["all_killed"]} -> {output}')
    return 0 if result['all_killed'] else 2


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError) as error:
        print('campaign-evidence: ERROR: ' + str(error), file=sys.stderr)
        raise SystemExit(2)
