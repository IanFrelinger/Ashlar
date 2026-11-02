import os,json,time,subprocess,datetime,pathlib,platform,hashlib
r=pathlib.Path('/measurement'); packages=r/'cold-nuget';assert not packages.exists();packages.mkdir()
env=dict(os.environ,NUGET_PACKAGES=str(packages),DOTNET_CLI_TELEMETRY_OPTOUT='1')
env.update(ASHLAR_PR_BLOCKERS_URL='https://github.com/IanFrelinger/Ashlar/pull/900',ASHLAR_EVIDENCE_DIRECTORY=str(r/'artifacts'),Ashlar__Audit__File__Directory=str(r/'audit'),GITHUB_OUTPUT=str(r/'action-outputs'),GITHUB_STEP_SUMMARY=str(r/'action-summary.md'))
env.pop('ASHLAR_GITHUB_TOKEN',None);env.pop('ASHLAR_GITHUB_REPOSITORY',None)
m={'schemaVersion':1,'candidateCommit':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'startedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'sdk':subprocess.check_output(['dotnet','--version'],text=True).strip(),'platform':platform.platform(),'cache':'new empty app package/HTTP cache; preinstalled SDK and cached Docker image; remote cache uncontrolled','authorization':'no token supplied by code; inherited proxy upstream identity unknown','case':'timing-only fresh public PR900 recollection; excluded from frozen accuracy corpus','commands':[]}
start=time.monotonic()
commands=[('restore',['dotnet','restore','src/Ashlar.Mcp.Server.Host/Ashlar.Mcp.Server.Host.csproj','-p:NuGetAudit=false','--nologo','-v','minimal']),('build',['dotnet','build','src/Ashlar.Mcp.Server.Host/Ashlar.Mcp.Server.Host.csproj','-c','Release','--no-restore','-p:NuGetAudit=false','--nologo','-v','minimal']),('collect',[ 'python3','actions/pr-blockers/pr_blockers.py','--','dotnet','src/Ashlar.Mcp.Server.Host/bin/Release/net10.0/Ashlar.Mcp.Server.Host.dll'])]
for name,cmd in commands:
    tick=time.monotonic()
    with (r/(name+'.log')).open('w') as log:result=subprocess.run(cmd,env=env,stdout=log,stderr=subprocess.STDOUT)
    m['commands'].append({'name':name,'arguments':cmd,'exitCode':result.returncode,'seconds':time.monotonic()-tick})
    (r/'measurement.json').write_text(json.dumps(m,indent=2)+'\n')
    if result.returncode:break
m['insideContainerSeconds']=time.monotonic()-start;m['completedAt']=datetime.datetime.now(datetime.timezone.utc).isoformat()
if (r/'action-outputs').exists():
    outputs=dict(line.split('=',1) for line in (r/'action-outputs').read_text().splitlines())
    if 'evidence-json' in outputs:
        p=pathlib.Path(outputs['evidence-json']); pack=json.loads(p.read_text())
        m['pack']={'path':str(p.relative_to(r)),'markdownPath':str(pathlib.Path(outputs['evidence-markdown']).relative_to(r)),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'headSha':pack['headSha'],'verdict':pack['verdict'],'assurance':pack['assurance'],'collectionSeconds':(datetime.datetime.fromisoformat(pack['collectedAt'])-datetime.datetime.fromisoformat(pack['collectionStartedAt'])).total_seconds()}
(r/'measurement.json').write_text(json.dumps(m,indent=2)+'\n');print(json.dumps(m));raise SystemExit(m['commands'][-1]['exitCode'])
