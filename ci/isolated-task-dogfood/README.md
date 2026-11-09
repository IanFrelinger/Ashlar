# Isolated task developer dogfood (validation fixture)

This exercises the documented public registration with its real default services.
It is a validation-branch fixture, not a new shipped API or a hostile-code sandbox.

The developer builds a reviewed, non-root Python image. A standalone .NET 10 host
references the pinned Ashlar source, calls `AddAshlarIsolatedTasks()`, serializes a
small inventory CSV inside a JSON argument, and deserializes the worker's JSON
response into a typed record. A SKU containing spaces, a semicolon and `$()` must
round-trip literally. Two successful runs must have fresh scratch, a read-only
application directory, no host-only environment, the expected immutable image ID,
and the requested effective resource budget. Task output has a 4096-byte ceiling.
A malformed inventory must return the worker's exit code and stderr. All three
containers must be removed before return.

The workflow must check out PR source SHA
`7ee90d6b666aadb09105a630b95e8f48311a2b41` into a separate source directory, install
.NET SDKs 10.0.403 and 8.0.427, and use a Linux runner with Docker. Then run:

```bash
bash path/to/fixture/run-dogfood.sh path/to/source "$RUNNER_TEMP/dogfood-evidence"
```

Upload that output directory even on failure. The script records the source SHA,
resolved Python base digest, built worker image inspection, build log, and success
receipt. It intentionally fails if any assertion or the expected receipt is absent.
The host project is copied outside the source checkout to preserve a clean source
tree and to model an external developer consumer.

The Python base tag is resolved to a digest before the build; the request uses the
built image ID. No credentials, host mounts, host environment, dynamic installs,
or network access are granted to the workload. The CI harness itself uses Docker
and network access to build the reviewed image, as an embedding host must do.

This is a small-input parser example. It does not prove resistance to hostile code,
exercise image-declared volumes, provide streaming I/O, package a NuGet release, or
integrate an existing production brick pipeline.
