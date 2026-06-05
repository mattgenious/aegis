param(
    [string]$SourceRoot,
    [string]$TargetRoot,
    [string]$ProfileRoot,
    [string]$WorkspaceRoot,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

if (-not $SourceRoot) {
    $SourceRoot = Join-Path $repoRoot "support\vscode"
}

if (-not $TargetRoot -and $ProfileRoot) {
    $TargetRoot = $ProfileRoot
}

if (-not $TargetRoot) {
    $TargetRoot = Join-Path $HOME ".copilot"
}

if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
    throw "VS Code support source not found: $SourceRoot"
}

$sourceRootFull = [System.IO.Path]::GetFullPath($SourceRoot)
$sets = @("agents", "instructions", "prompts")

function Write-InstallLine {
    param([string]$Message)

    if ($DryRun) {
        Write-Host "DRY RUN: $Message"
    } else {
        Write-Host $Message
    }
}

function Install-VSCodeSupportSet {
    param(
        [string]$SetName,
        [string]$DestinationRoot,
        [string]$Label
    )

    $sourceDir = Join-Path $sourceRootFull $SetName
    if (-not (Test-Path -LiteralPath $sourceDir -PathType Container)) {
        throw "Required VS Code support directory not found: $sourceDir"
    }

    $targetDir = Join-Path $DestinationRoot $SetName
    if ($DryRun) {
        Write-InstallLine "would create directory -> $targetDir"
    } else {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    }

    $files = Get-ChildItem -LiteralPath $sourceDir -File
    if ($files.Count -eq 0) {
        throw "No files found in required VS Code support directory: $sourceDir"
    }

    foreach ($file in $files) {
        $targetFile = Join-Path $targetDir $file.Name
        if ($DryRun) {
            Write-InstallLine "would copy $($file.FullName) -> $targetFile"
            continue
        }

        Copy-Item -LiteralPath $file.FullName -Destination $targetFile -Force
        Write-Host "Installed $Label $SetName/$($file.Name)"
    }
}

Write-Host "Aegis VS Code support install"
Write-Host "Source: $sourceRootFull"
Write-Host "Profile target: $([System.IO.Path]::GetFullPath($TargetRoot))"
if ($WorkspaceRoot) {
    Write-Host "Workspace target: $([System.IO.Path]::GetFullPath($WorkspaceRoot))"
}

foreach ($set in $sets) {
    Install-VSCodeSupportSet -SetName $set -DestinationRoot ([System.IO.Path]::GetFullPath($TargetRoot)) -Label "profile"
}

if ($WorkspaceRoot) {
    $workspaceGithubRoot = Join-Path ([System.IO.Path]::GetFullPath($WorkspaceRoot)) ".github"
    foreach ($set in $sets) {
        Install-VSCodeSupportSet -SetName $set -DestinationRoot $workspaceGithubRoot -Label "workspace"
    }
}

if ($DryRun) {
    Write-Host "Dry run complete."
} else {
    Write-Host "VS Code support install complete. Restart VS Code if the new agent or prompt is not visible."
}
