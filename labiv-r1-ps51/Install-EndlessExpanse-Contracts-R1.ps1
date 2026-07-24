#requires -version 5.1
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$GameRoot,

    [ValidateNotNullOrEmpty()]
    [string]$ModDirectory,

    [ValidateNotNullOrEmpty()]
    [string]$CandidateZip = (Join-Path $PSScriptRoot 'Package\EndlessExpanseFeatureHub_v0.12.0-CONTRACTS-R1-LIFECYCLE-AND-HEADLESS-SETTLEMENT.zip'),

    [ValidateNotNullOrEmpty()]
    [string]$CandidateSha256File = (Join-Path $PSScriptRoot 'Package\EndlessExpanseFeatureHub_v0.12.0-CONTRACTS-R1-LIFECYCLE-AND-HEADLESS-SETTLEMENT.zip.sha256')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-NormalizedFullPath {
    param([string]$Path)
    return [System.IO.Path]::GetFullPath($Path.Trim())
}

function Get-ExpectedSha256 {
    param([string]$SidecarPath)
    $line = (Get-Content -LiteralPath $SidecarPath -Encoding ASCII | Select-Object -First 1).Trim()
    if ($line -notmatch '^([0-9A-Fa-f]{64})(?:\s+.+)?$') {
        throw "Sidecar SHA-256 invalide : $SidecarPath"
    }
    return $Matches[1].ToUpperInvariant()
}

function Assert-Sha256 {
    param(
        [string]$Path,
        [string]$Expected
    )
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
    if ($actual -ne $Expected.ToUpperInvariant()) {
        throw "SHA-256 incorrect pour '$Path'. Attendu=$Expected Observe=$actual"
    }
    return $actual
}

function Assert-SafeZipEntries {
    param([string]$ZipPath)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if ([string]::IsNullOrWhiteSpace($name)) { throw 'Entree ZIP vide.' }
            if ([System.IO.Path]::IsPathRooted($name)) { throw "Entree ZIP absolue interdite : $name" }
            $normalized = $name.Replace('/', '\')
            $segments = $normalized.Split('\')
            if ($segments -contains '..') { throw "Traversal ZIP interdit : $name" }
            if ($name.Contains(':')) { throw "Chemin ZIP avec deux-points interdit : $name" }
            if ($name.EndsWith('/') -or $name.EndsWith('\')) { throw "Sous-dossier inattendu dans le package : $name" }
            if ($name.Contains('/') -or $name.Contains('\')) { throw "Le package doit contenir uniquement des fichiers racine : $name" }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-PackageManifest {
    param([string]$ExtractedRoot)
    $manifestPath = Join-Path $ExtractedRoot 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'SHA256SUMS.txt absent du package.'
    }
    $seen = @{}
    foreach ($line in Get-Content -LiteralPath $manifestPath -Encoding UTF8) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([0-9A-Fa-f]{64})  ([^\\/]+)$') {
            throw "Ligne de manifeste invalide : $line"
        }
        $expected = $Matches[1].ToUpperInvariant()
        $name = $Matches[2]
        if ($seen.ContainsKey($name)) { throw "Entree dupliquee dans le manifeste : $name" }
        $seen[$name] = $true
        $filePath = Join-Path $ExtractedRoot $name
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "Fichier manifeste absent : $name"
        }
        [void](Assert-Sha256 -Path $filePath -Expected $expected)
    }
    $required = @(
        'EndlessExpanseHudSpike.dll',
        'EndlessExpanseEepdmCore.dll',
        'EndlessExpanseEepdmRuntimeEngine.dll',
        'EndlessExpanseCodexProgressionEngine.dll',
        'EndlessExpanseCodexEngine.dll',
        'EndlessExpanseBarracksEngine.dll',
        'EndlessExpanseContractsEngine.dll',
        'EndlessExpanseContractsUIEngine.dll',
        'EndlessExpanseWikiEngine.dll',
        'EndlessExpanseFeatureHubEngine.dll',
        'EndlessExpanseSettingsEngine.dll',
        'Info.json',
        'README.txt'
    )
    foreach ($name in $required) {
        if (-not $seen.ContainsKey($name)) { throw "Fichier requis absent du manifeste : $name" }
    }
    $unexpected = Get-ChildItem -LiteralPath $ExtractedRoot -Force | Where-Object {
        -not $_.PSIsContainer -and $_.Name -ne 'SHA256SUMS.txt' -and -not $seen.ContainsKey($_.Name)
    }
    if ($unexpected) {
        throw ('Fichiers non manifestes : ' + (($unexpected | ForEach-Object Name) -join ', '))
    }
}

if ([string]::IsNullOrWhiteSpace($ModDirectory)) {
    if ([string]::IsNullOrWhiteSpace($GameRoot)) {
        throw 'Fournir -GameRoot ou -ModDirectory.'
    }
    $ModDirectory = Join-Path (Get-NormalizedFullPath -Path $GameRoot) 'Mods\EndlessExpanseHudSpike'
}

$target = Get-NormalizedFullPath -Path $ModDirectory
if ([System.IO.Path]::GetFileName($target) -ne 'EndlessExpanseHudSpike') {
    throw "Le dossier cible doit se nommer exactement 'EndlessExpanseHudSpike' : $target"
}
$parent = Split-Path -Parent $target
if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    throw "Dossier parent inexistant : $parent"
}

$candidate = Get-NormalizedFullPath -Path $CandidateZip
$sidecar = Get-NormalizedFullPath -Path $CandidateSha256File
if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Package candidat introuvable : $candidate" }
if (-not (Test-Path -LiteralPath $sidecar -PathType Leaf)) { throw "Sidecar SHA-256 introuvable : $sidecar" }
$expectedCandidate = Get-ExpectedSha256 -SidecarPath $sidecar
$observedCandidate = Assert-Sha256 -Path $candidate -Expected $expectedCandidate
Assert-SafeZipEntries -ZipPath $candidate

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path $parent ('.EndlessExpanseHudSpike.R1.stage.' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $parent ('.EndlessExpanseHudSpike.backup.' + $stamp)
$receipt = Join-Path $parent ('EndlessExpanseHudSpike.R1.install.' + $stamp + '.json')
$oldMoved = $false
$newMoved = $false

try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Expand-Archive -LiteralPath $candidate -DestinationPath $stage -Force
    Assert-PackageManifest -ExtractedRoot $stage

    if (Test-Path -LiteralPath $target) {
        if (Test-Path -LiteralPath $backup) { throw "Backup deja existant : $backup" }
        Move-Item -LiteralPath $target -Destination $backup
        $oldMoved = $true
    }

    Move-Item -LiteralPath $stage -Destination $target
    $newMoved = $true

    $info = Get-Content -LiteralPath (Join-Path $target 'Info.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($info.Version -ne '0.12.0-CONTRACTS-R1-LIFECYCLE-AND-HEADLESS-SETTLEMENT') {
        throw "Version installee inattendue : $($info.Version)"
    }

    $result = [ordered]@{
        status = 'INSTALLED'
        installedAt = (Get-Date).ToString('o')
        target = $target
        candidateZip = $candidate
        candidateSha256 = $observedCandidate
        backup = $(if ($oldMoved) { $backup } else { $null })
        version = $info.Version
        runtimeValidation = 'NON_TESTE'
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $receipt -Encoding UTF8
    Write-Host "INSTALLATION CONTRACTS R1 TERMINEE"
    Write-Host "Cible   : $target"
    Write-Host "Backup  : $(if ($oldMoved) { $backup } else { 'AUCUN DOSSIER PREEXISTANT' })"
    Write-Host "Receipt : $receipt"
}
catch {
    $installError = $_
    try {
        if ($newMoved -and (Test-Path -LiteralPath $target)) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
        elseif (Test-Path -LiteralPath $stage) {
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
        if ($oldMoved -and (Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) {
            Move-Item -LiteralPath $backup -Destination $target
        }
    }
    catch {
        throw "INSTALLATION ECHOUEE ET ROLLBACK AUTOMATIQUE ECHOUE. Installation=$($installError.Exception.Message) Rollback=$($_.Exception.Message)"
    }
    throw "INSTALLATION ECHOUEE - ROLLBACK AUTOMATIQUE EFFECTUE : $($installError.Exception.Message)"
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
