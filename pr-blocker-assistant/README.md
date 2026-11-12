# PR Blocker Assistant

A command-line tool that answers: **"What is blocking this PR or release?"**

Built as a consumer application on **Ashlar 0.3.0**, demonstrating real-world usage of the published Ashlar packages from nuget.org.

## What It Does

Given a GitHub PR URL or release tag, the tool:

- ✅ Gathers commit status and check runs
- ✅ Reviews approval status and change requests
- ✅ Identifies merge conflicts
- ✅ Checks release artifacts and draft status
- ✅ Returns a concise summary with evidence links
- ✅ Explicitly states unknowns or blockers

## Architecture

This is a **pure consumer** of Ashlar 0.3.0:

- Uses published packages from nuget.org only (no local dependencies)
- Built on `Ashlar.Hosting.Bundle` v0.3.0
- Implements a custom brick (`PrBlockerBrick`) using `Ashlar.Authoring`
- Simple CLI host that executes the brick and displays results

## Quick Start

See [QUICKSTART.md](QUICKSTART.md) for detailed installation and usage instructions.

### TL;DR

```bash
# Clone and build
git clone [repo-url]
cd pr-blocker-assistant
./build.sh

# Run against a PR
export GITHUB_TOKEN=ghp_your_token_here
dotnet run --project src/PrBlocker.CLI -- https://github.com/owner/repo/pull/123

# Or use the test script
./test-real-pr.sh https://github.com/microsoft/terminal/pull/17796
```

See [EXAMPLE_OUTPUT.md](EXAMPLE_OUTPUT.md) for sample output from different scenarios.

## Usage Examples

Check a PR:
```bash
pr-blocker https://github.com/owner/repo/pull/123
pr-blocker owner/repo#123
```

Check a release:
```bash
pr-blocker https://github.com/owner/repo/releases/tag/v1.0.0
```

## Building

```bash
dotnet restore --configfile nuget.config
dotnet build
```

## Publishing as a Tool

```bash
dotnet pack src/PrBlocker.CLI
dotnet tool install -g --add-source ./artifacts PrBlocker.CLI
pr-blocker https://github.com/owner/repo/pull/123
```

## What This Demonstrates

### Ashlar Consumer Patterns

1. **Package consumption**: Clean consumption of `Ashlar.Hosting.Bundle` 0.3.0 from nuget.org
2. **Brick authoring**: Custom brick implementation using `DomainBrick`
3. **Host integration**: Minimal host setup with `AddAshlar()` and `AddAshlarBrick<T>()`
4. **Brick execution**: Direct brick execution through `IBrickExecutor`

### Real Integration

- **Octokit** for GitHub API access
- **Microsoft.Extensions.Hosting** for DI and configuration
- Real async operations with proper cancellation
- Error handling for missing resources, rate limits, etc.

## Usage Log

Track real assessments in `USAGE_LOG.md`:

1. PR assessed
2. Correctness of conclusion
3. Time saved vs manual checking
4. Any framework gaps discovered

## Next Steps

1. Test on 10 real PRs/releases
2. Document findings
3. Identify Ashlar improvements
4. Consider IDE integration

## License

Apache 2.0 (same as Ashlar)
