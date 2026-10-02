param(
    [Parameter(Mandatory = $true)]
    [string]$LocalizedPath,
    [Parameter(Mandatory = $true)]
    [string]$MetadataPath
)

$ErrorActionPreference = 'Stop'
$localized = @{}
foreach ($line in [IO.File]::ReadAllLines($LocalizedPath)) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
    $fields = $line.Split('|')
    if ($fields.Length -lt 4) { throw "Linha localizada inválida: $line" }
    $localized[[int]$fields[0]] = $fields[1]
}

$result = [Collections.Generic.List[string]]::new()
$updated = 0
foreach ($line in [IO.File]::ReadAllLines($MetadataPath)) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) {
        if ($line -eq '# Generated from Raft Wiki ItemInfobox data; 0 means unknown/unverified.') {
            $result.Add('# Names extracted from the installed Raft localization: pt-BR; other fields retain the verified game/wiki metadata.')
        } else {
            $result.Add($line)
        }
        continue
    }

    $fields = $line.Split('|')
    if ($fields.Length -lt 6) { throw "Linha de metadados inválida: $line" }
    $id = [int]$fields[0]
    if (-not $localized.ContainsKey($id)) { throw "Nome oficial ausente para o Item ID $id" }
    $fields[1] = $localized[$id]
    $result.Add(($fields -join '|'))
    $updated++
}

if ($updated -ne $localized.Count) {
    throw "Contagem divergente: $updated metadados e $($localized.Count) nomes localizados."
}

[IO.File]::WriteAllLines($MetadataPath, $result, [Text.UTF8Encoding]::new($false))
Write-Output "$updated localized names applied to the catalog."
