param(
    [string]$Api = 'https://raft.fandom.com/api.php',
    [string]$CatalogPath = "$PSScriptRoot\..\src\RaftSaveEditor.Core\Catalog\items.metadata.txt",
    [string]$IconsPath = "$PSScriptRoot\..\src\RaftSaveEditor.App\Assets\Items"
)

$ErrorActionPreference = 'Stop'
$headers = @{ 'User-Agent' = 'RaftSaveEditorCatalog/1.0 (local catalog builder)' }

function Invoke-WikiApi([hashtable]$Body) {
    $Body.format = 'json'
    $Body.formatversion = '2'
    Invoke-RestMethod -Uri $Api -Method Post -Body $Body -Headers $headers
}

function Get-Field([string]$Text, [string]$Name) {
    $match = [regex]::Match($Text, "(?im)^\|\s*$([regex]::Escape($Name))\s*=\s*(.*?)\s*$")
    if ($match.Success) { return $match.Groups[1].Value.Trim() }
    return ''
}

function Normalize-Name([string]$Name) {
    return ([regex]::Replace($Name.ToLowerInvariant(), '[^a-z0-9]', ''))
}

function Safe-FileName([string]$Name) {
    $safe = [regex]::Replace($Name, '[^A-Za-z0-9._-]+', '_').Trim('_')
    if ([string]::IsNullOrWhiteSpace($safe)) { return 'item' }
    return $safe
}

$linksResponse = Invoke-WikiApi @{ action = 'parse'; page = 'Items'; prop = 'links' }
$supplementalTitles = @(
    'Wooden Fishing Rod', 'Pilot Helmet', 'Grass Seed', 'Beach Ball', 'Name Tag',
    'Cup of Fresh Water', 'Cup of Salt Water', 'Bottle of Fresh Water', 'Bottle of Salt Water',
    'Fruit Compot', 'Recipe: Fruit Compot', 'Compass', 'Blueprint: Recycler', 'Mystery Package',
    'Recipe: Simple Smoothie', 'Recipe: Coconut Beat', 'Recipe: Mangonana', 'Trade Coin',
    'Large Storage', 'Water Pipe'
)
$titles = @(($linksResponse.parse.links | Where-Object { $_.ns -eq 0 } | ForEach-Object { $_.title }) + $supplementalTitles | Sort-Object -Unique)
$wikiItems = [System.Collections.Generic.List[object]]::new()

for ($offset = 0; $offset -lt $titles.Count; $offset += 40) {
    $last = [Math]::Min($offset + 39, $titles.Count - 1)
    $batch = $titles[$offset..$last] -join '|'
    $response = Invoke-WikiApi @{
        action = 'query'; prop = 'revisions'; rvprop = 'content'; rvslots = 'main'; redirects = '1'; titles = $batch
    }

    foreach ($page in $response.query.pages) {
        if ($null -eq $page.revisions -or $page.revisions.Count -eq 0) { continue }
        $text = $page.revisions[0].slots.main.content
        if ([string]::IsNullOrWhiteSpace($text) -or $text -notmatch '\{\{ItemInfobox') { continue }
        $name = Get-Field $text 'name'
        if ([string]::IsNullOrWhiteSpace($name)) { $name = $page.title }
        $image = Get-Field $text 'image'
        $type = Get-Field $text 'type'
        $stackText = Get-Field $text 'stack'
        $usesText = Get-Field $text 'uses'
        [int]$stack = 0
        [int]$uses = 0
        [void][int]::TryParse(([regex]::Match($stackText, '\d+').Value), [ref]$stack)
        [void][int]::TryParse(([regex]::Match($usesText, '\d+').Value), [ref]$uses)
        if ($uses -eq 0 -and $stack -gt 0) { $uses = 1 }
        $wikiItems.Add([pscustomobject]@{
            PageTitle = $page.title; Name = $name; Normalized = Normalize-Name $name; Type = $type; Stack = $stack; Uses = $uses
            Image = $image; Icon = ''
        })
    }
}

# Some Raft Wiki ItemInfobox templates leave image= empty even though the page
# contains the correct item image. Query the page images and choose the file
# whose name most closely matches the item name (including Recipe: images).
$missingImageItems = @($wikiItems)
for ($offset = 0; $offset -lt $missingImageItems.Count; $offset += 20) {
    $last = [Math]::Min($offset + 19, $missingImageItems.Count - 1)
    $batchItems = @($missingImageItems[$offset..$last])
    $pageTitles = ($batchItems | ForEach-Object { $_.PageTitle }) -join '|'
    $response = Invoke-WikiApi @{
        action = 'query'; prop = 'images'; imlimit = 'max'; redirects = '1'; titles = $pageTitles
    }

    $itemsByPage = @{}
    foreach ($item in $batchItems) { $itemsByPage[$item.PageTitle] = $item }
    foreach ($page in $response.query.pages) {
        $item = $itemsByPage[$page.title]
        if ($null -eq $item -or $null -eq $page.images) { continue }

        $bestName = ''
        $bestScore = 0
        foreach ($image in $page.images) {
            $fileName = $image.title -replace '^File:', ''
            if ($fileName -notmatch '(?i)\.(png|webp|jpe?g)$') { continue }
            if ($fileName -match '(?i)(logo|icon|gallery|banner|holding|rouhi|concept|trailer|steam)') { continue }
            $baseName = [System.IO.Path]::GetFileNameWithoutExtension($fileName)
            $normalizedImage = Normalize-Name $baseName
            $score = 0
            if ($normalizedImage -eq $item.Normalized) { $score = 100 }
            elseif ($normalizedImage.EndsWith($item.Normalized)) { $score = 80 }
            elseif ($normalizedImage.Contains($item.Normalized)) { $score = 60 }
            if ($score -gt $bestScore) {
                $bestScore = $score
                $bestName = $fileName
            }
        }
        if ($bestScore -gt 0) { $item.Image = $bestName }
    }
}

New-Item -ItemType Directory -Path $IconsPath -Force | Out-Null
$imageItems = @($wikiItems | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Image) })
for ($offset = 0; $offset -lt $imageItems.Count; $offset += 40) {
    $last = [Math]::Min($offset + 39, $imageItems.Count - 1)
    $batchItems = @($imageItems[$offset..$last])
    $fileTitles = ($batchItems | ForEach-Object { "File:$($_.Image)" }) -join '|'
    $response = Invoke-WikiApi @{
        action = 'query'; prop = 'imageinfo'; iiprop = 'url'; iiurlwidth = '96'; iiurlheight = '96'; titles = $fileTitles
    }
    $urls = @{}
    foreach ($page in $response.query.pages) {
        $key = $page.title -replace '^File:', ''
        if ($page.imageinfo.Count -gt 0) {
            $info = $page.imageinfo[0]
            $urls[$key] = if ($info.thumburl) { $info.thumburl } else { $info.url }
        }
    }
    foreach ($item in $batchItems) {
        if (-not $urls.ContainsKey($item.Image)) { continue }
        $fileName = (Safe-FileName $item.Name) + '.png'
        $target = Join-Path $IconsPath $fileName
        if (Test-Path -LiteralPath $target) {
            $item.Icon = $fileName
            continue
        }
        $downloadUrl = $urls[$item.Image] -replace '/revision/.*$', ''
        $temporary = "$target.download"
        try {
            Invoke-WebRequest -Uri $downloadUrl -OutFile $temporary -Headers @{
                'User-Agent' = 'Mozilla/5.0'; 'Referer' = 'https://raft.fandom.com/'
            }
            & ffmpeg -loglevel error -y -i $temporary $target
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $target)) { $item.Icon = $fileName }
        } catch {
            Write-Warning "Icon failed: $($item.Name)"
        } finally {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }
}

# Reuse already-downloaded assets even if the wiki renamed an image or did not
# return imageinfo during this run.
$iconsByName = @{}
foreach ($icon in Get-ChildItem -LiteralPath $IconsPath -Filter '*.png') {
    $iconsByName[(Normalize-Name $icon.BaseName)] = $icon.Name
}
foreach ($item in $wikiItems) {
    if (-not [string]::IsNullOrWhiteSpace($item.Icon)) { continue }
    if ($iconsByName.ContainsKey($item.Normalized)) {
        $item.Icon = $iconsByName[$item.Normalized]
    }
}

$legacyPath = Join-Path $PSScriptRoot '..\src\RaftSaveEditor.Core\Catalog\items.catalog.txt'
$catalogSource = Join-Path $PSScriptRoot '..\src\RaftSaveEditor.Core\Catalog\RaftItemCatalog.cs'
$gameCatalogPath = Join-Path $PSScriptRoot '..\src\RaftSaveEditor.Core\Catalog\items.game.txt'
$names = @{}
$gameUniqueNames = @{}
$gameDisplayNames = @{}
$gameStack = @{}
$gameUses = @{}
$gameIcons = @{}
if (Test-Path -LiteralPath $gameCatalogPath) {
    foreach ($line in Get-Content -LiteralPath $gameCatalogPath) {
        if ($line -notmatch '^\d+\|') { continue }
        $fields = $line -split '\|', 9
        if ($fields.Count -lt 9) { continue }
        $id = [int]$fields[0]
        $gameUniqueNames[$id] = $fields[1]
        $gameDisplayNames[$id] = $fields[2]
        $names[$id] = if ([string]::IsNullOrWhiteSpace($fields[2])) { $fields[1] } else { $fields[2] }
        $gameStack[$id] = [int]$fields[3]
        $gameUses[$id] = [int]$fields[4]
        $localIcon = "Game_$id.png"
        if (Test-Path -LiteralPath (Join-Path $IconsPath $localIcon)) { $gameIcons[$id] = $localIcon }
    }
} else {
    foreach ($line in Get-Content -LiteralPath $legacyPath) {
        if ($line -match '^(\d+)\|(.+)$') { $names[[int]$matches[1]] = $matches[2].Trim() }
    }
    $sourceText = Get-Content -LiteralPath $catalogSource -Raw
    foreach ($match in [regex]::Matches($sourceText, '\[(\d+)\]\s*=\s*"([^"]+)"')) {
        $names[[int]$match.Groups[1].Value] = $match.Groups[2].Value
    }
    $names[275] = 'Machete'
}

$aliases = @{
    'battery' = 'simplebattery'; 'flipper' = 'flippers'; 'seavine' = 'seaweed'; 'thatch' = 'palmleaf'
    'copperore' = 'copper'; 'shear' = 'shears'; 'netgun' = 'netlauncher'; 'spearwood' = 'woodenspear'
    'spearscrap' = 'metalspear'; 'hookplastic' = 'plastichook'; 'hookscrap' = 'scraphook'
    'fishingrodmetal' = 'metalfishingrod'; 'headpoisonpuffer' = 'poisonpufferhead'
    'cookedsharkmeat' = 'cookedshark'; 'rawsharkmeat' = 'rawsharkmeat'
    'bow' = 'basicbow'; 'arrowstone' = 'stonearrow'; 'arrowmetal' = 'metalarrow'; 'axestone' = 'stoneaxe'
    'claybowlempty' = 'claybowl'; 'berriesred' = 'redberries'; 'cupempty' = 'emptycup'
    'cupwater' = 'cupoffreshwater'; 'plasticcupsaltwater' = 'cupofsaltwater'; 'plasticbottleempty' = 'emptybottle'
    'plasticbottlesaltwater' = 'bottleofsaltwater'; 'plasticbottlewater' = 'bottleoffreshwater'; 'headshark' = 'sharkhead'
    'placeablechair' = 'chair'; 'placeableanchorthrowable' = 'throwableanchor'
    'placeablecookingstandpurifiertwo' = 'advancedpurifier'; 'placeablecookingstandpurifierone' = 'simplepurifier'
    'placeablebedhammock' = 'hammock'; 'placeablebedsimple' = 'simplebed'; 'placeablescarecrow' = 'scarecrow'
    'placeablebirdsnest' = 'birdsnest'; 'placeablescrapmechanic' = 'scrapmechanicduck'
    'placeableresearchtable' = 'researchtable'; 'placeablepaintmill' = 'paintmill'; 'placeablesharktrophy' = 'sharkhead'
    'placeablecropplotshoe' = 'oldshoe'; 'placeablestoragesmall' = 'smallstorage'; 'placeablestreamer' = 'streamer'
    'placeableluckycat' = 'luckycat'; 'placeablecandlebottle' = 'candlebottle'; 'placeablerug' = 'rug'
    'placeableclock' = 'clock'; 'placeablecropplotmedium' = 'mediumcropplot'; 'placeablecalendar' = 'calendar'
    'placeablesign' = 'sign'; 'placeablereciever' = 'receiver'; 'placeablerecieverantenna' = 'antenna'
    'placeableshelf' = 'shelf'; 'placeablecookingpot' = 'cookingpot'; 'placeabletrophyboardlarge' = 'largetrophyboard'
    'placeabletrophyboardmedium' = 'mediumtrophyboard'; 'placeabletrophyboardsmall' = 'smalltrophyboard'
    'colorred' = 'redpaint'; 'colorblue' = 'bluepaint'; 'coloryellow' = 'yellowpaint'; 'colorwhite' = 'whitepaint'; 'colorblack' = 'blackpaint'
    'seedflowerblack' = 'blackseed'; 'seedflowerwhite' = 'whiteseed'; 'seedflowerred' = 'redseed'
    'seedfloweryellow' = 'yellowseed'; 'seedflowerblue' = 'blueseed'; 'seedwatermelon' = 'watermelonseed'
    'seedmango' = 'mangoseed'; 'seedpineapple' = 'pineappleseed'; 'seedgrass' = 'grassseed'
    'claybowlvegetablesoup' = 'vegetablesoup'; 'claybowlleftover' = 'leftovers'; 'claybowlsimplefishstew' = 'simplefishstew'
    'clayplatecatfishdeluxe' = 'catfishdeluxe'; 'claybowlcoconutchicken' = 'coconutchicken'
    'clayplatedrumstickwithjam' = 'steakwithjam'; 'claybowlheadbroth' = 'headbroth'
    'clayplatemushroomomelette' = 'mushroomomelette'; 'clayplatesalmonsalad' = 'salmonsalad'
    'clayplatesharkdinner' = 'sharkdinner'; 'claybowlfishstew' = 'heartystew'; 'clayplatefruitcompot' = 'fruitcompot'
    'placeablerecipevegetablesoup' = 'recipevegetablesoup'; 'placeablerecipesimplefishstew' = 'recipesimplefishstew'
    'placeablerecipecatfishdeluxe' = 'recipecatfishdeluxe'; 'placeablerecipecoconutchicken' = 'recipecoconutchicken'
    'placeablerecipedrumstickwithjam' = 'recipesteakwithjam'; 'placeablerecipeheadbroth' = 'recipeheadbroth'
    'placeablerecipemushroomomelette' = 'recipemushroomomelette'; 'placeablerecipesalmonsalad' = 'recipesalmonsalad'
    'placeablerecipesharkdinner' = 'recipesharkdinner'; 'placeablerecipefishstew' = 'recipeheartystew'
    'placeablerecipefruitcompot' = 'recipefruitcompot'
    'placeablerecipesimplesmoothie' = 'recipesimplesmoothie'; 'placeablerecipecoconutbeat' = 'recipecoconutbeat'
    'placeablerecipemangonana' = 'recipemangonana'; 'tradetoken' = 'tradecoin'
    'bucketmilk' = 'bucketofmilk'; 'equipmentleatherhelmet' = 'leatherhelmet'; 'equipmentleatherchest' = 'leatherbodyarmor'
    'equipmentleatherlegs' = 'leathergreaves'; 'headboar' = 'warthoghead'
    'placeableanchorstationary' = 'stationaryanchor'; 'placeableitemnet' = 'simplecollectionnet'; 'rawshark' = 'rawsharkmeat'
    'placeablecollectionnetbasic' = 'simplecollectionnet'; 'placeablecollectionnetadvanced' = 'advancedcollectionnet'
    'placeablestoragelarge' = 'largestorage'; 'placeablepipewater' = 'waterpipe'
    'seedpalm' = 'palmseed'; 'axe' = 'metalaxe'; 'hammer' = 'buildinghammer'; 'plasticcupempty' = 'emptycup'
    'plasticcupwater' = 'cupoffreshwater'; 'placeablestoragemedium' = 'storage'; 'placeablecookingstandfoodone' = 'simplegrill'
    'placeablecropplotsmall' = 'smallcropplot'; 'placeablecropplotlarge' = 'largecropplot'; 'placeablelantern' = 'lantern'
    'placeabletable' = 'table'; 'placeablegiantclam' = 'giantclam'; 'placeablecookingstandsmelter' = 'smelter'
    'placeablebrickwet' = 'wetbrick'; 'brickdry' = 'drybrick'; 'placeablecookingstandfoodtwo' = 'advancedgrill'
    'flowerblack' = 'blackflower'; 'flowerred' = 'redflower'; 'floweryellow' = 'yellowflower'
    'flowerblue' = 'blueflower'; 'flowerwhite' = 'whiteflower'; 'placeablesail' = 'sail'
    'blueprintreciever' = 'blueprintreceiver'; 'headscreecher' = 'screecherhead'; 'placeablecropplotgrass' = 'grassplot'
}
$wikiByName = @{}
foreach ($item in $wikiItems) { $wikiByName[$item.Normalized] = $item }

$output = [System.Collections.Generic.List[string]]::new()
$output.Add('# ID|Name|StackSize|MaxUses|Type|Icon')
$output.Add('# Generated from Raft Wiki ItemInfobox data; 0 means unknown/unverified.')
$matched = 0
foreach ($id in @($names.Keys | Sort-Object)) {
    $name = $names[$id] -replace '_', ' '
    $normalized = Normalize-Name $name
    if ($aliases.ContainsKey($normalized)) { $normalized = $aliases[$normalized] }
    $wiki = $wikiByName[$normalized]
    if ($null -eq $wiki -and $gameUniqueNames.ContainsKey($id)) {
        $uniqueNormalized = Normalize-Name $gameUniqueNames[$id]
        if ($aliases.ContainsKey($uniqueNormalized)) { $uniqueNormalized = $aliases[$uniqueNormalized] }
        $wiki = $wikiByName[$uniqueNormalized]
    }
    if ($null -ne $wiki) {
        $matched++
        $cleanType = ($wiki.Type -replace '[|\r\n]', ' ').Trim()
        $stack = if ($gameStack.ContainsKey($id) -and $gameStack[$id] -gt 0) { $gameStack[$id] } else { $wiki.Stack }
        $uses = if ($gameUses.ContainsKey($id)) { $gameUses[$id] } else { $wiki.Uses }
        $icon = if ($gameIcons.ContainsKey($id)) { $gameIcons[$id] } else { $wiki.Icon }
        $output.Add("$id|$($wiki.Name)|$stack|$uses|$cleanType|$icon")
    } else {
        $fallbackName = if ($gameUniqueNames.ContainsKey($id)) { $gameUniqueNames[$id] -replace '_', ' ' } else { $name }
        $cleanName = ($fallbackName -creplace '(?<=[a-z])(?=[A-Z])', ' ').Trim()
        $stack = if ($gameStack.ContainsKey($id)) { $gameStack[$id] } else { 0 }
        $uses = if ($gameUses.ContainsKey($id)) { $gameUses[$id] } else { 0 }
        $icon = if ($gameIcons.ContainsKey($id)) { $gameIcons[$id] } else { '' }
        $type = if ($normalized.StartsWith('blueprint')) { 'Blueprint' }
            elseif ($normalized.StartsWith('placeable') -or $normalized.StartsWith('block')) { 'Building' }
            elseif ($normalized.StartsWith('seed')) { 'Seeds' }
            else { 'Unclassified' }
        $output.Add("$id|$cleanName|$stack|$uses|$type|$icon")
    }
}

$output | Set-Content -LiteralPath $CatalogPath -Encoding utf8
"Wiki item infoboxes: $($wikiItems.Count)"
"Catalog IDs: $($names.Count)"
"Catalog IDs matched to Wiki metadata: $matched"
"Icons downloaded: $(@(Get-ChildItem -LiteralPath $IconsPath -Filter '*.png').Count)"
