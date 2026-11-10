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
            result = subprocess.run([*cmd, *args], env={**os.environ, 'DOTNET_SYSTEM_IO_DISABLEFILELOCKING':disabled}, capture_output=True, text=True, timeout=45)
            print(json.dumps({'baseline':BASE, 'tfm':tfm,'args':args,'disabled':disabled,'exit':result.returncode,'stdout':result.stdout[:1500],'stderr':result.stderr[:1500]}),flush=True)
            return result
        guard = run(['yaml','guard','mapping','10000'])
        assert guard.returncode == 0 and 'accepted=True' in guard.stdout, guard
        fatal = run(['yaml','manifest','mapping','10000'])
        assert fatal.returncode in (-6,-11,134,139) and 'Stack overflow' in fatal.stderr, fatal
        print(f'REPRODUCED {tfm} #476: guard admitted small deep mapping; manifest process terminated with stack overflow',flush=True)
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
