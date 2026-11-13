param([ValidateSet('0', '1')][string]$SingleFile = '1')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo 'artifacts/release'
$zip = Join-Path $release 'PlcSim.Slmp-win-x64.zip'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$entries = @('README.md', 'LICENSE', 'docs/CONFIGURATION.md')
# Select source-defined content so stale files in the release folder are never distributed.
foreach ($directory in @('config', 'python', 'scripts')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repo $directory) -File -Recurse) {
        $relative = $file.FullName.Substring($repo.Length + 1).Replace('\', '/')
        if ($relative -notmatch '(^|/)__pycache__/|\.(pyc|pdb)$') { $entries += $relative }
    }
}
if ($SingleFile -eq '1') {
    $entries += @('PlcSim.Slmp.Gui.exe', 'PlcSim.Slmp.Host.exe')
} else {
    $entries += @('gui', 'host', 'start_gui.bat', 'start_console.bat')
}
# Resolve required inputs before replacing a prior archive.
$files = foreach ($entry in $entries) {
    $item = Get-Item -LiteralPath (Join-Path $release $entry)
    if ($item.PSIsContainer) { Get-ChildItem -LiteralPath $item.FullName -File -Recurse } else { $item }
}
$stream = [IO.File]::Open($zip, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($release.Length + 1).Replace('\', '/')
        if ($relative -match '(^|/)__pycache__/|\.(pyc|pdb)$') { continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "PlcSim.Slmp/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
$line = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($zip)))).Replace('-', '').ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zip) + [char]10
[IO.File]::WriteAllText($zip + '.sha256', $line, [Text.UTF8Encoding]::new($false))
