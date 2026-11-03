# Relationship to Ashlar Release Manager

This document clarifies how this project relates to the existing [ashlar-release-manager](https://github.com/IanFrelinger/ashlar-release-manager).

## What ashlar-release-manager Does

The ashlar-release-manager is a **background monitoring system** that:

- Runs continuously as a daemon
- Monitors repository changes (repo-monitor agent)
- Runs framework smoke tests (test-runner agent)
- Collects SLO evidence (slo-collector agent)
- Generates readiness reports (report-generator agent)
- Uses only the **deterministic provider** (no model, no network calls)
- Focuses on **internal repository readiness** monitoring

**Key characteristics**:
- Passive monitoring
- Scheduled background jobs
- Local repository focus
- Report generation for CI/CD pipelines

## What PR Blocker Assistant Does

This is an **interactive query tool** that:

- Runs on-demand when you need an answer NOW
- Takes a specific PR or release URL as input
- Queries GitHub's live API for current status
- Returns immediate analysis of blockers
- Uses **external API integration** (GitHub via Octokit)
- Focuses on **GitHub PR/release status**

**Key characteristics**:
- Active querying
- On-demand CLI tool
- Cross-repository capable
- Real-time GitHub integration

## Complementary Use Cases

They solve different problems:

| Aspect | ashlar-release-manager | PR Blocker Assistant |
|--------|------------------------|----------------------|
| **Trigger** | Scheduled background | On-demand command |
| **Scope** | Local repository monitoring | Any GitHub PR/release |
| **Data source** | Local artifacts, test results | GitHub API live data |
| **Output** | Periodic reports | Immediate CLI answer |
| **Use case** | "Is our repo ready?" | "What's blocking this PR?" |
| **User** | CI/CD pipeline | Developer at terminal |

## What We Built On

From ashlar-release-manager we learned:

1. ✅ **Package consumption pattern**
   - nuget.config with only nuget.org
   - Central package management via Directory.Packages.props
   - One AshlarVersion property

2. ✅ **Host pattern**
   - Minimal Program.cs
   - `AddAshlar()` registration
   - Configuration loading

3. ✅ **Background agents architecture**
   - Though we DON'T use background agents (we use bricks instead)

## What We Built Fresh

This project adds:

1. ✅ **Interactive CLI pattern** - new for Ashlar consumers
2. ✅ **External API integration** (GitHub via Octokit)
3. ✅ **Brick-based architecture** instead of background agents
4. ✅ **On-demand execution model**
5. ✅ **Cross-repository querying**

## Future Integration

Possible ways these could work together:

- ashlar-release-manager could **call** pr-blocker-assistant to check upstream dependencies
- pr-blocker-assistant could **read** reports from ashlar-release-manager's output
- Both could share a common `Ashlar.Bricks.GitHub` package (doesn't exist yet)

## Conclusion

These are two different tools for two different workflows:

- **ashlar-release-manager**: "Continuously monitor this repository's readiness"
- **pr-blocker-assistant**: "Right now, what's blocking this specific PR?"

Both demonstrate Ashlar 0.3.0 consumption, but in different architectural patterns.
