[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$GameDir
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$stageRoot = Join-Path $root "artifacts\FS2Hercules-$Version-release"
$payload = Join-Path $stageRoot "FS2Hercules"
$archive = Join-Path $root "artifacts\FS2Hercules-$Version.zip"

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    $GameDir = $env:NuclearOptionGameDir
}
if ([string]::IsNullOrWhiteSpace($GameDir) -or
    -not (Test-Path -LiteralPath (Join-Path $GameDir "NuclearOption_Data\Managed\Assembly-CSharp.dll") -PathType Leaf)) {
    throw "Pass -GameDir PATH or set NuclearOptionGameDir to the Nuclear Option install folder."
}

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}

dotnet build -c Release "-p:GameDir=$GameDir"
$oldRustFlags = $env:RUSTFLAGS
try {
    $env:RUSTFLAGS = "-C target-feature=+crt-static"
    cargo build --release --manifest-path (Join-Path $root "tools\assettool\Cargo.toml")
} finally {
    $env:RUSTFLAGS = $oldRustFlags
}

New-Item -ItemType Directory -Path $payload -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $payload "tools") -Force | Out-Null
Copy-Item (Join-Path $root "bin\Release\net472\FS2Hercules.dll") $payload
Copy-Item (Join-Path $root "Extract-FS2Hercules.cmd") $payload
Copy-Item (Join-Path $root "Extract-FS2Hercules.ps1") $payload
Copy-Item (Join-Path $root "README.md") $payload
Copy-Item (Join-Path $root "LICENSE") $payload
Copy-Item (Join-Path $root "THIRD_PARTY_NOTICES.md") $payload
Copy-Item (Join-Path $root "tools\assettool\target\release\fs2hercules-assettool.exe") (Join-Path $payload "tools\FS2Hercules.AssetTool.exe")

Compress-Archive -Path $payload -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash
Write-Host "Created $archive"
Write-Host "SHA-256 $hash"
Write-Host "The archive intentionally contains no generated FreeSpace 2-derived assets."
