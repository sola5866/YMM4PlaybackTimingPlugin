[CmdletBinding()]
param([string]$Configuration = 'Release', [string]$OutputRoot = '')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputRoot) { $OutputRoot = Join-Path $repository ('artifacts/' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw 'Package output must be a new directory.' }
$name = 'YMM4PlaybackTimingPlugin'
$output = Join-Path $repository "src/$name/bin/$Configuration/net10.0-windows10.0.19041.0"
if (-not(Test-Path -LiteralPath (Join-Path $output "$name.dll"))) { throw 'Build the solution before packaging.' }
$noticeName = 'THIRD-PARTY-NOTICES.txt'
$builtNotices = Join-Path $output $noticeName
$sourceNotices = Join-Path $repository $noticeName
if (-not (Test-Path -LiteralPath $builtNotices -PathType Leaf) -or
    (Get-FileHash -LiteralPath $builtNotices).Hash -ne (Get-FileHash -LiteralPath $sourceNotices).Hash) {
    throw 'Third-party notices are missing or outdated. Build the solution before packaging.'
}
$license = Join-Path $repository 'LICENSE'
if (-not (Test-Path -LiteralPath $license -PathType Leaf)) { throw 'LICENSE is missing from the repository.' }
$package = Join-Path $OutputRoot 'package'
New-Item -ItemType Directory -Path $package -Force | Out-Null
foreach ($file in @("$name.dll", $noticeName)) {
    Copy-Item -LiteralPath (Join-Path $output $file) -Destination $package
}
Copy-Item -LiteralPath (Join-Path $repository 'assets/README.md'), $license -Destination $package
$archive = Join-Path $OutputRoot "$name.ymme"
[IO.Compression.ZipFile]::CreateFromDirectory($package, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
[pscustomobject]@{Archive = $archive } | ConvertTo-Json
