#!/usr/bin/env bash
set -euo pipefail
NuGetAudit=false dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net10.0 --filter 'FullyQualifiedName~GitHubBlockersBrickTests|FullyQualifiedName~GitHubReadAccessTests|FullyQualifiedName~GitHubEvaluationRegressionTests|FullyQualifiedName~GitHubProviderAssuranceRegressionTests' --logger 'trx;LogFileName=step1.trx' --results-directory /provider-evidence --nologo -v minimal
