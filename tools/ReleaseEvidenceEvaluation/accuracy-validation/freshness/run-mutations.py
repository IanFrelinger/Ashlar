from pathlib import Path
import subprocess,json,hashlib,xml.etree.ElementTree as ET,os
ROOT=Path('/repo');OUT=Path('/evidence');PLAN=json.loads((OUT/'mutation-plan.json').read_text());SUMMARY={'schemaVersion':1,'commit':PLAN['commit'],'mutations':[],'status':'running'}
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def save(): (OUT/'mutation-summary.json').write_text(json.dumps(SUMMARY,indent=2)+'\n')
def git(*args): return subprocess.check_output(['git',*args],cwd=ROOT)
def test(identifier,phase,filter):
 trx=identifier+'-'+phase+'.trx';log=identifier+'-'+phase+'.log'
 args=['dotnet','test','src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj','-f','net10.0','--filter',filter,'--nologo','-v','minimal','--logger','trx;LogFileName='+trx,'--results-directory',str(OUT)]
 (OUT/(identifier+'-'+phase+'-command.json')).write_text(json.dumps({'arguments':args,'cwd':str(ROOT),'environmentOverrides':{'NuGetAudit':'false'}},indent=2)+'\n')
 with (OUT/log).open('wb') as f: result=subprocess.run(args,cwd=ROOT,stdout=f,stderr=subprocess.STDOUT,env={**os.environ,'NuGetAudit':'false'})
 if not (OUT/trx).is_file(): raise RuntimeError('No real test TRX: '+log)
 tree=ET.fromstring((OUT/trx).read_bytes());counter={k:int(v) for k,v in tree.find('.//t:Counters',NS).attrib.items()}
 failed=[x.attrib['testName'] for x in tree.findall('.//t:UnitTestResult',NS) if x.attrib['outcome']=='Failed']
 assert counter['executed']>0 and counter['notExecuted']==0 and counter['error']==0,(identifier,phase,counter)
 return {'exitCode':result.returncode,'counts':counter,'failedTests':failed,'log':log,'trx':trx}
assert git('rev-parse','HEAD').decode().strip()==PLAN['commit']
assert git('status','--porcelain')==b''
(OUT/'initial-porcelain.txt').write_bytes(git('status','--porcelain'));save()
for item in PLAN['mutations']:
 path=ROOT/item['path'];original=path.read_bytes();body=original.decode()
 for edit in item['edits']:
  assert body.count(edit['old'])==1,(item['id'],edit['old'])
  body=body.replace(edit['old'],edit['new'],1)
 assert body.encode()!=original
 path.write_text(body)
 patch=git('diff','--binary','--',item['path']);assert patch
 (OUT/(item['id']+'-applied.diff')).write_bytes(patch)
 red=test(item['id'],'red',item['filter'])
 assert red['exitCode']!=0 and red['counts']['failed']>0,(item['id'],'survived',red)
 subprocess.run(['git','restore','--',item['path']],cwd=ROOT,check=True)
 assert path.read_bytes()==original
 porcelain=git('status','--porcelain');(OUT/(item['id']+'-restored-porcelain.txt')).write_bytes(porcelain);assert porcelain==b''
 green=test(item['id'],'green',item['filter'])
 assert green['exitCode']==0 and green['counts']['failed']==0 and green['counts']['passed']==green['counts']['total'],(item['id'],'bad-green',green)
 porcelain=git('status','--porcelain');(OUT/(item['id']+'-after-green-porcelain.txt')).write_bytes(porcelain);assert porcelain==b''
 SUMMARY['mutations'].append({'id':item['id'],'path':item['path'],'filter':item['filter'],'patchSha256':hashlib.sha256(patch).hexdigest(),'originalSourceSha256':hashlib.sha256(original).hexdigest(),'restoredSourceSha256':hashlib.sha256(path.read_bytes()).hexdigest(),'red':red,'green':green,'verdict':'KILLED'})
 save();print(item['id']+': KILLED red='+str(red['counts']['failed'])+'/'+str(red['counts']['total'])+' green='+str(green['counts']['passed'])+'/'+str(green['counts']['total']),flush=True)
SUMMARY['status']='passed';save()
