from pathlib import Path
import hashlib,json,subprocess,sys
from xml.etree import ElementTree as ET

ROOT=Path('/repo'); OUT=Path('/evidence')
plan=json.loads((OUT/'final-plan.json').read_text())
rows=[]
summary={'schemaVersion':1,'status':'running','candidateCommit':plan['candidateCommit'],'mutations':rows}

def write_summary():
 (OUT/'final-summary.json').write_text(json.dumps(summary,indent=2)+'\n')
def run(args,log):
 with log.open('w') as stream:
  result=subprocess.run(args,cwd=ROOT,stdout=stream,stderr=subprocess.STDOUT)
 return result.returncode

def git(*args):return subprocess.check_output(['git',*args],cwd=ROOT,text=True)
def clean(label):
 text=git('status','--porcelain')
 (OUT/(label+'-porcelain.txt')).write_text(text)
 if text:raise RuntimeError('Restoration was not clean: '+text)

def tests(selection,label,no_build=False,framework='net10.0'):
 log=OUT/(label+'.log');trx=OUT/(label+'.trx')
 if trx.exists():raise RuntimeError('Refusing stale test artifact')
 args=['dotnet','test','src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj','--framework',framework,
       '--filter',selection,'--no-restore','--nologo','-v','minimal','-m:2','-p:NuGetAudit=false',
       '--logger','trx;LogFileName='+trx.name,'--results-directory',str(OUT)]
 if no_build:args+=['--no-build']
 (OUT/(label+'-command.json')).write_text(json.dumps(args)+'\n')
 code=run(args,log)
 guard_code=run(['bash','scripts/ci/zero-test-guard.sh',str(trx)],OUT/(label+'-guard.log'))
 if guard_code:raise RuntimeError('Test guard failed for '+label)
 ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
 doc=ET.parse(trx).getroot(); counters=doc.find('t:ResultSummary/t:Counters',ns)
 if counters is None:raise RuntimeError('No TRX counters')
 counts={k:int(v) for k,v in counters.attrib.items()}
 failed=[node.attrib.get('testName') for node in doc.findall('t:Results/t:UnitTestResult',ns) if node.attrib.get('outcome')=='Failed']
 return {'exitCode':code,'counts':counts,'failedTests':failed,'log':log.name,'trx':trx.name}

def mutate(m,ref):
 path=ROOT/m['path'];original=path.read_text()
 if original.count(m['old'])!=1:raise RuntimeError('Mutation must apply exactly once: '+m['id'])
 result={'id':m['id'],'candidateCommit':ref,'path':m['path'],'filter':m['filter']}
 try:
  path.write_text(original.replace(m['old'],m['new']))
  patch=git('diff','--',m['path'])
  if not patch:raise RuntimeError('Mutation produced no diff')
  (OUT/(m['id']+'-applied.diff')).write_text(patch)
  result['patchSha256']=hashlib.sha256(patch.encode()).hexdigest()
  red=tests(m['filter'],m['id']+'-red',m.get('noBuild',False))
  result['red']=red
  if red['exitCode']==0 or red['counts'].get('failed',0)<1 or red['counts'].get('executed',0)<1 or red['counts'].get('notExecuted',0)!=0:
   raise RuntimeError('Not an assertion mutation kill: '+m['id'])
 finally:
  subprocess.run(['git','checkout','--',m['path']],cwd=ROOT,check=True)
 clean(m['id']+'-restored')
 green=tests(m['filter'],m['id']+'-green',m.get('noBuild',False))
 result['green']=green
 if green['exitCode'] or green['counts'].get('failed',0) or green['counts'].get('executed',0)<1 or green['counts'].get('notExecuted',0):
  raise RuntimeError('Restored run not green: '+m['id'])
 clean(m['id']+'-after-green')
 result['verdict']='KILLED'
 rows.append(result);write_summary()
 print(f"{m['id']}: KILLED red={red['counts']['failed']}/{red['counts']['total']} green={green['counts']['passed']}/{green['counts']['total']}",flush=True)

try:
 if git('rev-parse','HEAD').strip()!=plan['candidateCommit']:raise RuntimeError('Wrong committed clone')
 clean('final-initial')
 if run(['dotnet','restore','src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj','-p:NuGetAudit=false'],OUT/'final-restore.log'):
  raise RuntimeError('Offline cached restore failed')
 for m in plan['mutations']:mutate(m,plan['candidateCommit'])
 final=plan['finalCommit']
 subprocess.run(['git','fetch','-q','/src-mirror',final],cwd=ROOT,check=True)
 subprocess.run(['git','checkout','-q','--detach',final],cwd=ROOT,check=True)
 clean('final-validation-commit')
 preflight=tests('FullyQualifiedName~EgressGuardConventionTests.F1_no_file_has_more_outbound_paths_than_its_pin','final-F1-preflight')
 summary['preflight']=preflight;write_summary()
 if preflight['exitCode'] or preflight['counts'].get('failed',0):raise RuntimeError('F1 preflight failed')
 gate=run(['env','NuGetAudit=false','bash','scripts/run-cert-gate.sh'],OUT/'final-5b-net8-cert-gate.log')
 gate_trx=ROOT/'test-results/cert-gate.trx'
 if gate_trx.exists():(OUT/'final-5b-net8-cert-gate.trx').write_bytes(gate_trx.read_bytes())
 summary['finalCertGate']={'candidateCommit':final,'exitCode':gate,'log':'final-5b-net8-cert-gate.log','trx':'final-5b-net8-cert-gate.trx'}
 write_summary()
 if gate:raise RuntimeError('Final NET8 required gate failed')
 path='src/Ashlar.Bricks.GitHub/GitHub/BlockerAnalysis.cs'
 m={'id':'partial-ci-wrongly-proves-absence','path':path,
    'old':'ci.ChecksReadComplete && ci.ChecksReported == ci.Checks.Count && ci.StatusesReported == ci.Statuses.Count',
    'new':'ci.ChecksReadComplete || !ci.ChecksReadComplete',
    'filter':'FullyQualifiedName~GitHubEvaluationRegressionTests'}
 mutate(m,final)
 finalfilter='FullyQualifiedName~GitHubBlockersBrickTests|FullyQualifiedName~GitHubReadAccessTests|FullyQualifiedName~GitHubEvaluationRegressionTests|FullyQualifiedName~GitHubProviderAssuranceRegressionTests'
 finalgreen=tests(finalfilter,'final-github-177-green')
 summary['finalFocusedGreen']={'candidateCommit':final,**finalgreen}
 if finalgreen['exitCode'] or finalgreen['counts'].get('failed',0) or finalgreen['counts'].get('notExecuted',0):
  raise RuntimeError('Final focused filter failed')
 clean('final-finish')
 summary['producerCommit']=plan['producerCommit'];summary['finalValidationCommit']=plan['finalCommit'];summary['status']='passed';write_summary()
except Exception as error:
 summary['status']='failed';summary['error']=str(error);write_summary()
 print('Mutation sequence failed: '+str(error),file=sys.stderr,flush=True)
 raise
