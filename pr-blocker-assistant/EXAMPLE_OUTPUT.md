# Example Output

This document shows sample output from the PR Blocker Assistant for different scenarios.

## Example 1: Blocked PR (Failed Checks)

```bash
$ pr-blocker https://github.com/microsoft/terminal/pull/12345
```

**Output:**

```
Analyzing: https://github.com/microsoft/terminal/pull/12345
Using provided GitHub token

## PR Status
❌ **BLOCKED**

**3 blocker(s) found:**
- Check failed: Build / Windows (x64, Release) - Build failed with 2 errors
- Check failed: Spell Check - Misspellings detected
- 1 reviewer(s) requested changes

Ready: False

Detailed blockers:
  - Check failed: Build / Windows (x64, Release) - Build failed with 2 errors
  - Check failed: Spell Check - Misspellings detected
  - 1 reviewer(s) requested changes

Evidence (JSON):
{
  "pr": {
    "Number": 12345,
    "Title": "Add new color scheme feature",
    "State": "Open",
    "Mergeable": true,
    "MergeableState": "unstable",
    "Merged": false,
    "Draft": false
  },
  "combinedStatus": {
    "State": "Failure",
    "TotalCount": 12,
    "Statuses": [
      {
        "Context": "Build / Windows (x64, Release)",
        "State": "Failure",
        "Description": "Build failed with 2 errors"
      },
      // ... more statuses
    ]
  },
  "checkRuns": {
    "TotalCount": 8,
    "CheckRuns": [
      {
        "Name": "Spell Check",
        "Status": "Completed",
        "Conclusion": "Failure",
        "Title": "Spelling errors found",
        "Summary": "Found 3 misspelled words"
      },
      // ... more check runs
    ]
  },
  "reviews": [
    {
      "Login": "reviewer1",
      "State": "ChangesRequested",
      "SubmittedAt": "2026-10-24T01:30:00Z"
    }
  ],
  "reviewSummary": {
    "Approvals": 0,
    "ChangesRequested": 1
  }
}
```

## Example 2: Ready to Merge

```bash
$ pr-blocker owner/repo#456
```

**Output:**

```
Analyzing: owner/repo#456

## PR Status
✅ **READY TO MERGE**

All checks passed, no blockers detected.

Ready: True

Detailed blockers:

Evidence (JSON):
{
  "pr": {
    "Number": 456,
    "Title": "Fix typo in README",
    "State": "Open",
    "Mergeable": true,
    "MergeableState": "clean",
    "Merged": false,
    "Draft": false
  },
  "combinedStatus": {
    "State": "Success",
    "TotalCount": 5,
    "Statuses": [
      {
        "Context": "continuous-integration/jenkins",
        "State": "Success",
        "Description": "Build succeeded"
      }
    ]
  },
  "checkRuns": {
    "TotalCount": 3,
    "CheckRuns": [
      {
        "Name": "CI / test",
        "Status": "Completed",
        "Conclusion": "Success"
      }
    ]
  },
  "reviews": [
    {
      "Login": "maintainer",
      "State": "Approved",
      "SubmittedAt": "2026-10-24T02:00:00Z"
    }
  ],
  "reviewSummary": {
    "Approvals": 1,
    "ChangesRequested": 0
  }
}
```

## Example 3: Draft PR

```bash
$ pr-blocker https://github.com/owner/repo/pull/789
```

**Output:**

```
Analyzing: https://github.com/owner/repo/pull/789

## PR Status
❌ **BLOCKED**

**1 blocker(s) found:**
- PR is in draft mode

Ready: False

Detailed blockers:
  - PR is in draft mode

Evidence (JSON):
{
  "pr": {
    "Number": 789,
    "Title": "[WIP] Refactor authentication module",
    "State": "Open",
    "Mergeable": true,
    "MergeableState": "clean",
    "Merged": false,
    "Draft": true
  },
  // ... rest of evidence
}
```

## Example 4: Merge Conflicts

```bash
$ pr-blocker https://github.com/owner/repo/pull/321
```

**Output:**

```
Analyzing: https://github.com/owner/repo/pull/321

## PR Status
❌ **BLOCKED**

**2 blocker(s) found:**
- PR has merge conflicts
- Checks are still pending

Ready: False

Detailed blockers:
  - PR has merge conflicts
  - Checks are still pending

Evidence (JSON):
{
  "pr": {
    "Number": 321,
    "Title": "Update dependencies",
    "State": "Open",
    "Mergeable": false,
    "MergeableState": "dirty",
    "Merged": false,
    "Draft": false
  },
  // ... rest of evidence
}
```

## Example 5: Release Check

```bash
$ pr-blocker https://github.com/owner/repo/releases/tag/v1.0.0
```

**Output:**

```
Analyzing: https://github.com/owner/repo/releases/tag/v1.0.0

## Release Status
✅ **READY FOR RELEASE**

Ready: True

Evidence (JSON):
{
  "release": {
    "Name": "Version 1.0.0",
    "TagName": "v1.0.0",
    "Draft": false,
    "Prerelease": false,
    "CreatedAt": "2026-10-24T00:00:00Z",
    "PublishedAt": "2026-10-24T01:00:00Z",
    "AssetsCount": 4
  }
}
```

## Example 6: Draft Release

```bash
$ pr-blocker https://github.com/owner/repo/releases/tag/v2.0.0-beta
```

**Output:**

```
Analyzing: https://github.com/owner/repo/releases/tag/v2.0.0-beta

## Release Status
❌ **NOT READY**

**2 issue(s) found:**
- Release is in draft state
- Release has no assets/artifacts

Ready: False

Detailed blockers:
  - Release is in draft state
  - Release has no assets/artifacts

Evidence (JSON):
{
  "release": {
    "Name": "Version 2.0.0 Beta",
    "TagName": "v2.0.0-beta",
    "Draft": true,
    "Prerelease": true,
    "CreatedAt": "2026-10-24T02:00:00Z",
    "PublishedAt": null,
    "AssetsCount": 0
  }
}
```

## Using Evidence for Debugging

The JSON evidence can be parsed programmatically:

```bash
pr-blocker owner/repo#123 | jq '.evidence.checkRuns.CheckRuns[] | select(.Conclusion == "Failure")'
```

Or saved for later analysis:

```bash
pr-blocker owner/repo#123 > pr-123-status.txt
```
