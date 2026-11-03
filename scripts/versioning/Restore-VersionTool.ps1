#Requires -Version 7.0
# Canonical template owner: Skills product, manage-project-versions/assets.
# Uses the vendor's native .NET tool lifecycle, independently of a source SDK pin.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Repository)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$manifest=Join-Path ([IO.Path]::GetFullPath($Repository)) '.config/dotnet-tools.json'
$pin=Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json -AsHashtable
if ($pin.isRoot -ne $true -or $pin.tools['gitversion.tool'].version -cne '6.8.2' -or
    ($pin.tools['gitversion.tool'].commands -join ',') -cne 'dotnet-gitversion') { throw 'Select the exact local GitVersion.Tool 6.8.2 manifest' }
$root=Join-Path $env:LOCALAPPDATA 'Icm.Ai/BuildTools/GitVersion/6.8.2'
$executable=Join-Path $root 'dotnet-gitversion.exe'
if (Test-Path -LiteralPath $root) {
    if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint -or
        -not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Existing vendor tool directory requires explicit native repair; no overwrite or fallback' }
} else {
    # A neutral working directory lets the installed SDK run the vendor installer;
    # source compiler SDK requirements continue to govern source builds.
    $neutral=Join-Path ([IO.Path]::GetTempPath()) ('icm-gitversion-restore-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $neutral | Out-Null
    $config=Join-Path $neutral 'NuGet.Config'
    [IO.File]::WriteAllText($config,'<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>',[Text.UTF8Encoding]::new($false))
    Push-Location -LiteralPath $neutral
    try {
        & dotnet tool install GitVersion.Tool --version 6.8.2 --tool-path $root --configfile $config | Write-Host
        if ($LASTEXITCODE -ne 0) { throw "Native vendor installation failed; retain $neutral and $root for inspection" }
    } finally { Pop-Location }
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($neutral)).TrimEnd('\','/') -ine [IO.Path]::GetTempPath().TrimEnd('\','/') -or
        (Get-Item -LiteralPath $neutral).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected restore scratch cleanup path' }
    Remove-Item -LiteralPath $neutral -Recurse
}
$version=(& $executable /version) -join "`n"
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^6\.8\.2(?:\+|$)') { throw 'The selected native vendor tool identity differs' }
[ordered]@{tool='GitVersion.Tool@6.8.2';path=$executable;sha256=(Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant();manifestSha256=(Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant()} | ConvertTo-Json
