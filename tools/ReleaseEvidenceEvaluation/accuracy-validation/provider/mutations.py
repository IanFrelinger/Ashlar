from pathlib import Path
import subprocess,json,hashlib,xml.etree.ElementTree as ET
ROOT=Path('/repo');OUT=Path('/provider-evidence');REF='76c8be38be32abe88781ffeb7549a969fd77cad2'
FILE='src/Ashlar.Bricks.GitHub/GitHub/RequiredCheckEvaluation.cs'
FILTER='FullyQualifiedName~GitHubProviderAssuranceRegressionTests'
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def git(*args):return subprocess.check_output(['git',*args],cwd=ROOT,text=True)
subprocess.run(['git','reset','--hard','86c6e9e18'],cwd=ROOT,check=True)
subprocess.run(['git','clean','-fd'],cwd=ROOT,check=True)
subprocess.run(['git','fetch','-q','/src-mirror',REF],cwd=ROOT,check=True)
subprocess.run(['git','checkout','-q','--detach',REF],cwd=ROOT,check=True)
assert git('rev-parse','HEAD').strip()==REF
assert git('status','--porcelain')==''
plan=[('provider-binding-ignored','(requirement.AppId is null || requirement.AppId == check.AppId)','true'),
 ('malformed-id-becomes-any-provider','return value is null or int or long;','return true;'),
 ('global-origin-flag-masks-known-context','if (protection.CheckRequirements is { } bindings)','if (protection.CheckRequirements is { } bindings && protection.RequiredCheckOriginsKnown)')]
summary={'commit':REF,'filter':FILTER,'mutations':[]}
(OUT/'mutation-plan.json').write_text(json.dumps(plan,indent=2)+'\n')
def test(name):
 cmd=['dotnet','test','src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj','-f','net10.0','--no-restore','--filter',FILTER,'--logger','trx;LogFileName='+name+'.trx','--results-directory','/provider-evidence','--nologo','-v','minimal']
 (OUT/(name+'-command.json')).write_text(json.dumps(cmd,indent=2)+'\n')
 with (OUT/(name+'.log')).open('w') as stream:r=subprocess.run(cmd,cwd=ROOT,stdout=stream,stderr=subprocess.STDOUT)
 tree=ET.parse(OUT/(name+'.trx'));c={k:int(v) for k,v in tree.find('.//t:Counters',NS).attrib.items()}
 failed=[x.attrib['testName'] for x in tree.findall('.//t:UnitTestResult',NS) if x.attrib.get('outcome')=='Failed']
 return {'exitCode':r.returncode,'counts':c,'failedTests':failed,'log':name+'.log','trx':name+'.trx'}
for ident,old,new in plan:
 path=ROOT/FILE;before=path.read_text();assert before.count(old)==1,ident
 path.write_text(before.replace(old,new))
 patch=git('diff','--',FILE);assert patch
 (OUT/(ident+'.diff')).write_text(patch)
 red=test(ident+'-red')
 assert red['exitCode']!=0 and red['counts']['failed']>0 and red['counts']['executed']>0 and red['counts']['error']==0 and red['counts']['notExecuted']==0,red
 subprocess.run(['git','restore','--',FILE],cwd=ROOT,check=True)
 assert path.read_text()==before
 status=git('status','--porcelain');(OUT/(ident+'-restored-porcelain.txt')).write_text(status);assert status==''
 green=test(ident+'-green');assert green['exitCode']==0 and green['counts']['failed']==0 and green['counts']['passed']==green['counts']['executed']>0 and green['counts']['notExecuted']==0,green
 assert git('status','--porcelain')==''
 summary['mutations'].append({'id':ident,'file':FILE,'patchSha256':hashlib.sha256(patch.encode()).hexdigest(),'red':red,'green':green,'restoredPorcelainEmpty':True})
 (OUT/'mutations-summary.json').write_text(json.dumps(summary,indent=2)+'\n')
 print(ident,'KILLED',red['counts']['failed'],'/',red['counts']['total'],'→',green['counts']['passed'],'/',green['counts']['total'],flush=True)
summary['status']='passed';(OUT/'mutations-summary.json').write_text(json.dumps(summary,indent=2)+'\n')
(OUT/'final-porcelain.txt').write_text(git('status','--porcelain'))
