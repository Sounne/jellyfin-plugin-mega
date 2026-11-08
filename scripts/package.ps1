#Requires -Version 7
<#
.SYNOPSIS
    Builds the plugin for every supported Jellyfin version, zips each build and adds them to manifest.json.

.DESCRIPTION
    One zip per Jellyfin version goes to ./artifacts. manifest.json (the plugin repository that
    Jellyfin reads) gets one entry per zip, pointing at the GitHub release v<Version>.

    Publishing a version:
        pwsh scripts/package.ps1 -Version 1.0.1 -Changelog "What changed"
        gh release create v1.0.1 artifacts/*.zip --title "1.0.1" --notes "What changed"
        git add manifest.json; git commit -m "Release 1.0.1"; git push

.PARAMETER Version
    Plugin version, three numbers (1.0.1). The last number of the full version is the Jellyfin
    major version (1.0.1.12, 1.0.1.11) so that each server picks its own build.
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Changelog = '',
    [string]$Repository = 'Sounne/jellyfin-plugin-mega'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Jellyfin.Plugin.Mega/Jellyfin.Plugin.Mega.csproj'
$artifacts = Join-Path $root 'artifacts'
$manifestPath = Join-Path $root 'manifest.json'
$utf8 = [System.Text.UTF8Encoding]::new($false)

$targets = @(
    @{ Framework = 'net10.0'; TargetAbi = '12.0.0.0'; Suffix = '12' }
    @{ Framework = 'net9.0'; TargetAbi = '10.11.0.0'; Suffix = '11' }
)

$plugin = [ordered]@{
    guid = '61a62eb4-5383-489b-8150-3b595e6a6400'
    name = 'MEGA'
    description = 'Plays your MEGA cloud drive in Jellyfin. Files are decrypted on the fly (end-to-end encryption kept), libraries are virtual so nothing is downloaded ahead, playback starts fast and seeks with parallel connections, and MEGA previews are used as thumbnails. Works with MEGA accounts (e-mail, 2FA, or MEGAcmd session) and public folder links.'
    overview = 'Stream your MEGA cloud drive: decryption on the fly, virtual libraries, fast seeking'
    owner = 'Sounne'
    category = 'General'
}

$timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $artifacts | Out-Null

$newVersions = foreach ($t in $targets) {
    $full = "$Version.$($t.Suffix)"
    $stage = Join-Path $artifacts "stage-$($t.Framework)"
    Write-Host "Building $full for Jellyfin $($t.TargetAbi) ($($t.Framework))"
    dotnet publish $project -c Release -f $t.Framework -o $stage "-p:PluginVersion=$Version" --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $($t.Framework)" }

    # Jellyfin provides every dependency: the zip holds the plugin assembly and its meta.json only.
    Get-ChildItem $stage -Exclude 'Jellyfin.Plugin.Mega.dll' | Remove-Item -Recurse -Force
    $meta = [ordered]@{}
    $plugin.GetEnumerator() | ForEach-Object { $meta[$_.Key] = $_.Value }
    $meta.version = $full
    $meta.targetAbi = $t.TargetAbi
    $meta.changelog = $Changelog
    $meta.timestamp = $timestamp
    [IO.File]::WriteAllText((Join-Path $stage 'meta.json'), ($meta | ConvertTo-Json), $utf8)

    $zipName = "jellyfin-plugin-mega_$full.zip"
    $zip = Join-Path $artifacts $zipName
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Remove-Item $stage -Recurse -Force

    [ordered]@{
        version = $full
        changelog = $Changelog
        targetAbi = $t.TargetAbi
        sourceUrl = "https://github.com/$Repository/releases/download/v$Version/$zipName"
        checksum = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()
        timestamp = $timestamp
    }
}

# Keep the versions already published, replace the ones being rebuilt.
$oldVersions = @()
if (Test-Path $manifestPath) {
    $existing = Get-Content $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $entry = $existing | Where-Object { $_.guid -eq $plugin.guid } | Select-Object -First 1
    if ($entry) {
        $rebuilt = $newVersions | ForEach-Object { $_.version }
        $oldVersions = @($entry.versions | Where-Object { $rebuilt -notcontains $_.version })
    }
}

$all = @($newVersions) + $oldVersions | Sort-Object { [Version]$_.version } -Descending
$manifestEntry = [ordered]@{}
$plugin.GetEnumerator() | ForEach-Object { $manifestEntry[$_.Key] = $_.Value }
$manifestEntry.versions = @($all)
[IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject @($manifestEntry) -Depth 10) + "`n", $utf8)

Get-ChildItem $artifacts -Filter *.zip | ForEach-Object { Write-Host "  $($_.Name)  $([math]::Round($_.Length / 1KB)) KB" }
Write-Host "manifest.json: $($all.Count) version(s)"
