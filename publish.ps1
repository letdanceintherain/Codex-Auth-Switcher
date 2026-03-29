[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$NoInstaller,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionPath = Join-Path $scriptRoot "CodexAuthSwitcher.sln"
$appProjectPath = Join-Path $scriptRoot "src\CodexAuthSwitcher.App\CodexAuthSwitcher.App.csproj"
$testsProjectPath = Join-Path $scriptRoot "tests\CodexAuthSwitcher.Core.Tests\CodexAuthSwitcher.Core.Tests.csproj"
$artifactsRoot = Join-Path $scriptRoot "artifacts"
$publishDir = Join-Path $artifactsRoot "publish\win-x64"
$installerDir = Join-Path $artifactsRoot "installer"
$installerScript = Join-Path $scriptRoot "installer\CodexAuthSwitcher.iss"

function Get-ProjectVersion {
    param([string]$ProjectPath)

    $content = Get-Content -LiteralPath $ProjectPath -Raw
    if ($content -match '<Version>([^<]+)</Version>') {
        return $Matches[1].Trim()
    }

    return "1.0.0"
}

function Resolve-InnoSetupCompiler {
    $candidates = @()
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        $candidates += $cmd.Source
    }

    $programFilesX86 = ${env:ProgramFiles(x86)}
    $programFiles = $env:ProgramFiles
    $localPrograms = Join-Path $env:LOCALAPPDATA "Programs"
    if ($programFilesX86) {
        $candidates += (Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe")
    }
    if ($programFiles) {
        $candidates += (Join-Path $programFiles "Inno Setup 6\ISCC.exe")
    }
    if ($localPrograms) {
        $candidates += (Join-Path $localPrograms "Inno Setup 6\ISCC.exe")
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    throw "Unable to find Inno Setup Compiler (ISCC.exe). Install Inno Setup 6 or add ISCC.exe to PATH."
}

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

if ($Clean) {
    if (Test-Path -LiteralPath $artifactsRoot) {
        Remove-Item -LiteralPath $artifactsRoot -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

Write-Host "Restoring solution..."
Invoke-DotNet @("restore", $solutionPath)

if (-not $SkipTests) {
    Write-Host "Running tests..."
    Invoke-DotNet @("test", $testsProjectPath, "-c", $Configuration, "--no-restore")
}

Write-Host "Publishing app..."
Invoke-DotNet @(
    "publish",
    $appProjectPath,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false",
    "-o", $publishDir,
    "--no-restore"
)

$appVersion = Get-ProjectVersion -ProjectPath $appProjectPath

if (-not $NoInstaller) {
    $isccPath = Resolve-InnoSetupCompiler
    Write-Host "Building installer..."
    $defines = @(
        "/DAppVersion=$appVersion",
        "/DPublishDir=$publishDir",
        "/DOutputDir=$installerDir"
    )

    & $isccPath $defines $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
    }
}

Write-Host ""
Write-Host "Build complete."
Write-Host "Publish output: $publishDir"
if (-not $NoInstaller) {
    Write-Host "Installer output: $installerDir"
}
