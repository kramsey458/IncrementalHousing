$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskRoot = $PSScriptRoot
$outDir = Join-Path $taskRoot 'dist'
New-Item -ItemType Directory -Force $outDir | Out-Null
$modDir = Join-Path $PSScriptRoot 'IncrementalHousing'
$testDir = Join-Path $PSScriptRoot 'IncrementalHousing.Tests'
function Write-Archive($target, $entries) {
    $zip = [System.IO.Compression.ZipFile]::Open($target, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $entries.GetEnumerator()) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entry.Value, $entry.Key, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($target)
    try {
        if ($zip.Entries.Count -ne $entries.Count) { throw 'Archive entry count mismatch' }
        foreach ($entry in $zip.Entries) {
            $stream = $entry.Open()
            $hash = [System.Security.Cryptography.SHA256]::Create()
            try { $actual = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $hash.Dispose() }
            $expected = (Get-FileHash -LiteralPath $entries[$entry.FullName] -Algorithm SHA256).Hash
            if ($actual -ne $expected) { throw "Archive content mismatch: $($entry.FullName)" }
        }
    } finally { $zip.Dispose() }
    Write-Output "Verified $($entries.Count) entries: $target"
}
$payload = [ordered]@{
    'IncrementalHousing-Preview/version-1.1/Scripts/IncrementalHousing.dll' = (Join-Path $modDir 'bin/Release/netstandard2.1/IncrementalHousing.dll')
    'IncrementalHousing-Preview/version-1.1/manifest.json' = (Join-Path $modDir 'manifest.json')
    'IncrementalHousing-Preview/README.md' = (Join-Path $modDir 'README.md')
    'IncrementalHousing-Preview/LICENSE' = (Join-Path $modDir 'LICENSE')
}
Write-Archive (Join-Path $outDir 'IncrementalHousing-preview2.zip') $payload
$sources = [ordered]@{}
foreach ($directory in @($modDir, $testDir)) {
    foreach ($file in Get-ChildItem -LiteralPath $directory -File | Sort-Object Name) {
        $name = (Split-Path -Leaf $directory) + '/' + $file.Name
        $sources[$name] = $file.FullName
    }
}
$sources['package.ps1'] = $PSCommandPath
Write-Archive (Join-Path $outDir 'IncrementalHousing-preview2-source.zip') $sources
Copy-Item -LiteralPath (Join-Path $modDir 'README.md') -Destination (Join-Path $outDir 'IncrementalHousing-preview2-notes.md')
$checksums = foreach ($file in Get-ChildItem -LiteralPath $outDir -Filter 'IncrementalHousing-preview2*.zip' | Sort-Object Name) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $file.Name
}
$checksums | Set-Content -LiteralPath (Join-Path $outDir 'IncrementalHousing-preview2-SHA256SUMS.txt')
$checksums


