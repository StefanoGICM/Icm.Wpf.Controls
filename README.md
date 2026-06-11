# Icm.Wpf.Controls

Shared WPF controls for ICM desktop applications.

## Controls

- `AutoFilterDataGrid`: `DataGrid` with Excel-style column filters and optional initial column filter values.

## Package

Build and pack locally:

```powershell
dotnet build -c Release
dotnet test -c Release
dotnet pack .\src\Icm.Wpf.Controls\Icm.Wpf.Controls.csproj -c Release -o .\artifacts\packages
```

Publish to the internal GitHub Packages feed:

```powershell
dotnet nuget push .\artifacts\packages\Icm.Wpf.Controls.1.0.0.nupkg `
  --source https://nuget.pkg.github.com/StefanoGICM/index.json `
  --api-key $env:NUGET_AUTH_TOKEN `
  --skip-duplicate
```
