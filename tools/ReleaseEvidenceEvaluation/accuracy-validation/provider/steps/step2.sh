#!/usr/bin/env bash
set -euo pipefail
git reset --hard 86c6e9e18
git clean -fd
git apply /provider-evidence/step2.patch
cp /src-mirror/src/Ashlar.Bricks.GitHub/GitHub/RequiredCheckEvaluation.cs src/Ashlar.Bricks.GitHub/GitHub/RequiredCheckEvaluation.cs
cp /src-mirror/src/Ashlar.Tests.Infrastructure/Tests/Bricks/GitHubProviderBindingRegressionTests.cs src/Ashlar.Tests.Infrastructure/Tests/Bricks/GitHubProviderBindingRegressionTests.cs
sha256sum /provider-evidence/step2.patch src/Ashlar.Bricks.GitHub/GitHub/RequiredCheckEvaluation.cs src/Ashlar.Tests.Infrastructure/Tests/Bricks/GitHubProviderBindingRegressionTests.cs
NuGetAudit=false dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net10.0 --filter 'FullyQualifiedName~GitHubBlockersBrickTests|FullyQualifiedName~GitHubReadAccessTests|FullyQualifiedName~GitHubEvaluationRegressionTests|FullyQualifiedName~GitHubProviderAssuranceRegressionTests' --logger 'trx;LogFileName=step2.trx' --results-directory /provider-evidence --nologo -v minimal
