param(
    [string]$GameDir,
    [switch]$Uninstall,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$finder = Join-Path $PSScriptRoot 'Find-RaftGame.ps1'

if ($GameDir) { $found = & $finder -GameDir $GameDir }
else { $found = & $finder }
if (-not $found) { exit 1 }
$gameRoot = [IO.Path]::GetFullPath($found)
$gamePrefix = $gameRoot.TrimEnd('\') + '\'
$manifestPath = Join-Path $gameRoot '.raft-save-editor-install.json'
$pluginRelative = 'BepInEx\plugins\RaftSaveEditor.Connector.dll'
$pluginSource = Join-Path $projectRoot 'RaftSaveEditor.Connector.dll'

function Get-Sha256([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Get-SafeTarget([string]$RelativePath) {
    $absolute = [IO.Path]::GetFullPath((Join-Path $gameRoot $RelativePath))
    if (-not $absolute.StartsWith($gamePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Caminho fora da pasta do Raft: $RelativePath"
    }
    return $absolute
}

$running = @(Get-Process -Name Raft -ErrorAction SilentlyContinue | Where-Object {
    try { [IO.Path]::GetFullPath((Split-Path -Parent $_.MainModule.FileName)) -eq $gameRoot }
    catch { $true }
})
if ($running.Count -gt 0) {
    throw 'Feche o Raft antes de instalar ou remover o conector. Reinicie o jogo depois da instalacao.'
}

if ($Uninstall) {
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        Write-Host 'Nenhuma instalacao deste instalador foi encontrada.'
        exit 0
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.GameRoot -ne $gameRoot) { throw 'Manifesto nao pertence a esta instalacao do Raft.' }
    $kept = @()
    foreach ($entry in $manifest.Files) {
        $target = Get-SafeTarget $entry.Path
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        if ((Get-Sha256 $target) -ne $entry.Sha256) {
            $kept += $entry.Path
            Write-Warning "Arquivo alterado; preservado: $($entry.Path)"
            continue
        }
        if ($WhatIf) { Write-Host "Removeria: $target" }
        else { Remove-Item -LiteralPath $target -Force }
    }
    if (-not $WhatIf) {
        if ($kept.Count -eq 0) { Remove-Item -LiteralPath $manifestPath -Force }
        else {
            $manifest.Files = @($manifest.Files | Where-Object { $_.Path -in $kept })
            $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        }
    }
    Write-Host 'Remocao concluida. Arquivos de terceiros preexistentes foram preservados.'
    exit 0
}

if (Test-Path -LiteralPath $manifestPath) {
    throw 'O conector ja esta instalado. Para atualizar, execute install-connector.bat -Uninstall e instale novamente.'
}

$loaderPaths = @('winhttp.dll', 'doorstop_config.ini', 'BepInEx\core\BepInEx.dll')
$present = @($loaderPaths | Where-Object { Test-Path -LiteralPath (Get-SafeTarget $_) })
if ($present.Count -gt 0 -and $present.Count -ne $loaderPaths.Count) {
    throw "Instalacao de loader parcial ou conflitante detectada ($($present -join ', ')). Nada foi alterado."
}
$useExistingLoader = $present.Count -eq $loaderPaths.Count
if (-not $useExistingLoader -and (Test-Path -LiteralPath (Get-SafeTarget 'BepInEx'))) {
    throw 'Pasta BepInEx parcial ou preexistente detectada. Nada foi alterado.'
}

if (-not (Test-Path -LiteralPath $pluginSource -PathType Leaf)) {
    if ($WhatIf) { throw 'Conector ainda nao compilado. Execute build.bat antes do modo -WhatIf.' }
    $env:RAFT_GAME_DIR = $gameRoot
    & (Join-Path $projectRoot 'build.bat')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $pluginSource)) {
        throw 'Nao foi possivel compilar o conector. Verifique o .NET 8 SDK.'
    }
}
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($pluginSource).Version
if ($assemblyVersion.Major -ne 5) { throw 'A versao do conector compilado nao corresponde ao protocolo esperado.' }

$stagingRoot = $null
try {
$files = [System.Collections.Generic.List[object]]::new()
if (-not $useExistingLoader) {
    $archive = & (Join-Path $PSScriptRoot 'Get-BepInEx.ps1') -KeepArchive
    if (-not $archive) { throw 'Nao foi possivel obter o BepInEx oficial.' }
    $stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ('RaftSaveEditor-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stagingRoot | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $stagingRoot)
    $packageRoot = $stagingRoot
    foreach ($source in Get-ChildItem -LiteralPath $packageRoot -Recurse -File) {
        $relative = $source.FullName.Substring($packageRoot.Length).TrimStart('\')
        $files.Add([pscustomobject]@{ Path = $relative; Source = $source.FullName })
    }
}
$files.Add([pscustomobject]@{ Path = $pluginRelative; Source = $pluginSource })

foreach ($entry in $files) {
    $target = Get-SafeTarget $entry.Path
    if (Test-Path -LiteralPath $target) {
        throw "Arquivo existente nao sera sobrescrito: $target"
    }
}

Write-Host "Raft detectado: $gameRoot"
Write-Host "Loader: $(if ($useExistingLoader) { 'BepInEx existente' } else { 'BepInEx oficial 5.4.23.5, SHA-256 verificado' })"
Write-Host "Arquivos a instalar: $($files.Count)"
if ($WhatIf) { return }

$created = [System.Collections.Generic.List[string]]::new()
try {
    $manifestFiles = @()
    foreach ($entry in $files) {
        $target = Get-SafeTarget $entry.Path
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $entry.Source -Destination $target
        $created.Add($target)
        $manifestFiles += [pscustomobject]@{ Path = $entry.Path; Sha256 = (Get-Sha256 $target) }
    }
    [pscustomobject]@{
        Format = 1
        GameRoot = $gameRoot
        InstalledAt = (Get-Date).ToString('o')
        Files = $manifestFiles
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
} catch {
    foreach ($target in $created) { Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue }
    throw
}

Write-Host 'Conector instalado. Abra/reinicie o Raft e carregue o mundo antes de usar Detectar jogo aberto.'
Write-Host 'Para remover: install-connector.bat -Uninstall'
} finally {
    if ($stagingRoot) {
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $resolvedStaging = [IO.Path]::GetFullPath($stagingRoot)
        if ($resolvedStaging.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $resolvedStaging)) {
            Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
        }
    }
}
