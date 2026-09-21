[CmdletBinding()]
param(
    [string]$FreeSpace,
    [string]$Output,
    [string]$AssetTool
)

$ErrorActionPreference = "Stop"
$modRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $modRoot "assets"
}

if ([string]::IsNullOrWhiteSpace($AssetTool)) {
    $AssetTool = Join-Path $modRoot "tools\FS2Hercules.AssetTool.exe"
}
if (-not (Test-Path -LiteralPath $AssetTool -PathType Leaf)) {
    $developerTool = Join-Path $modRoot "tools\assettool\target\release\fs2hercules-assettool.exe"
    if (Test-Path -LiteralPath $developerTool -PathType Leaf) {
        $AssetTool = $developerTool
    }
}
if (-not (Test-Path -LiteralPath $AssetTool -PathType Leaf)) {
    throw "The asset tool is missing. Use a release archive containing tools\FS2Hercules.AssetTool.exe, or build tools\assettool first."
}

if ([string]::IsNullOrWhiteSpace($FreeSpace)) {
    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    $programFiles = [Environment]::GetEnvironmentVariable("ProgramFiles")
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $candidates += Join-Path $programFilesX86 "Steam\steamapps\common\Freespace 2"
        $candidates += Join-Path $programFilesX86 "GOG Galaxy\Games\FreeSpace 2"
    }
    if (-not [string]::IsNullOrWhiteSpace($programFiles)) {
        $candidates += Join-Path $programFiles "Steam\steamapps\common\Freespace 2"
        $candidates += Join-Path $programFiles "GOG Galaxy\Games\FreeSpace 2"
    }
    $candidates = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
    $validCandidates = @(
        $candidates | Where-Object {
            @(Get-ChildItem -LiteralPath $_ -Filter "*.vp" -File -ErrorAction SilentlyContinue).Count -gt 0
        }
    )
    if ($validCandidates.Count -eq 1) {
        $FreeSpace = $validCandidates[0]
        Write-Host "Found FreeSpace 2 at $FreeSpace"
    }
}

if ([string]::IsNullOrWhiteSpace($FreeSpace)) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "Select the folder containing your original FreeSpace 2 installation"
    $dialog.UseDescriptionForTitle = $true
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-Host "No FreeSpace 2 folder selected. Nothing was changed."
        exit 1
    }
    $FreeSpace = $dialog.SelectedPath
}

if (-not (Test-Path -LiteralPath $FreeSpace -PathType Container) -and
    -not (Test-Path -LiteralPath $FreeSpace -PathType Leaf)) {
    throw "FreeSpace 2 path does not exist: $FreeSpace"
}

$vpFiles = if (Test-Path -LiteralPath $FreeSpace -PathType Leaf) {
    @($FreeSpace)
} else {
    @(Get-ChildItem -LiteralPath $FreeSpace -Filter "*.vp" -File -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
}
if ($vpFiles.Count -eq 0) {
    throw "No .vp archives were found there. Select the original FreeSpace 2 folder, not the Nuclear Option folder."
}

New-Item -ItemType Directory -Path $Output -Force | Out-Null
Write-Host "Generating Hercules assets in $Output"
& $AssetTool --freespace $FreeSpace --output $Output
if ($LASTEXITCODE -ne 0) {
    throw "The asset tool failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Done. Restart Nuclear Option to load the generated assets."
Write-Host "The original FreeSpace 2 files were read but not copied."
