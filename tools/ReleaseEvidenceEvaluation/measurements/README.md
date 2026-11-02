# Installation and live timing observations

These observations use a fresh source clone and a new empty `NUGET_PACKAGES` profile
in a new Linux container. The .NET 10.0.403 SDK, actual .NET/ASP.NET 8 runtime and
`ashlar-devtest:slim` image were already installed/cached. SDK/image download time is
**not measured**. Remote proxy/CDN and OS caches, shared machine load, and CPU scheduling
were not controlled. No cold-runner or customer productivity claim follows.

- `cold-cache-build/`: restore 22.370s, Release build 79.304s, inside-container total 101.676s;
  831,516,182 package bytes. Candidate `47a7c8506`; no collection.
- `cold-cache-to-public-pack/`: separate empty application cache, restore 21.479s,
  build 40.219s, client/host collection 5.822s, inside-container total 67.523s.
  Measured full wrapper time including container startup, local clone, build,
  collection and teardown 82.668s. The pack's own API collection interval 4.908s.

The latter recollected public Ashlar PR #900 live for timing only, after ground-truth
freeze. It is excluded from the 36-case accuracy corpus and is not claimed equivalent
to the earlier frozen snapshot. It observed `blocked` / assurance `fail`. Code supplied
no token; inherited proxy upstream identity was not observable. API sources were read
sequentially by the real host/client path. No explicit wrapper retries occurred;
SDK/NuGet internal retry behavior is left in the retained logs.

Both runs disabled NuGet vulnerability auditing (`-p:NuGetAudit=false`); they are build
and workflow measurements, not dependency security audits. JSON measurements record
exact commands, commits, platform, dates, exit codes and durations. Text logs retain
actual command output; `measure.py` shows the timing instrumentation. Reproduction
requires a new output/profile directory mounted at `/measurement` and the repository's
`scripts/test-in-container.sh --ref <candidate> -- 'python3 /measurement/measure.py'` path,
with the normal proxy/CA mounts when required. Recollection creates a new live snapshot.
