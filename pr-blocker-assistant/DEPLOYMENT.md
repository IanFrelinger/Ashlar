# Deployment Guide

## Publishing the Tool

### As a .NET Global Tool

1. **Update the csproj** with tool metadata:

```xml
<PropertyGroup>
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>pr-blocker</ToolCommandName>
  <PackageId>PrBlocker.CLI</PackageId>
  <Version>1.0.0</Version>
  <Authors>Your Name</Authors>
  <Description>Command-line tool to analyze what's blocking a GitHub PR or release</Description>
  <PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>
  <RepositoryUrl>https://github.com/yourname/pr-blocker-assistant</RepositoryUrl>
</PropertyGroup>
```

2. **Pack the tool**:

```bash
dotnet pack src/PrBlocker.CLI --configuration Release --output ./artifacts
```

3. **Publish to NuGet.org**:

```bash
dotnet nuget push ./artifacts/PrBlocker.CLI.*.nupkg --source https://api.nuget.org/v3/index.json --api-key YOUR_API_KEY
```

4. **Install globally**:

```bash
dotnet tool install -g PrBlocker.CLI
pr-blocker https://github.com/owner/repo/pull/123
```

### As a Self-Contained Binary

Build for specific platforms:

```bash
# Linux x64
dotnet publish src/PrBlocker.CLI -c Release -r linux-x64 --self-contained -o ./artifacts/linux-x64

# macOS ARM64
dotnet publish src/PrBlocker.CLI -c Release -r osx-arm64 --self-contained -o ./artifacts/osx-arm64

# Windows x64
dotnet publish src/PrBlocker.CLI -c Release -r win-x64 --self-contained -o ./artifacts/win-x64
```

Users can then run the binary directly without .NET SDK installed.

## Container Image

Create a `Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props nuget.config ./
COPY src/PrBlocker.Brick/*.csproj ./src/PrBlocker.Brick/
COPY src/PrBlocker.CLI/*.csproj ./src/PrBlocker.CLI/

RUN dotnet restore --configfile nuget.config src/PrBlocker.CLI/PrBlocker.CLI.csproj

COPY src/ ./src/
RUN dotnet publish src/PrBlocker.CLI -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./

ENTRYPOINT ["dotnet", "PrBlocker.CLI.dll"]
```

Build and run:

```bash
docker build -t pr-blocker .
docker run --rm -e GITHUB_TOKEN pr-blocker https://github.com/owner/repo/pull/123
```

## Integration Examples

### As a GitHub Action

`.github/workflows/check-pr.yml`:

```yaml
name: Check PR Blockers

on:
  pull_request:
    types: [opened, synchronize, reopened]

jobs:
  check:
    runs-on: ubuntu-latest
    steps:
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '10.0.x'
        
    - name: Install PR Blocker
      run: dotnet tool install -g PrBlocker.CLI
      
    - name: Check this PR
      run: |
        pr-blocker ${{ github.event.pull_request.html_url }}
      env:
        GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

### As a CI Check Script

```bash
#!/bin/bash
# check-prs.sh - Check all open PRs in a repository

REPO="owner/repo"
GITHUB_TOKEN="${GITHUB_TOKEN}"

# Get all open PRs
prs=$(gh pr list --repo "$REPO" --json number --jq '.[].number')

for pr in $prs; do
    echo "Checking PR #$pr..."
    pr-blocker "${REPO}#${pr}"
    echo ""
done
```

### Integration with Slack

```bash
#!/bin/bash
# notify-blocked-prs.sh - Post to Slack when PRs are blocked

REPO="owner/repo"
SLACK_WEBHOOK_URL="https://hooks.slack.com/services/YOUR/WEBHOOK/URL"

# Check PR and parse output
result=$(pr-blocker "https://github.com/${REPO}/pull/123")

if echo "$result" | grep -q "BLOCKED"; then
    curl -X POST "$SLACK_WEBHOOK_URL" \
        -H 'Content-Type: application/json' \
        -d "{\"text\": \"PR blocked:\\n\`\`\`${result}\`\`\`\"}"
fi
```

## Monitoring and Automation

### Scheduled Checks

Use cron or systemd timer to check PRs periodically:

```bash
# crontab entry: check all open PRs every hour
0 * * * * /usr/local/bin/check-all-open-prs.sh >> /var/log/pr-checks.log 2>&1
```

### Dashboard Integration

Expose as a web service and integrate with monitoring dashboards:

```bash
# Simple HTTP wrapper
while true; do
  pr_url=$(nc -l -p 8080 | grep -oP 'pr_url=\K[^&\s]+')
  pr-blocker "$pr_url" | tee response.txt
  cat response.txt | nc -l -p 8080
done
```

## Security Considerations

1. **GitHub Token Storage**
   - Use environment variables or secret managers
   - Never commit tokens to git
   - Use minimal scopes (public_repo for public, repo for private)

2. **Rate Limiting**
   - Token provides 5,000 requests/hour
   - Cache results for frequently checked PRs
   - Implement exponential backoff on rate limit errors

3. **Network Security**
   - Run in isolated network environments when possible
   - Consider corporate proxy settings
   - Validate SSL certificates

## Troubleshooting

See QUICKSTART.md for common issues and solutions.
