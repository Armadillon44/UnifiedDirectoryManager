# Unified Directory Manager — .NET 10 build notes

This is the **primary** Unified Directory Manager project, targeting **.NET 10** (`net10.0-windows`). It originated as
a .NET 10 retarget of the original .NET 8 WPF app; the application/business code is identical — only the
target framework and NuGet package references differ from that earlier .NET 8 build.

Status: **builds, publishes, and runs on .NET 10** (verified with SDK 10.0.301 / WindowsDesktop
runtime 10.0.9 — clean build, 0 warnings; self-contained single-file exe launches successfully).

## .NET 10 specifics
- `src/UnifiedDirectoryManager/UnifiedDirectoryManager.csproj`: `<TargetFramework>` is `net10.0-windows`.
- `global.json`: pins the SDK to `10.0.100` (rollForward `latestFeature`, so any 10.0.x SDK satisfies it).
- NuGet packages:
  - `CommunityToolkit.Mvvm` → `8.4.2`.
  - `System.DirectoryServices.Protocols` → `10.0.9`.
  - `System.DirectoryServices` has **no** `PackageReference` — it is provided by the `net10.0-windows`
    framework. An explicit reference raises **NU1510** (".NET 10 prunes framework-provided packages").
    `System.DirectoryServices.Protocols` is *not* framework-provided, so it remains a package reference.

## Building / publishing
Requires the **.NET 10 SDK** and the .NET 10 **WindowsDesktop** runtime.

```powershell
dotnet build src/UnifiedDirectoryManager/UnifiedDirectoryManager.csproj -c Release
./build/publish.ps1            # self-contained single-file win-x64 + win-arm64 -> ./dist/<rid>/UnifiedDirectoryManager.exe
```

> Publish gotcha: if a previously-built `dist/<rid>/UnifiedDirectoryManager.exe` is still running, publish fails with
> "file being used by another process" — stop that process first.
