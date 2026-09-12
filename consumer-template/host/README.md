# Reference consumer host

The minimal ASP.NET Core host that fronts an authored brick over HTTP: `AddAshlarBrick<T>()` before
`AddAshlar()`, a `GET /health`, and a `POST /api/bricks/{brickId}/execute` that maps the wire DTOs
onto `Brick.ExecuteAsync`. It is the answer to "what does a consumer host **look like**", written
once — a shape, not a trust boundary. See "What this host does not do" below before copying it.

These are real, compilable files rather than prose or a heredoc because this host is *exercised*, not
merely described: `scripts/verify-external-product-shape.sh` renders them into a throwaway consumer
tree, restores it from a package feed with no repo project references, boots it and round-trips a
request through it. CI runs that as the `external-product-shape` job of
`.github/workflows/distribution-matrix-gate.yml`.

Be precise about what that buys, because the word "verified" has a second meaning three directories
away. The script is executable proof of **distribution shape**: that the published package graph
restores with no repo-relative paths, that these files compile against it, that the host boots, that
a request round-trips, and that a RID publish carries no backend natives. It certifies nothing and
asserts nothing about trust — the brick it round-trips has never been through a certification gate.
`external-product-shape` is an honest name for the job; treat it as the job's whole claim.

## What this host does not do

**It executes an unverified brick.** There is no certification record read at startup, no signature
checked, and no hash compared — `CertificationTrustVerifier` is on this project's compile path
(transitively, through `Ashlar.Hosting.Bundle`) and is never called. A record placed beside the
binary would change nothing about whether the host serves.

**And it could not bind one without a second change.** `ExternalProductHost.csproj` takes a
`<ProjectReference>` on the brick, so the brick that runs is the host's own compile — bytes the
certifier never saw. Because the certifier's emit is not byte-reproducible, recompiling the exact
source a certificate covers does not reproduce the assembly hash in the record, so no
`gate-emitted-artifact` hash can cover this host's build. A boot check bolted on while that
reference stands would hash one assembly and run another, and print a trusted verdict for a program
nothing had bound. That is the more dangerous state, which is why this template discloses the gap
rather than half-closing it.

The two halves of the fix, and the recipe for both, are in
[../CONSUMING.md](../CONSUMING.md#certification-what-this-template-binds-and-what-it-does-not). The
binding itself is measured in-repo by
`src/Ashlar.Tests.Infrastructure/Tests/Certification/JudgedArtifactIsTheExecutedArtifactTests.cs`,
which verifies artifact bytes and then executes *those* bytes, so the recipe that document gives is
exercised rather than asserted.

## Tokens

Four `__UPPER_SNAKE__` markers name the brick this host is being built around. They are legal C#
identifiers and legal MSBuild text, so both files still parse — an editor, an analyzer and
`dotnet build` all see the template as the thing it is a template for.

| Token | Becomes | Appears in |
|-------|---------|------------|
| `__ASHLAR_VERSION__` | the Ashlar package version being consumed | `ExternalProductHost.csproj` |
| `__BRICK_PROJECT_NAME__` | the brick project's directory and `.csproj` name | `ExternalProductHost.csproj` |
| `__BRICK_NAMESPACE__` | the brick class's namespace | `Program.cs` |
| `__BRICK_CLASS__` | the brick class | `Program.cs` |

`scripts/verify-external-product-shape.sh` substitutes them with `sed` and refuses to continue if
any survive. Copying this host by hand means doing the same four replacements yourself.

Edits here change what CI verifies, so treat this directory as gate input rather than documentation:
the workflow's path filters list `consumer-template/host/**` for exactly that reason.

## Publishing this host for a RID

The gate also renders a second copy of these files with `<RuntimeIdentifier>` and `<SelfContained>` in
the csproj, publishes it, and runs the published binary — a container image is built that way, and a
RID publish resolves native assets differently enough from `dotnet build` that it failed on this exact
template while the build stage stayed green. See the RID-publish section of
[../CONSUMING.md](../CONSUMING.md), including the opt-in for local GGUF inference.
