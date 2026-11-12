#!/usr/bin/env python3
"""Controller-only test runner: execute committed source serially inside devtest."""
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import time
import xml.etree.ElementTree as ET

EVIDENCE = Path('/evidence')
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
PROJECTS = {
    'src/Ashlar.Tests.Kernel/Ashlar.Tests.Kernel.csproj': {'net8.0', 'net10.0'},
    'application/src/Ashlar.Tests.CLI/Ashlar.Tests.CLI.csproj': {'net10.0'},
    'src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj': {'net8.0', 'net10.0'},
}

def git(*args):
    return subprocess.check_output(['git', *args], text=True).strip()

def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + '\n')

def invoke(command, logfile):
    started = time.time_ns()
    with logfile.open('w') as output:
        output.write(json.dumps({'argv': command, 'started_ns': started}) + '\n')
        output.flush()
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        for line in process.stdout:
            print(line, end='', flush=True)
            output.write(line)
        code = process.wait()
    return {'command': command, 'exit_code': code, 'started_ns': started, 'ended_ns': time.time_ns()}

def verify_trx(path, expected_total, expected_failures, exit_code):
    document = ET.parse(path)
    counters = document.find('.//t:Counters', NS)
    if counters is None:
        raise ValueError('TRX has no counters')
    counts = {key: int(counters.get(key, '0')) for key in ('total', 'executed', 'passed', 'failed', 'notExecuted', 'error', 'timeout', 'aborted', 'inconclusive')}
    rows = [{'name': row.get('testName'), 'outcome': row.get('outcome'), 'test_id': row.get('testId'), 'execution_id': row.get('executionId')}
            for row in document.findall('.//t:UnitTestResult', NS)]
    if counts['total'] < 1 or len(rows) != counts['total'] or counts['executed'] != counts['total']:
        raise ValueError(f'No tests, skipped tests, or result/counter mismatch: {counts}')
    if expected_total is not None and counts['total'] != expected_total:
        raise ValueError(f'Expected {expected_total} tests, observed {counts["total"]}')
    if counts['passed'] + counts['failed'] != counts['total'] or any(row['outcome'] not in ('Passed', 'Failed') for row in rows):
        raise ValueError(f'Non-pass/fail outcome: {counts}')
    if any(counts[key] for key in ('notExecuted', 'error', 'timeout', 'aborted', 'inconclusive')):
        raise ValueError(f'Non-test-failure counter: {counts}')
    failures = [row['name'] for row in rows if row['outcome'] == 'Failed']
    if len(failures) != counts['failed']:
        raise ValueError('Failure-row/counter mismatch')
    if bool(failures) != (exit_code != 0) or exit_code not in (0, 1):
        raise ValueError(f'Unexpected test exit/outcome combination: exit={exit_code}, failures={len(failures)}')
    matches = [0] * len(expected_failures)
    for name in failures:
        owners = [index for index, expected in enumerate(expected_failures) if re.fullmatch(expected['name_regex'], name)]
        if len(owners) != 1:
            raise ValueError(f'Undeclared or ambiguous failed test: {name}')
        matches[owners[0]] += 1
    if matches != [expected['count'] for expected in expected_failures]:
        raise ValueError(f'Expected failure multiplicities not met: {matches}')
    return {'counts': counts, 'results': rows, 'trx_sha256': hashlib.sha256(path.read_bytes()).hexdigest()}

def main():
    if not Path('/.dockerenv').exists() or Path.cwd() != Path('/repo') or not Path('/src-mirror/.git').is_dir():
        raise ValueError('Run only through test-in-container.sh in its devtest clone')
    subprocess.run(['mountpoint', '-q', '/evidence'], check=True)
    plan = json.loads((EVIDENCE / 'plan.json').read_text())
    sha = plan['source_sha']
    if plan.get('schema') != 1 or not re.fullmatch('[0-9a-f]{40}', sha) or git('rev-parse', 'HEAD') != sha:
        raise ValueError('Plan/source SHA mismatch')
    if git('status', '--porcelain', '--untracked-files=all'):
        raise ValueError('Dirty initial source')
    (EVIDENCE / 'source-sha.txt').write_text(sha + '\n')
    (EVIDENCE / 'initial-status.txt').write_text(git('status', '--porcelain', '--untracked-files=all'))
    for flag in ('--info', '--list-sdks', '--list-runtimes'):
        with (EVIDENCE / ('dotnet-' + flag.removeprefix('--') + '.txt')).open('w') as output:
            subprocess.run(['dotnet', flag], stdout=output, stderr=subprocess.STDOUT, check=True)
    steps = plan['steps']
    if not isinstance(steps, list) or not steps:
        raise ValueError('A nonempty serial step list is required')
    ids = [step['id'] for step in steps]
    if len(ids) != len(set(ids)) or any(not re.fullmatch('[a-z0-9][a-z0-9.-]*', value) for value in ids):
        raise ValueError('Step IDs must be unique safe filenames')
    summary = {'schema': 1, 'source_sha': sha, 'plan_sha256': hashlib.sha256((EVIDENCE / 'plan.json').read_bytes()).hexdigest(), 'steps': [], 'complete': False}
    write_json(EVIDENCE / 'summary.json', summary)
    for step in steps:
        out = EVIDENCE / step['id']
        out.mkdir()
        record = {'id': step['id'], 'kind': step['kind']}
        summary['steps'].append(record)
        try:
            if step['kind'] == 'tests':
                project, tfm, filter_ = step['project'], step['framework'], step['filter']
                if tfm not in PROJECTS.get(project, set()) or not isinstance(filter_, str) or not filter_:
                    raise ValueError('Unsupported project/framework or empty filter')
                total = step['expected_total']
                failures = step['expected_failures']
                if type(total) is not int or total < 1 or not isinstance(failures, list):
                    raise ValueError('Exact positive total and failure list required')
                for expected in failures:
                    if type(expected['count']) is not int or expected['count'] < 1:
                        raise ValueError('Failure multiplicities must be positive')
                    re.compile(expected['name_regex'])
                command = ['dotnet', 'test', project, '--framework', tfm, '--filter', filter_, '--nologo', '-v', 'minimal', '--logger', 'trx;LogFileName=result.trx', '--results-directory', str(out)]
                record.update(invoke(command, out / 'console.log'))
                record.update(verify_trx(out / 'result.trx', total, failures, record['exit_code']))
            elif step['kind'] == 'cert-gate':
                record.update(invoke(['bash', 'scripts/run-cert-gate.sh'], out / 'console.log'))
                shutil.copy2('/repo/test-results/cert-gate.trx', out / 'result.trx')
                record.update(verify_trx(out / 'result.trx', None, [], record['exit_code']))
            elif step['kind'] == 'mutations':
                manifest = step['manifest']
                if not re.fullmatch(r'scripts/mutations/[a-z0-9-]+\.json', manifest):
                    raise ValueError('Mutation manifest must be a committed scripts/mutations JSON')
                subprocess.run(['git', 'ls-files', '--error-unmatch', '--', manifest], check=True)
                record.update(invoke(['python3', 'scripts/run-spec007-mutation-batch.py', '--manifest', manifest, '--output', str(out / 'evidence')], out / 'console.log'))
                if record['exit_code'] != 0:
                    raise ValueError('Mutation campaign did not pass; inspect raw per-case evidence')
                record['note'] = 'Raw per-case evidence retained; independently reconcile all cases before claiming completion.'
            else:
                raise ValueError('Unsupported step kind')
            if git('rev-parse', 'HEAD') != sha or git('status', '--porcelain', '--untracked-files=all'):
                raise ValueError('Source changed during step')
            record['verified'] = True
        except Exception as error:
            record['verified'] = False
            record['error'] = str(error)
            raise
        finally:
            write_json(EVIDENCE / 'summary.json', summary)
    (EVIDENCE / 'final-status.txt').write_text(git('status', '--porcelain', '--untracked-files=all'))
    summary['complete'] = True
    write_json(EVIDENCE / 'summary.json', summary)
    print('EXACT_SHA_TEST_PLAN=VERIFIED', flush=True)

if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        (EVIDENCE / 'runner-error.txt').write_text(str(error) + '\n')
        raise
