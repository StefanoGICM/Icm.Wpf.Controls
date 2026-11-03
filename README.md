# Icm.Wpf.Controls

Shared WPF controls for ICM desktop applications. The library targets `net10.0-windows` and
builds with the .NET 10 SDK pinned in `global.json`.

## Controls

- `AutoFilterDataGrid`: `DataGrid` with Excel-style column filters and optional initial column filter values.

## Build and version

Its consumers, Icm.Bom.Explorer and the tests of Icm.Pdm.ProductManager, build it from source
with a `ProjectReference`, like the other shared libraries of the Icm.Bom workspace. Since
2026-11-03 no NuGet package is packed or published (the last one was 2.0.0).

```powershell
dotnet build -c Release
dotnet test -c Release
```

The version comes from Git: GitVersion with GitFlow (`GitVersion.yml`), restarted at 0.1.0 with
the whole Icm.Bom suite. An ordinary build is `0.0.0-dev`; a release build of a consumer takes
the version that `scripts/versioning/Prepare-Version.ps1` prepares.
