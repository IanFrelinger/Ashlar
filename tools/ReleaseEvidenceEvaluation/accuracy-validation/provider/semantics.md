# Provider interpretation used in 76c8be38b

- Branch-protection `checks` carries each context's expected app. `contexts` is its legacy name projection, not an extra unbound requirement.
- Explicit response `app_id:null` is unbound; documented `app_id:-1` allows any app. Missing response metadata stays unknown. Request omission auto-selects a recent app and must not be confused with an unbound GET response.
- Ruleset `integration_id` is an expected integration. Positive IDs and explicit null are supported; missing IDs and undocumented negative IDs remain unknown. No unsupported negative wildcard is inferred.
- Separate policy rules apply together. An unbound source does not erase an unknown or bound source. Conflicting duplicate origin metadata inside one source remains unknown.
- Observed check `app.id` is compared as a positive integer from GitHub's response. Numeric strings, booleans and fractional numbers are rejected rather than coerced into IDs or unbound policy.
- Commit-status `creator.id` identifies a user; it is never used as an app ID. Same-name statuses with unverified origin prevent a bound-provider pass. A known matching failure remains confirmed despite other unknown observations.
- Unbound required names preserve both check-run and commit-status channels. A same-name check/status combination must pass both channels.
- Foreign known-app failures are advisory when the requirement binds to another app. Unidentified same-name observations remain unknown. Definitive missing-provider claims require complete CI and no ambiguous same-name producer candidates.
- The old internal model defaults remain a compatibility fallback; the actual GitHub reader always provides per-context requirements. The global origins flag cannot mask a different context's confirmed result.

## Official sources

- https://docs.github.com/en/rest/branches/branch-protection?apiVersion=2022-11-28
- https://docs.github.com/en/rest/repos/rules?apiVersion=2022-11-28
- https://docs.github.com/en/rest/commits/statuses?apiVersion=2022-11-28
- https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/collaborating-on-repositories-with-code-quality-features/troubleshooting-required-status-checks
- https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets

## Deliberate scope limits

Provider verification means comparing GitHub API assertions, not independently authenticating a check runner or rerunning its tests. Caller identity remains unknown. No credential scope, app capability, bypass authorization, unsupported deployment rule or release authorization is inferred.

Old name-only unit fixtures now report unknown producer details rather than inventing attribution. Their raw responses were not changed. The frozen independent evaluation inputs, labels and prior outputs remain untouched. Legacy GitHub-derived readiness can remain ready while unsupported policy leaves scoped assurance unknown; no result label was changed to hide that limitation.
