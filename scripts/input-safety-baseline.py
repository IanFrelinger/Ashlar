#!/usr/bin/env python3
"""One-time historical vulnerability reproduction; run only in devtest container."""
import fcntl
import json
import os
from pathlib import Path
import resource
import shutil
import subprocess
import tempfile

BASE = '35d63c0e447c73762f05217a96a4fcb95a24632b'
ROOT = Path.cwd()
resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
with tempfile.TemporaryDirectory(prefix='input-safety-baseline-') as tmp:
    baseline = Path(tmp) / 'source'
    subprocess.run(['git', 'clone', '--shared', '--no-checkout', str(ROOT), str(baseline)], check=True)
    subprocess.run(['git', 'checkout', BASE], cwd=baseline, check=True)
    shutil.copytree(ROOT / 'tools/InputSafetyProbe', baseline / 'tools/InputSafetyProbe', ignore=shutil.ignore_patterns('bin', 'obj'))
    for tfm in ('net8.0', 'net10.0'):
        subprocess.run(['dotnet', 'build', 'tools/InputSafetyProbe/InputSafetyProbe.csproj', '--framework', tfm, '--nologo', '-v', 'minimal'], cwd=baseline, check=True, timeout=300)
        cmd = ['dotnet', str(baseline / f'tools/InputSafetyProbe/bin/Debug/{tfm}/InputSafetyProbe.dll')]
        def run(args, disabled='0'):
            result = subprocess.run([*cmd, *args], env={**os.environ, 'DOTNET_SYSTEM_IO_DISABLEFILELOCKING':disabled}, capture_output=True, text=True, timeout=90)
            print(json.dumps({'baseline':BASE, 'tfm':tfm,'args':args,'disabled':disabled,'exit':result.returncode,'stdout':result.stdout[:1500],'stderr':result.stderr[:1500]}),flush=True)
            return result
        guard = run(['yaml','guard','mapping','10000'])
        assert guard.returncode == 0 and 'accepted=True' in guard.stdout, guard
        crashed = False
        for depth in ('30000', '100000'):
            fatal = run(['yaml','manifest','mapping',depth])
            if fatal.returncode in (-6,-11,134,139) and 'Stack overflow' in fatal.stderr:
                crashed = True
                print(f'REPRODUCED {tfm} #476: {depth} mapping containers caused a process-fatal stack overflow below the 1 MB cap',flush=True)
                break
        assert crashed, 'No process-fatal depth found in the bounded baseline panel'
        for store,folder in (('gate','gates'),('ledger','ledger')):
            state=Path(tmp)/f'{tfm}-{store}'; (state/folder).mkdir(parents=True)
            with (state/folder/'.lock').open('w') as held:
                fcntl.flock(held,fcntl.LOCK_EX|fcntl.LOCK_NB)
                blocked=run([store,str(state),'blocked'])
                assert blocked.returncode == 24 and 'contended' in blocked.stdout,blocked
                bypass=run([store,str(state),'write'],'1')
                assert bypass.returncode == 0 and 'written' in bypass.stdout,bypass
                assert len(list(state.glob('**/*.json'))) == 1
            print(f'REPRODUCED {tfm} #480 {store}: normal child blocked; disabled child wrote while independent process held lock',flush=True)
print('PASS historical baseline reproductions, both real target runtimes',flush=True)
