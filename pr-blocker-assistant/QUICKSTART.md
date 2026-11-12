# Quick Start Guide

## Prerequisites

- .NET 10 SDK (or .NET 8 SDK - the packages target both)
- Optional: GitHub personal access token for higher rate limits

## Installation

### Option 1: Build and Run from Source

```bash
git clone [your-repo-url]
cd pr-blocker-assistant

# Restore packages (from nuget.org only)
dotnet restore --configfile nuget.config

# Build
dotnet build

# Run
dotnet run --project src/PrBlocker.CLI -- https://github.com/owner/repo/pull/123
```

### Option 2: Install as Global Tool (future)

```bash
dotnet tool install -g PrBlocker.CLI
pr-blocker https://github.com/owner/repo/pull/123
```

## Usage Examples

### Check a Pull Request

```bash
# Basic check
dotnet run --project src/PrBlocker.CLI -- https://github.com/microsoft/terminal/pull/12345

# With GitHub token (recommended)
export GITHUB_TOKEN=ghp_your_token_here
dotnet run --project src/PrBlocker.CLI -- https://github.com/microsoft/terminal/pull/12345

# Or pass token directly
dotnet run --project src/PrBlocker.CLI -- https://github.com/microsoft/terminal/pull/12345 --token ghp_xxx
```

### Short URL Format

```bash
# These also work
dotnet run --project src/PrBlocker.CLI -- microsoft/terminal#12345
```

### Check a Release

```bash
dotnet run --project src/PrBlocker.CLI -- https://github.com/microsoft/terminal/releases/tag/v1.15.0
```

## Understanding the Output

The tool will display:

1. **Summary** - Quick status (✅ READY or ❌ BLOCKED)
2. **Blockers list** - Specific issues preventing merge/release
3. **Ready status** - Boolean indicating readiness
4. **Evidence (JSON)** - Full structured data including:
   - PR metadata
   - Combined status from commit statuses
   - Check runs (GitHub Actions, etc.)
   - Review approvals and change requests
   - Merge conflicts

## Example Output

```
Analyzing: https://github.com/IanFrelinger/Ashlar/pull/123

## PR Status
❌ **BLOCKED**

**3 blocker(s) found:**
- Check failed: build-core - Build failed
- 1 reviewer(s) requested changes
- Checks are still pending

Ready: False

Detailed blockers:
  - Check failed: build-core - Build failed
  - 1 reviewer(s) requested changes
  - Checks are still pending

Evidence (JSON):
{
  "pr": {
    "Number": 123,
    "Title": "Add new feature",
    "State": "Open",
    "Mergeable": true,
    "MergeableState": "unstable",
    "Merged": false,
    "Draft": false
  },
  "combinedStatus": {
    "State": "Failure",
    ...
  },
  ...
}
```

## GitHub Token Setup

### Why a Token?

- **Without**: 60 requests/hour (quickly exhausted)
- **With**: 5,000 requests/hour

### Getting a Token

1. Go to https://github.com/settings/tokens
2. Generate new token (classic)
3. Select scopes: `repo` (for private repos) or `public_repo` (public only)
4. Copy token (shown once!)
5. Export: `export GITHUB_TOKEN=ghp_your_token_here`

## Testing with Real PRs

Try these public PRs to test:

```bash
# Microsoft Terminal (usually has active PRs)
dotnet run --project src/PrBlocker.CLI -- https://github.com/microsoft/terminal/pull/12345

# .NET Runtime (large project)
dotnet run --project src/PrBlocker.CLI -- https://github.com/dotnet/runtime/pull/67890

# Ashlar itself (if there are open PRs)
dotnet run --project src/PrBlocker.CLI -- https://github.com/IanFrelinger/Ashlar/pull/XX
```

## Troubleshooting

### "dotnet: command not found"

Install .NET SDK from https://dot.net

### "Rate limit exceeded"

Set `GITHUB_TOKEN` environment variable (see above)

### "Could not parse GitHub URL"

Supported formats:
- `https://github.com/owner/repo/pull/123`
- `owner/repo#123`
- `https://github.com/owner/repo/releases/tag/v1.0.0`

### Package restore fails

Make sure you're using `--configfile nuget.config`:

```bash
dotnet restore --configfile nuget.config
```

## Next Steps

1. Test on 10 real PRs (track in USAGE_LOG.md)
2. Report accuracy and findings
3. Suggest Ashlar improvements based on real usage
