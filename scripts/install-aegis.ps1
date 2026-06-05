param(
    [string]$AegisProject,
    [string]$InstallRoot,
    [int]$KeepVersions = 1,
    [switch]$NoPathUpdate,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

if (-not $AegisProject) {
    $AegisProject = Join-Path $repoRoot "src\Aegis\Aegis.csproj"
}

if (-not (Test-Path -LiteralPath $AegisProject -PathType Leaf)) {
    throw "Aegis project not found: $AegisProject"
}

if (-not $InstallRoot) {
    $InstallRoot = Join-Path $HOME ".local\aegis"
}

if ($KeepVersions -lt 1) {
    throw "KeepVersions must be at least 1."
}

$installRootFull = [System.IO.Path]::GetFullPath($InstallRoot)
$versionsRoot = Join-Path $installRootFull "versions"
$binRoot = Join-Path $installRootFull "bin"
$version = Get-Date -Format "yyyyMMddHHmmss"
$versionRoot = Join-Path $versionsRoot $version
$activeMarker = Join-Path $installRootFull ".active"
$runningOnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)

function Write-InstallLine {
    param([string]$Message)

    if ($DryRun) {
        Write-Host "DRY RUN: $Message"
    } else {
        Write-Host $Message
    }
}

function Write-WindowsShim {
    param(
        [string]$Path,
        [string]$ExePath
    )

    $lines = @(
        "@echo off",
        "`"$ExePath`" %*"
    )

    if ($DryRun) {
        Write-InstallLine "would write shim -> $Path"
        return
    }

    Set-Content -LiteralPath $Path -Value $lines -Encoding ASCII
}

function Write-PosixShim {
    param(
        [string]$Path,
        [string]$ExePath
    )

    $lines = @(
        "#!/usr/bin/env sh",
        "exec `"$ExePath`" `"$@`""
    )

    if ($DryRun) {
        Write-InstallLine "would write shim -> $Path"
        return
    }

    Set-Content -LiteralPath $Path -Value $lines -Encoding ASCII
    & chmod +x $Path
}

function Ensure-UserPath {
    param([string]$Path)

    if (-not $runningOnWindows -or $NoPathUpdate) {
        return
    }

    $current = [Environment]::GetEnvironmentVariable("PATH", "User")
    $entries = @()
    if ($current) {
        $entries = $current -split ';' | Where-Object { $_ }
    }

    foreach ($entry in $entries) {
        try {
            if ([System.IO.Path]::GetFullPath($entry) -ieq $Path) {
                Write-Host "PATH already contains $Path"
                return
            }
        } catch {
            if ($entry -ieq $Path) {
                Write-Host "PATH already contains $Path"
                return
            }
        }
    }

    if ($DryRun) {
        Write-InstallLine "would add $Path to user PATH"
        return
    }

    $newPath = if ($current) { "$current;$Path" } else { $Path }
    [Environment]::SetEnvironmentVariable("PATH", $newPath, "User")
    Write-Host "Added $Path to user PATH. Open a new terminal for PATH refresh."
}

Write-Host "Aegis standalone install"
Write-Host "Project: $AegisProject"
Write-Host "Install root: $installRootFull"

if ($DryRun) {
    Write-InstallLine "would publish Aegis -> $versionRoot"
} else {
    New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null
    dotnet publish $AegisProject -c Release -o $versionRoot --self-contained false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed."
    }
}

if (-not $DryRun) {
    New-Item -ItemType Directory -Path $binRoot -Force | Out-Null
}

if ($runningOnWindows) {
    $entryPoint = Join-Path $versionRoot "aegis.exe"
    Write-WindowsShim -Path (Join-Path $binRoot "aegis.cmd") -ExePath $entryPoint
    Write-WindowsShim -Path (Join-Path $binRoot "opencode-aegis.cmd") -ExePath $entryPoint
    Write-WindowsShim -Path (Join-Path $binRoot "harness-cli.cmd") -ExePath $entryPoint
    Write-WindowsShim -Path (Join-Path $binRoot "opencode-harness-cli.cmd") -ExePath $entryPoint
} else {
    $entryPoint = Join-Path $versionRoot "aegis"
    Write-PosixShim -Path (Join-Path $binRoot "aegis") -ExePath $entryPoint
    Write-PosixShim -Path (Join-Path $binRoot "opencode-aegis") -ExePath $entryPoint
    Write-PosixShim -Path (Join-Path $binRoot "harness-cli") -ExePath $entryPoint
    Write-PosixShim -Path (Join-Path $binRoot "opencode-harness-cli") -ExePath $entryPoint
}

if ($DryRun) {
    Write-InstallLine "would update active marker -> $activeMarker"
} else {
    Set-Content -LiteralPath $activeMarker -Value $version -Encoding ASCII
}

if (Test-Path -LiteralPath $versionsRoot -PathType Container) {
    $versions = Get-ChildItem -LiteralPath $versionsRoot -Directory |
        Sort-Object Name -Descending
    $oldVersions = $versions | Select-Object -Skip $KeepVersions
    foreach ($oldVersion in $oldVersions) {
        if ($DryRun) {
            Write-InstallLine "would remove old version -> $($oldVersion.FullName)"
            continue
        }

        try {
            Remove-Item -LiteralPath $oldVersion.FullName -Recurse -Force
            Write-Host "Removed old version -> $($oldVersion.FullName)"
        } catch {
            Write-Warning "Could not remove old version directory $($oldVersion.FullName). It may be locked by a running process."
        }
    }
}

Ensure-UserPath $binRoot

if ($DryRun) {
    Write-Host "Dry run complete."
} else {
    Write-Host "Installed entry point -> $entryPoint"
    Write-Host "Installed primary shim -> $binRoot"
    Write-Host "Install complete. Open a new terminal if aegis is not found on PATH."
}
