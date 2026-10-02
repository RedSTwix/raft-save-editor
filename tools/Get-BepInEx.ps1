param([switch]$KeepArchive)

$ErrorActionPreference = 'Stop'
$version = '5.4.23.5'
$fileName = "BepInEx_win_x64_$version.zip"
$expectedHash = '82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4'
$expectedCoreHash = '8255b28902886085c578b9e427d3073c97002db85176d2090cdeda90ef14ce70'
$cacheDir = Join-Path $PSScriptRoot '..\.deps\bepinex-download'
$archive = Join-Path $cacheDir $fileName
$url = "https://github.com/BepInEx/BepInEx/releases/download/v$version/$fileName"

function Get-Sha256([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}

New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
if (Test-Path -LiteralPath $archive) {
    $actualHash = Get-Sha256 $archive
    if ($actualHash -ne $expectedHash) {
        throw "Arquivo BepInEx em cache com hash incorreto: $archive"
    }
} else {
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    $actualHash = Get-Sha256 $archive
    if ($actualHash -ne $expectedHash) {
        Remove-Item -LiteralPath $archive -Force
        throw 'Download do BepInEx falhou na verificacao SHA-256.'
    }
}

$coreDir = Join-Path $cacheDir 'BepInEx\core'
$coreDll = Join-Path $coreDir 'BepInEx.dll'
if (-not (Test-Path -LiteralPath $coreDll)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = $zip.GetEntry('BepInEx/core/BepInEx.dll')
        if (-not $entry) { throw 'BepInEx.dll nao foi encontrado no arquivo oficial.' }
        New-Item -ItemType Directory -Path $coreDir -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $coreDll, $true)
    } finally { $zip.Dispose() }
}
if (-not (Test-Path -LiteralPath $coreDll)) { throw 'BepInEx.dll nao foi encontrado no pacote oficial.' }
if ((Get-Sha256 $coreDll) -ne $expectedCoreHash) { throw 'BepInEx.dll extraido nao corresponde ao pacote oficial.' }

if ($KeepArchive) { Write-Output $archive }
else { Write-Output $coreDll }
