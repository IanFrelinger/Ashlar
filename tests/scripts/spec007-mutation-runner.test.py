"""Host-only Python fixtures. Fake test processes; never launch dotnet or Docker."""
import base64
import contextlib
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch, MagicMock
import xml.etree.ElementTree as ET

SCRIPTS = Path(__file__).resolve().parents[2] / 'scripts'
def load(name, file):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module
runner = load('runner', 'run-spec007-mutation-batch.py')
decoder = load('decoder', 'decode-spec007-mutation-evidence.py')

def trx(outcomes=('Passed','Failed','NotExecuted')):
    root = ET.Element('TestRun', xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    results = ET.SubElement(root, 'Results')
    for i, outcome in enumerate(outcomes):
        item = ET.SubElement(results, 'UnitTestResult', outcome=outcome, testName=f'Test{i}', testId=f'test-{i}', executionId=f'execution-{i}')
        if outcome == 'Failed':
            ET.SubElement(ET.SubElement(ET.SubElement(item,'Output'),'ErrorInfo'),'Message').text='Expected one send, but observed two.'
    summary = ET.SubElement(root, 'ResultSummary', outcome='Failed' if 'Failed' in outcomes else 'Completed')
    ET.SubElement(summary, 'Counters', total=str(len(outcomes)), executed=str(sum(x!='NotExecuted' for x in outcomes)), passed=str(outcomes.count('Passed')), failed=str(outcomes.count('Failed')), notExecuted=str(outcomes.count('NotExecuted')), error='0', timeout='0', aborted='0', inProgress='0', completed='0')
    return root

class ResultFixtures(unittest.TestCase):
    def check(self, root, code=1, console=''):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'result.trx'; ET.ElementTree(root).write(path)
            return runner.read_result(path,code,console)
    def test_valid_mixed_run(self):
        result=self.check(trx()); self.assertEqual(result['failures'],['Test1']); self.assertEqual(result['total'],3)
    def test_valid_green(self):
        self.assertEqual(self.check(trx(('Passed',)),0)['passed'],1)
    def test_total_mismatch(self):
        root=trx(); root.find('ResultSummary/Counters').set('total','4')
        with self.assertRaises(runner.InvalidRun): self.check(root)
    def test_missing_individual_skip(self):
        root=trx(); root.find('Results').remove(root.find('Results')[-1])
        with self.assertRaises(runner.InvalidRun): self.check(root)
    def test_individual_outcome_disagrees(self):
        root=trx(); root.find('Results')[0].set('outcome','Failed')
        with self.assertRaises(runner.InvalidRun): self.check(root)
    def test_duplicate_execution(self):
        root=trx(); root.find('Results')[1].set('executionId','execution-0')
        with self.assertRaises(runner.InvalidRun): self.check(root)
    def test_missing_test_identity(self):
        root=trx(); root.find('Results')[1].set('testName','')
        with self.assertRaises(runner.InvalidRun): self.check(root)
    def test_missing_failure_message(self):
        root=trx(); root.find('Results')[1].remove(root.find('Results')[1].find('Output'))
        with self.assertRaises(runner.InvalidRun): self.check(root)
    def test_aborted_summary(self):
        root=trx(); root.find('ResultSummary').set('outcome','Aborted')
        with self.assertRaises(runner.InvalidRun): self.check(root)
    def test_aborted_counter(self):
        for counter in ('error','timeout','aborted','inProgress','pending','passedButRunAborted','warning'):
            with self.subTest(counter=counter):
                root=trx(); root.find('ResultSummary/Counters').set(counter,'1')
                with self.assertRaises(runner.InvalidRun): self.check(root)
    def test_run_level_error(self):
        root=trx(); ET.SubElement(ET.SubElement(root.find('ResultSummary'),'RunInfos'),'RunInfo',outcome='Error')
        with self.assertRaises(runner.InvalidRun): self.check(root)
    def test_incomplete_individual_result(self):
        root=trx(); root.find('Results')[1].set('outcome','InProgress')
        with self.assertRaises(runner.InvalidRun): self.check(root)
    def test_no_tests_or_only_skips(self):
        for outcomes in ((),('NotExecuted',)):
            with self.assertRaises(runner.InvalidRun): self.check(trx(outcomes),0)
    def test_exit_mismatch_and_abnormal_exit(self):
        for outcomes,code in [(('Passed',),1),(('Failed',),0),(('Failed',),137),(('Failed',),-9)]:
            with self.assertRaises(runner.InvalidRun): self.check(trx(outcomes),code)
    def test_build_error_even_with_trx(self):
        for text in ['/repo/F.cs(1,2): error CS1001: expected name', '/repo/F.cs(1,2): error CA1859: wrong type', '/repo/X.csproj : error MSB1001: build failed']:
            with self.assertRaises(runner.InvalidRun): self.check(trx(),1,text)
    def test_negative_or_missing_counter(self):
        for value in ('-1','x'):
            root=trx(); root.find('ResultSummary/Counters').set('passed',value)
            with self.assertRaises(runner.InvalidRun): self.check(root)
        root=trx(); del root.find('ResultSummary/Counters').attrib['notExecuted']
        with self.assertRaises(runner.InvalidRun): self.check(root)

GREEN=dict(total=2,executed=2,passed=2,failed=0,notExecuted=0,failures=[],exit=0)
RED=dict(total=2,executed=2,passed=1,failed=1,notExecuted=0,failures=['Tests.Intended'],exit=1)

class GitFixtures(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory(); self.base=Path(self.tmp.name); self.repo=self.base/'repo'; self.repo.mkdir()
        self.source=self.repo/'source.cs'; self.original=b'// original\r\nvalue = true;\r\n'
        self.source.write_bytes(self.original); (self.repo/'tests.csproj').write_text('<Project/>')
        self.git('init','-q'); self.git('config','user.email','fixture@example.test'); self.git('config','user.name','Fixture'); self.git('config','core.autocrlf','false'); self.git('add','.'); self.git('commit','-qm','fixture')
        self.case=dict(id='fixture',file='source.cs',old='value = true;',new='value = false;',project='tests.csproj',framework='net8.0',filter='FullyQualifiedName~Intended',expected_failures=['Intended'])
    def tearDown(self): self.tmp.cleanup()
    def git(self,*args): return subprocess.check_output(['git',*args],cwd=self.repo,stderr=subprocess.STDOUT,text=True)
    def run_case(self,phases,case=None):
        def fake(repo,case,directory,phase,timeout):
            result=phases[phase]
            if isinstance(result,BaseException): raise result
            if callable(result): return result()
            return copy.deepcopy(result)
        with contextlib.redirect_stdout(io.StringIO()),patch.object(runner,'run_tests',side_effect=fake):
            return runner.run_mutation(self.repo,self.git('rev-parse','HEAD').strip(),case or self.case,self.base/'evidence',{})
    def assert_restored(self):
        self.assertEqual(self.source.read_bytes(),self.original); self.assertEqual(self.git('status','--porcelain'),'')
    def test_kill_restores_bytes_and_saves_evidence(self):
        result=self.run_case(dict(baseline=GREEN,red=RED,green=GREEN)); self.assertEqual(result['verdict'],'KILLED'); self.assert_restored()
        self.assertEqual(result['original_sha256'],result['restored_sha256']); self.assertTrue((self.base/'evidence/applied.diff').is_file()); self.assertTrue((self.base/'evidence/verdict.json').is_file())
    def test_survivor_still_restores_and_runs_green(self):
        result=self.run_case(dict(baseline=GREEN,red=GREEN,green=GREEN)); self.assertEqual(result['verdict'],'SURVIVED'); self.assertTrue(result['final_clean']); self.assert_restored()
    def test_compile_failure_restores_and_keeps_invalid_verdict(self):
        result=self.run_case(dict(baseline=GREEN,red=runner.InvalidRun('no TRX'),green=GREEN)); self.assertEqual(result['verdict'],'INVALID'); self.assertTrue(result['final_clean']); self.assert_restored()
    def test_unrelated_assertion_is_invalid(self):
        red={**RED,'failures':['Tests.Unrelated']}; result=self.run_case(dict(baseline=GREEN,red=red,green=GREEN)); self.assertEqual(result['verdict'],'INVALID'); self.assert_restored()
    def test_every_expected_fragment_required(self):
        case={**self.case,'expected_failures':['Intended','Other']}; result=self.run_case(dict(baseline=GREEN,red=RED,green=GREEN),case); self.assertEqual(result['verdict'],'INVALID')
    def test_green_changed_discovery_is_invalid(self):
        changed={**GREEN,'total':3,'executed':3,'passed':3}; result=self.run_case(dict(baseline=GREEN,red=RED,green=changed)); self.assertEqual(result['verdict'],'INVALID'); self.assert_restored()
    def test_dirty_baseline_is_invalid_before_apply(self):
        def dirty(): self.source.write_text('dirty'); return GREEN
        result=self.run_case(dict(baseline=dirty)); self.assertEqual(result['verdict'],'INVALID'); self.assertNotIn('red',result)
    def test_interruption_restores_and_does_not_continue_tests(self):
        result=self.run_case(dict(baseline=GREEN,red=KeyboardInterrupt())); self.assertEqual(result['verdict'],'INVALID'); self.assertTrue(result['interrupted']); self.assert_restored()
    def test_restore_error_never_claims_kill_and_verdict_persists(self):
        with patch.object(runner,'restore_source',side_effect=OSError('fixture disk fault')):
            result=self.run_case(dict(baseline=GREEN,red=RED))
        self.assertEqual(result['verdict'],'INVALID'); self.assertNotIn('restored_clean',result); self.assertTrue((self.base/'evidence/verdict.json').exists())
    def test_parent_absolute_windows_and_git_paths_rejected(self):
        for relative in ('../outside','/tmp/file','C:/Windows/file','C:relative','a\\b','.git/config','a/../source.cs','./source.cs','source.cs/'):
            with self.subTest(relative=relative),self.assertRaises((ValueError,subprocess.CalledProcessError)):
                runner.source_path(self.repo,relative)
    def test_untracked_source_rejected(self):
        (self.repo/'untracked.cs').write_text('x')
        with self.assertRaises(subprocess.CalledProcessError): runner.source_path(self.repo,'untracked.cs')
    def test_expected_failure_validation(self):
        for expected in ('Intended',[''],[4]):
            with self.assertRaises(ValueError): runner.validate_cases(self.repo,{'mutations':[{**self.case,'expected_failures':expected}]},None)
    def test_missing_or_duplicate_replacement_invalid(self):
        case={**self.case,'old':'absent'}; result=self.run_case({},case); self.assertEqual(result['verdict'],'INVALID'); self.assert_restored()
    def test_main_emits_archive_on_invalid_baseline(self):
        manifest=self.base/'manifest.json'; manifest.write_text(json.dumps({'mutations':[self.case]}))
        previous=Path.cwd(); actual_is_file=Path.is_file; captured=io.StringIO()
        try:
            os.chdir(self.repo)
            with patch('sys.argv',['runner','--manifest',str(manifest),'--output',str(self.base/'batch'),'--archive-stdout']),patch.object(Path,'is_file',lambda p: p.name=='.dockerenv' or actual_is_file(p)),patch.object(runner.signal,'signal'),patch.object(runner,'run_tests',side_effect=runner.InvalidRun('no TRX')),contextlib.redirect_stdout(captured):
                self.assertEqual(runner.main(),2)
        finally: os.chdir(previous)
        output=decoder.decode(captured.getvalue(),self.base/'decoded',self.repo)
        self.assertEqual(json.loads((output/'summary.json').read_text())['verdict'],'INVALID')
        self.assertTrue((output/'000-fixture/verdict.json').exists())
    def test_no_trx_preserves_command_console_and_exit(self):
        process=MagicMock(returncode=1); process.communicate.return_value=('build failed',None)
        with patch.object(runner,'source_path',return_value=self.repo/'tests.csproj'),patch.object(runner.subprocess,'Popen',return_value=process),contextlib.redirect_stdout(io.StringIO()),self.assertRaises(runner.InvalidRun):
            runner.run_tests(self.repo,self.case,self.base/'logs','red')
        self.assertEqual((self.base/'logs/red/console.log').read_text(),'build failed')
        self.assertEqual(json.loads((self.base/'logs/red/process.json').read_text())['exit'],1)
    def test_timeout_preserves_logs_and_kills_process(self):
        process=MagicMock(returncode=-9); process.communicate.side_effect=[subprocess.TimeoutExpired('dotnet',1),('timed out',None)]
        with patch.object(runner,'source_path',return_value=self.repo/'tests.csproj'),patch.object(runner.subprocess,'Popen',return_value=process),patch.object(runner.os,'killpg',create=True) as kill_group,contextlib.redirect_stdout(io.StringIO()),self.assertRaises(runner.InvalidRun):
            runner.run_tests(self.repo,self.case,self.base/'logs','red',1)
        self.assertTrue(json.loads((self.base/'logs/red/process.json').read_text())['timed_out'])
        self.assertTrue(kill_group.called if os.name=='posix' else process.kill.called)
    def test_compiler_failure_with_trx_is_not_killed(self):
        def process_for(command,**kwargs):
            results=Path(command[command.index('--results-directory')+1]); ET.ElementTree(trx()).write(results/'result.trx')
            process=MagicMock(returncode=1); process.communicate.return_value=('/repo/F.cs(1,2): error CS1001: missing name',None); return process
        with patch.object(runner,'source_path',return_value=self.repo/'tests.csproj'),patch.object(runner.subprocess,'Popen',side_effect=process_for),contextlib.redirect_stdout(io.StringIO()),self.assertRaises(runner.InvalidRun):
            runner.run_tests(self.repo,self.case,self.base/'logs','red')
        self.assertTrue((self.base/'logs/red/result.trx').is_file())

def packaged(entries):
    buffer=io.BytesIO()
    with tarfile.open(fileobj=buffer,mode='w:gz') as archive:
        for name,value in entries:
            entry=tarfile.TarInfo(name)
            if isinstance(value,bytes): entry.size=len(value); archive.addfile(entry,io.BytesIO(value))
            else: entry.type=value[0]; entry.linkname='../outside'; archive.addfile(entry)
    payload=buffer.getvalue()
    return f'BEGIN ASHLAR_MUTATION_EVIDENCE sha256={hashlib.sha256(payload).hexdigest()} bytes={len(payload)}\n{base64.b64encode(payload).decode()}\nEND ASHLAR_MUTATION_EVIDENCE\n'

class ArchiveFixtures(unittest.TestCase):
    def extract(self,text):
        with tempfile.TemporaryDirectory() as tmp:
            base=Path(tmp); repo=base/'repo'; repo.mkdir(); out=base/'output'
            decoder.decode(text,out,repo); return (out/'summary.json').read_text()
    def test_complete_archive_roundtrip_from_runner(self):
        with tempfile.TemporaryDirectory() as tmp:
            Path(tmp,'summary.json').write_text('{"verdict":"INVALID"}'); output=io.StringIO()
            with contextlib.redirect_stdout(output): runner.emit_archive(Path(tmp))
            self.assertEqual(json.loads(self.extract(output.getvalue()))['verdict'],'INVALID')
    def test_reject_truncated_duplicate_or_modified_archive(self):
        text=packaged([('evidence/summary.json',b'{}')])
        for invalid in (text[:-15],text+text,text.replace('bytes=','bytes=9',1)):
            with self.assertRaises(ValueError): self.extract(invalid)
    def test_reject_traversal_absolute_links_devices_and_windows_names(self):
        for name,value in [('evidence/../outside',b'x'),('/evidence/file',b'x'),('evidence/link',(tarfile.SYMTYPE,)),('evidence/hard',(tarfile.LNKTYPE,)),('evidence/device',(tarfile.CHRTYPE,)),('evidence/C:ads',b'x'),('evidence/CON.txt',b'x'),('evidence/a\\b',b'x')]:
            with self.subTest(name=name),self.assertRaises(ValueError): self.extract(packaged([('evidence/summary.json',b'{}'),(name,value)]))
    def test_reject_case_collision_and_missing_summary(self):
        for entries in [[('evidence/summary.json',b'{}'),('evidence/SUMMARY.json',b'{}')],[('evidence/log.txt',b'x')]]:
            with self.assertRaises(ValueError): self.extract(packaged(entries))
    def test_reject_existing_or_inside_repository_output(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo=Path(tmp); text=packaged([('evidence/summary.json',b'{}')])
            with self.assertRaises(ValueError): decoder.decode(text,repo/'nested',repo)
            with self.assertRaises(ValueError): decoder.decode(text,repo,repo)
    def test_reject_expansion_limit(self):
        with patch.object(decoder,'MAX_EXPANDED',2),self.assertRaises(ValueError): self.extract(packaged([('evidence/summary.json',b'123')]))

if __name__=='__main__': unittest.main(verbosity=2)
