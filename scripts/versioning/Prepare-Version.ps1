#Requires -Version 7.0
<#
Generated consumer copies come from Skills product: skills/manage-project-versions/assets.
GitVersion.Tool owns all version calculation. This adapter only binds build inputs
to a clean source checkpoint and maps vendor output to native build properties.
-Check proves that the prepared inputs are still bound to the current checkpoint, settings
and tool without running GitVersion; -Verify also re-derives them. Neither writes.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Repository, [switch]$Verify, [switch]$Check)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Verify -and $Check) { throw 'Select either -Check or -Verify' }

function Invoke-RepositoryGit {
    param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
    $value = & git --no-optional-locks -c core.fsmonitor=false -C $script:repo @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git refused: $($Arguments -join ' ')" }
    return ($value -join "`n").Trim()
}
function Digest([string]$Value) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value))).ToLowerInvariant()
}
function FileDigest([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function ToolPayloadFiles([string]$Root) {
    # .NET enumeration (hidden and system entries included): the provider costs seconds over the vendor's ~100 folders.
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pending.Push([IO.DirectoryInfo]::new($Root))
    $files = [Collections.Generic.SortedDictionary[string,IO.FileInfo]]::new([StringComparer]::Ordinal)
    while ($pending.Count) {
        $directory = $pending.Pop()
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked vendor tool payload is unsupported' }
        foreach ($item in $directory.EnumerateFileSystemInfos()) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked vendor tool payload is unsupported' }
            if ($item -is [IO.DirectoryInfo]) { $pending.Push($item) }
            else { $files.Add([IO.Path]::GetRelativePath($Root,$item.FullName).Replace('\','/'),$item) }
        }
    }
    if (-not $files.Count) { throw 'Empty vendor tool payload' }
    return ,$files
}
# Every payload byte: bound once by prepare and -Verify.
function ToolPayloadDigest([string]$Root) {
    return Digest (@(foreach ($file in (ToolPayloadFiles $Root).GetEnumerator()) { "$($file.Key)`0$($file.Value.Length)`0$(FileDigest $file.Value.FullName)`n" }) -join '')
}
# Path, length and last write of every payload file: cheap enough for every release build.
function ToolPayloadStat([string]$Root) {
    return Digest (@(foreach ($file in (ToolPayloadFiles $Root).GetEnumerator()) { "$($file.Key)`0$($file.Value.Length)`0$($file.Value.LastWriteTimeUtc.Ticks)`n" }) -join '')
}

$repo = (Get-Item -LiteralPath $Repository).FullName.TrimEnd([IO.Path]::DirectorySeparatorChar)
# MSBuild decodes %XX in imported paths and cmd.exe expands %NAME%: either could act on another folder.
if ($repo.Contains('%')) { throw "Move the repository to a path without '%'; MSBuild and cmd.exe would rewrite it" }
if ((Invoke-RepositoryGit @('rev-parse','--show-toplevel')).Replace('/','\') -ne $repo.Replace('/','\')) { throw 'Select the exact repository root' }
if ((Invoke-RepositoryGit @('rev-parse','--is-shallow-repository')) -ne 'false') { throw 'Complete Git history is required; fetch explicitly outside this adapter' }
if (Invoke-RepositoryGit @('status','--porcelain','--untracked-files=all')) { throw 'Commit the intended source checkpoint; unrelated dirty work must be preserved' }
$head = Invoke-RepositoryGit @('rev-parse','HEAD')
$branch = Invoke-RepositoryGit @('symbolic-ref','--quiet','--short','HEAD')
if ($branch -notmatch '^(main|master|develop|feature/.+|release/\d+\.\d+\.\d+|hotfix/\d+\.\d+\.\d+|support/\d+\.\d+)$') { throw "Select a GitFlow branch explicitly: $branch" }
$refs = Invoke-RepositoryGit @('show-ref')
if ($refs -notmatch 'refs/heads/(main|master)(\r?\n|$)' -or $refs -notmatch 'refs/heads/develop(\r?\n|$)') { throw 'GitFlow needs existing main/master and develop branches; create missing branches explicitly without resetting existing refs' }
$config = Join-Path $repo 'GitVersion.yml'
$manifest = Join-Path $repo '.config/dotnet-tools.json'
$pin = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json -AsHashtable
if ($pin.isRoot -ne $true -or $pin.tools['gitversion.tool'].version -cne '6.8.2' -or
    ($pin.tools['gitversion.tool'].commands -join ',') -cne 'dotnet-gitversion') { throw 'An exact local GitVersion.Tool 6.8.2 manifest is required' }
$executable=Join-Path $env:LOCALAPPDATA 'Icm.Ai/BuildTools/GitVersion/6.8.2/dotnet-gitversion.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Run this repository Restore-VersionTool.ps1 explicitly; the pinned vendor CLI is unavailable' }
$toolRoot = [IO.Path]::GetDirectoryName($executable)
# The stat fingerprint is taken before the byte digest, so its comparison after the GitVersion run covers both.
$payloadStat = ToolPayloadStat $toolRoot
$binding = [ordered]@{ repository=$repo; commit=$head; branch=$branch; refsSha256=(Digest $refs); configSha256=(FileDigest $config); manifestSha256=(FileDigest $manifest); toolExecutableSha256=(FileDigest $executable) }
if (-not $Check) { $binding.toolPayloadSha256 = ToolPayloadDigest $toolRoot }
$binding.toolPayloadStatSha256 = $payloadStat
$output = Join-Path $repo '.artifacts/versioning'
$recordPath = Join-Path $output 'version.json'
$propsPath = Join-Path $output 'Version.props'
if ($Verify -or $Check) {
    if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf) -or -not (Test-Path -LiteralPath $propsPath -PathType Leaf)) { throw 'Version inputs are missing; prepare this repository again' }
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json -AsHashtable
    if ($record.kind -cne 'icm-gitversion-build-input' -or $record.tool -cne 'GitVersion.Tool@6.8.2' -or $record.releaseCertification -ne $false) { throw 'Unexpected version input producer' }
    if ($record.schemaVersion -ne 3) { throw "Version input schema $($record.schemaVersion) is not supported by this adapter; prepare this repository again" }
    foreach ($key in $binding.Keys) { if ($record.source[$key] -cne $binding[$key]) { throw "Stale version inputs: $key" } }
    if ($record.propsSha256 -cne (FileDigest $propsPath)) { throw 'Version props integrity mismatch' }
    if ($Check) {
        Write-Output "Checked $($record.version) at $head"
        exit 0
    }
}
Push-Location -LiteralPath $repo
try {
    $toolVersion = (& $executable /version) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $toolVersion -notmatch '^6\.8\.2(?:\+|$)') { throw 'Restore the exact local tool manifest; no global fallback is supported' }
    $raw = (& $executable $repo /output json /nofetch /nonormalize /nocache) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "GitVersion failed: $raw" }
} finally { Pop-Location }
$v = $raw | ConvertFrom-Json -AsHashtable
if ($v.Sha -cne $head -or $v.BranchName -cne $branch -or $v.UncommittedChanges -ne 0 -or $v.SemVer.Contains('+')) { throw 'GitVersion returned a different or unclean source identity' }
if ($v.SemVer -notmatch '-') {
    $tags = (Invoke-RepositoryGit @('tag','--points-at','HEAD')) -split "`n"
    if ($branch -notmatch '^(main|master|support/\d+\.\d+)$' -or $tags -cnotcontains "v$($v.SemVer)") { throw 'Stable versions require the exact vSemVer tag on main/master or its support line' }
}
foreach ($name in @('AssemblySemVer','AssemblySemFileVer')) {
    $parts = $v[$name].Split('.')
    if ($parts.Count -ne 4 -or ($parts | Where-Object { $_ -notmatch '^\d+$' -or [long]$_ -gt $(if ($name -eq 'AssemblySemVer') { 65534 } else { 65535 }) })) { throw "$name exceeds native numeric limits; do not truncate or wrap" }
}
$msi = "$($v.Major).$($v.Minor).$($v.Patch)"
if ($v.Major -gt 255 -or $v.Minor -gt 255 -or $v.Patch -gt 65535) { $msi = '' }
$properties = [ordered]@{ Version=$v.SemVer; PackageVersion=$v.SemVer; VersionPrefix="$($v.Major).$($v.Minor).$($v.Patch)"; VersionSuffix=''; AssemblyVersion=$v.AssemblySemVer; FileVersion=$v.AssemblySemFileVer; InformationalVersion="$($v.SemVer)+sha.$head"; IncludeSourceRevisionInInformationalVersion='false'; IcmProductVersion=$v.SemVer; IcmExpectedAssemblyVersion=$v.AssemblySemVer; IcmExpectedFileVersion=$v.AssemblySemFileVer; IcmExpectedInformationalVersion="$($v.SemVer)+sha.$head"; IcmMsiVersion=$msi; IcmVersionCommit=$head }
$projections = [ordered]@{ assemblyVersion=$v.AssemblySemVer; fileVersion=$v.AssemblySemFileVer; informationalVersion="$($v.SemVer)+sha.$head"; msiVersion=$msi; nativeProductVersion=$v.MajorMinorPatch }
$xml = "<Project><PropertyGroup>`n" + (($properties.GetEnumerator() | ForEach-Object { "    <$($_.Key)>$([Security.SecurityElement]::Escape([string]$_.Value))</$($_.Key)>" }) -join "`n") + "`n</PropertyGroup></Project>`n"
if ((Invoke-RepositoryGit @('rev-parse','HEAD')) -cne $head -or (Invoke-RepositoryGit @('show-ref')) -cne $refs -or (Invoke-RepositoryGit @('status','--porcelain','--untracked-files=all'))) { throw 'Source changed during version preparation' }
if ((ToolPayloadStat $toolRoot) -cne $binding.toolPayloadStatSha256) { throw 'Vendor tool payload changed during version preparation' }
if ($Verify) {
    if ($record.version -cne $v.SemVer -or $record.vendorOutputSha256 -cne (Digest $raw) -or
        ($record.variables | ConvertTo-Json -Depth 15 -Compress) -cne ($v | ConvertTo-Json -Depth 15 -Compress) -or
        ($record.projections | ConvertTo-Json -Depth 15 -Compress) -cne ($projections | ConvertTo-Json -Depth 15 -Compress) -or
        $record.propsSha256 -cne (Digest $xml)) { throw 'Version metadata integrity mismatch with the pinned vendor output' }
    Write-Output "Verified $($record.version) at $head"
    exit 0
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
[IO.File]::WriteAllText($propsPath,$xml,[Text.UTF8Encoding]::new($false))
$record = [ordered]@{ schemaVersion=3; kind='icm-gitversion-build-input'; tool='GitVersion.Tool@6.8.2'; version=$v.SemVer; source=$binding; variables=$v; vendorOutputSha256=(Digest $raw); projections=$projections; propsSha256=(FileDigest $propsPath); releaseCertification=$false }
[IO.File]::WriteAllText($recordPath,($record | ConvertTo-Json -Depth 15)+"`n",[Text.UTF8Encoding]::new($false))
$record | ConvertTo-Json -Depth 15
