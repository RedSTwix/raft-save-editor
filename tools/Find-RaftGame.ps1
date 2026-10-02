param(
    [string]$GameDir = $env:RAFT_GAME_DIR
)

$ErrorActionPreference = 'Stop'
$candidates = [System.Collections.Generic.List[string]]::new()

function Add-Candidate([string]$Path) {
    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        $candidates.Add($Path.Trim('"'))
    }
}

Add-Candidate $GameDir

Get-Process -Name Raft -ErrorAction SilentlyContinue | ForEach-Object {
    try { Add-Candidate (Split-Path -Parent $_.MainModule.FileName) } catch { }
}

$steamRoots = [System.Collections.Generic.List[string]]::new()
foreach ($registryPath in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
    $entry = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
    if ($entry) {
        if ($entry.SteamPath) { $steamRoots.Add($entry.SteamPath) }
        if ($entry.InstallPath) { $steamRoots.Add($entry.InstallPath) }
    }
}
foreach ($root in @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam", 'C:\Steam')) {
    if (Test-Path -LiteralPath $root) { $steamRoots.Add($root) }
}

foreach ($steamRoot in $steamRoots | Select-Object -Unique) {
    $libraries = [System.Collections.Generic.List[string]]::new()
    $libraries.Add($steamRoot)
    $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        foreach ($line in Get-Content -LiteralPath $vdf -ErrorAction SilentlyContinue) {
            if ($line -match '^\s*"path"\s+"([^"]+)"') {
                $libraries.Add($Matches[1].Replace('\\', '\'))
            }
        }
    }
    foreach ($library in $libraries | Select-Object -Unique) {
        Add-Candidate (Join-Path $library 'steamapps\common\Raft')
    }
}

foreach ($base in @((Join-Path $env:USERPROFILE 'Downloads'), (Join-Path $env:USERPROFILE 'Desktop'))) {
    if (-not (Test-Path -LiteralPath $base)) { continue }
    Get-ChildItem -LiteralPath $base -Filter Raft.exe -File -Recurse -Depth 4 -ErrorAction SilentlyContinue |
        ForEach-Object { Add-Candidate $_.DirectoryName }
}

$valid = @($candidates | Select-Object -Unique | Where-Object {
    (Test-Path -LiteralPath (Join-Path $_ 'Raft.exe') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $_ 'Raft_Data\Managed\UnityEngine.dll') -PathType Leaf)
})

if ($valid.Count -eq 0) {
    [Console]::Error.WriteLine('Raft nao encontrado. Use -GameDir ou defina RAFT_GAME_DIR.')
    exit 1
}

if ($valid.Count -gt 1 -and -not $GameDir) {
    [Console]::Error.WriteLine('Mais de uma instalacao do Raft foi encontrada:')
    foreach ($candidate in $valid) { [Console]::Error.WriteLine("  $candidate") }
    [Console]::Error.WriteLine('Use -GameDir para selecionar a instalacao correta.')
    exit 2
}

Write-Output ([IO.Path]::GetFullPath($valid[0]))
