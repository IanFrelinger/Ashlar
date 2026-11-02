import os,json,time,subprocess,datetime,pathlib,platform
root=pathlib.Path('/measurement'); packages=root/'cold-nuget'
assert not packages.exists(), 'cold profile must not exist'
packages.mkdir()
env=dict(os.environ,NUGET_PACKAGES=str(packages),DOTNET_CLI_TELEMETRY_OPTOUT='1')
record={'schemaVersion':1,'candidateCommit':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'startedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'cache':'new empty NUGET_PACKAGES; new container HTTP cache; preinstalled SDK and cached Docker image','sdk':subprocess.check_output(['dotnet','--version'],text=True).strip(),'platform':platform.platform(),'commands':[]}
start=time.monotonic()
for name,args in [('restore',['dotnet','restore','src/Ashlar.Mcp.Server.Host/Ashlar.Mcp.Server.Host.csproj','-p:NuGetAudit=false','--nologo','-v','minimal']),('build',['dotnet','build','src/Ashlar.Mcp.Server.Host/Ashlar.Mcp.Server.Host.csproj','-c','Release','--no-restore','-p:NuGetAudit=false','--nologo','-v','minimal'])]:
    tick=time.monotonic()
    with (root/(name+'.log')).open('w') as log:
        result=subprocess.run(args,env=env,stdout=log,stderr=subprocess.STDOUT)
    record['commands'].append({'name':name,'arguments':args,'exitCode':result.returncode,'seconds':time.monotonic()-tick})
    (root/'measurement.json').write_text(json.dumps(record,indent=2)+'\n')
    if result.returncode:break
record['seconds']=time.monotonic()-start;record['completedAt']=datetime.datetime.now(datetime.timezone.utc).isoformat()
record['packageBytes']=sum(p.stat().st_size for p in packages.rglob('*') if p.is_file())
(root/'measurement.json').write_text(json.dumps(record,indent=2)+'\n')
print(json.dumps(record))
raise SystemExit(record['commands'][-1]['exitCode'])
