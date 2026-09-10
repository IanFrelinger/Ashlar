# Reference consumer host

The minimal ASP.NET Core host that fronts an authored brick over HTTP: `AddAshlarBrick<T>()` before
`AddAshlar()`, a `GET /health`, and a `POST /api/bricks/{brickId}/execute` that maps the wire DTOs
onto `Brick.ExecuteAsync`. It is the answer to "what does a consumer host look like", written once.

These are real, compilable files rather than prose or a heredoc because this host is *verified*, not
merely described: `scripts/verify-external-product-shape.sh` renders them into a throwaway consumer
tree, restores it from a package feed with no repo project references, boots it and round-trips a
request through it. That script is the executable proof that this template still works, and CI runs
it as the `external-product-shape` job of `.github/workflows/distribution-matrix-gate.yml`.

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
