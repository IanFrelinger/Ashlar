# Android / Play publishing (out of repo)

This repository is **Linux-first** and no longer ships a native Android or MAUI client. Packaging an app for Google Play (AAB/APK, signing, tracks) belongs in a **separate application repo** that consumes **`Ashlar.Client`** (NuGet) or calls **`Ashlar.API`** over HTTPS.

The workload-free HTTP reference clients (console, Blazor, Avalonia) that used to live in `docs/demos/` are parked on the `archive/parked-2026-10-03` branch.
