#requires -version 5.1
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$GameRoot,

    [ValidateNotNullOrEmpty()]
    [string]$ModDirectory,

    [ValidateNotNullOrEmpty()]
    [string]$RollbackZip = (Join-Path $PSScriptRoot 'Rollback\EndlessExpanseFeatureHub_v0.11.0-EE-PDM-R0-D3-CODEX-PROGRESSION-VERTICAL.zip')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ExpectedRollbackSha256 = 'CACC9ADBCA13380A4AD04FEDF72BAB2ABE03F150B6D5DC166BA51082C7A6715A'

function Get-NormalizedFullPath {
    param([string]$Path)
    return [System.IO.Path]::GetFullPath($Path.Trim())
}

if ([string]::IsNullOrWhiteSpace($ModDirectory)) {
    if ([string]::IsNullOrWhiteSpace($GameRoot)) { throw 'Fournir -GameRoot ou -ModDirectory.' }
    $ModDirectory = Join-Path (Get-NormalizedFullPath -Path $GameRoot) 'Mods\EndlessExpanseHudSpike'
}
$target = Get-NormalizedFullPath -Path $ModDirectory
if ([System.IO.Path]::GetFileName($target) -ne 'EndlessExpanseHudSpike') {
    throw "Le dossier cible doit se nommer exactement 'EndlessExpanseHudSpike' : $target"
}
$parent = Split-Path -Parent $target
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw "Dossier parent inexistant : $parent" }
$rollback = Get-NormalizedFullPath -Path $RollbackZip
if (-not (Test-Path -LiteralPath $rollback -PathType Leaf)) { throw "Rollback D3 introuvable : $rollback" }
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $rollback).Hash.ToUpperInvariant()
if ($actual -ne $ExpectedRollbackSha256) { throw "SHA-256 rollback incorrect. Attendu=$ExpectedRollbackSha256 Observe=$actual" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($rollback)
try {
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName
        if ([System.IO.Path]::IsPathRooted($name) -or $name.Contains(':') -or $name.Contains('/') -or $name.Contains('\')) {
            throw "Entree ZIP D3 non sure : $name"
        }
    }
}
finally { $archive.Dispose() }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path $parent ('.EndlessExpanseHudSpike.D3.stage.' + [Guid]::NewGuid().ToString('N'))
$replaced = Join-Path $parent ('.EndlessExpanseHudSpike.replaced-by-D3.' + $stamp)
$moved = $false
try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Expand-Archive -LiteralPath $rollback -DestinationPath $stage -Force
    $info = Get-Content -LiteralPath (Join-Path $stage 'Info.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($info.Version -ne '0.11.0-EE-PDM-R0-D3-CODEX-PROGRESSION-VERTICAL') {
        throw "Version D3 inattendue : $($info.Version)"
    }
    if (Test-Path -LiteralPath $target) {
        Move-Item -LiteralPath $target -Destination $replaced
        $moved = $true
    }
    Move-Item -LiteralPath $stage -Destination $target
    Write-Host 'ROLLBACK D3 TERMINE'
    Write-Host "Cible remplacee : $target"
    Write-Host "Ancien dossier  : $(if ($moved) { $replaced } else { 'AUCUN' })"
}
catch {
    $rollbackError = $_
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    if ($moved -and -not (Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath $replaced)) {
        Move-Item -LiteralPath $replaced -Destination $target
    }
    throw "ROLLBACK D3 ECHOUE : $($rollbackError.Exception.Message)"
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
}
