#!/usr/bin/env bash
set +e
git rev-parse HEAD > /validation-output/actual-head.txt
git status --porcelain > /validation-output/initial-porcelain.txt
NuGetAudit=false bash scripts/run-cert-gate.sh > /validation-output/net8.log 2>&1
cert_status=$?
printf '%s\n' "$cert_status" > /validation-output/net8.status
if [ -f test-results/cert-gate.trx ]; then cp test-results/cert-gate.trx /validation-output/net8.trx; fi
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net10.0 --filter 'FullyQualifiedName~GitHubBlockersBrickTests|FullyQualifiedName~GitHubReadAccessTests|FullyQualifiedName~GitHubEvaluationRegressionTests|FullyQualifiedName~GitHubProviderAssuranceRegressionTests|FullyQualifiedName~GitHubRecollectionTests|FullyQualifiedName~CertGateFilterCoverageTests' -p:NuGetAudit=false --logger 'trx;LogFileName=net10.trx' --results-directory /validation-output --nologo -v minimal > /validation-output/net10.log 2>&1
focused_status=$?
printf '%s\n' "$focused_status" > /validation-output/net10.status
dotnet restore Ashlar.LocalDevCore.slnf -p:NuGetAudit=false > /validation-output/core.log 2>&1
core_status=$?
if [ "$core_status" -eq 0 ]; then
 dotnet build Ashlar.LocalDevCore.slnf --no-restore -v minimal >> /validation-output/core.log 2>&1
 core_status=$?
fi
printf '%s\n' "$core_status" > /validation-output/core.status
git status --porcelain > /validation-output/final-porcelain.txt
chown -R 1000:1000 /validation-output
if [ "$cert_status" -ne 0 ] || [ "$focused_status" -ne 0 ] || [ "$core_status" -ne 0 ]; then exit 1; fi
