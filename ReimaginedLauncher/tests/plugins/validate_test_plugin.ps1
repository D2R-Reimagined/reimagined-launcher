param(
    [string]$PluginRoot = "C:\z_GitHub\reimagined-launcher\ReimaginedLauncher\tests\plugins\AllFilesTestPlugin",
    [string]$ModelsDir = "C:\z_GitHub\d2r-dotnet-tools\FileExtensions\Models"
)

# Re-build type->property map from source (same logic as generate_plugin_data.ps1)
$propRegex = [regex]'public\s+([A-Za-z_][A-Za-z0-9_<>\?]*)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{\s*get;\s*(?:set|init);\s*\}'
$modelFiles = @{}
foreach ($f in Get-ChildItem $ModelsDir -Filter "*.cs") {
    $modelFiles[$f.BaseName] = Get-Content $f.FullName -Raw
}

function Get-TypeProps($typeName) {
    $result = @{}
    $visited = @{}
    $q = New-Object System.Collections.Queue
    $q.Enqueue($typeName)
    while ($q.Count -gt 0) {
        $n = $q.Dequeue()
        if ($visited.ContainsKey($n)) { continue }
        $visited[$n] = $true
        $src = $null
        foreach ($k in $modelFiles.Keys) {
            if ($modelFiles[$k] -match "(?:class|record)\s+$([regex]::Escape($n))\b") { $src = $modelFiles[$k]; break }
        }
        if (-not $src) { continue }
        $defMatches = [regex]::Matches($src, 'public\s+(?:sealed\s+)?(?:class|record)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?::\s*([A-Za-z_][A-Za-z0-9_]*))?')
        foreach ($dm in $defMatches) {
            if ($dm.Groups[1].Value -eq $n -and $dm.Groups[2].Success) { $q.Enqueue($dm.Groups[2].Value) }
        }
        foreach ($pm in $propRegex.Matches($src)) { $result[$pm.Groups[2].Value] = $true }
    }
    return $result
}

# File -> (TypeName, UsesRowId)
$registry = @{
    "armor.txt" = @("Armor",$false); "automagic.txt" = @("AutoMagic",$false); "charstats.txt" = @("CharStats",$false);
    "cubemain.txt" = @("CubeMain",$true); "cubemod.txt" = @("CubeModifierType",$false);
    "difficultylevels.txt" = @("DifficultyLevel",$false); "experience.txt" = @("Experience",$false);
    "gamble.txt" = @("Gamble",$false); "gems.txt" = @("Gem",$false); "hireling.txt" = @("Hirelings",$true);
    "inventory.txt" = @("Inventory",$false); "itemtypes.txt" = @("ItemType",$false);
    "lvlmaze.txt" = @("LvlMaze",$true); "lvlprest.txt" = @("LevelsPreset",$true); "lvlwarp.txt" = @("LvlWarp",$false);
    "magicprefix.txt" = @("MagicPrefix",$true); "magicsuffix.txt" = @("MagicSuffix",$true);
    "misc.txt" = @("Misc",$false); "missiles.txt" = @("Missiles",$false);
    "monequip.txt" = @("MonEquip",$true); "monpreset.txt" = @("MonPreset",$true);
    "monprop.txt" = @("MonProp",$false); "monstats.txt" = @("MonStat",$false); "monstats2.txt" = @("MonStats2",$false);
    "montype.txt" = @("MonType",$false); "monumod.txt" = @("MonUMod",$false);
    "npc.txt" = @("Npc",$false); "pettype.txt" = @("PetType",$false);
    "properties.txt" = @("Property",$false); "propertygroups.txt" = @("PropertyGroup",$false);
    "runes.txt" = @("RuneWord",$false); "setitems.txt" = @("SetItem",$false); "sets.txt" = @("Sets",$false);
    "shrines.txt" = @("Shrines",$false); "skillcalc.txt" = @("SkillCalc",$false); "skilldesc.txt" = @("SkillDesc",$false);
    "skills.txt" = @("Skills",$false); "sounds.txt" = @("Sounds",$false); "states.txt" = @("States",$false);
    "storepage.txt" = @("StorePage",$false); "superuniques.txt" = @("SuperUnique",$false);
    "treasureclassex.txt" = @("TreasureClass",$false); "uniqueitems.txt" = @("UniqueItem",$false);
    "weapons.txt" = @("Weapon",$false);
    "actinfo.txt" = @("ActInfo",$false); "automap.txt" = @("Automap",$true);
    "itemuicategories.txt" = @("ItemUiCategory",$false); "levelgroups.txt" = @("LevelGroup",$false);
    "levels.txt" = @("Level",$false); "monlvl.txt" = @("MonLvl",$false); "monpet.txt" = @("MonPet",$false);
    "objects.txt" = @("GameObject",$true); "overlay.txt" = @("Overlay",$false)
}

$errors = @()

# Load plugininfo
$piPath = Join-Path $PluginRoot "plugininfo.json"
if (!(Test-Path $piPath)) { Write-Host "MISSING plugininfo.json"; exit 1 }
$pi = Get-Content $piPath -Raw | ConvertFrom-Json
if (-not $pi.name) { $errors += "plugininfo.json: name missing" }
if (-not $pi.version) { $errors += "plugininfo.json: version missing" }
if (-not $pi.modVersion) { $errors += "plugininfo.json: modVersion missing" }
if ($pi.modVersion -and $pi.modVersion -notmatch '^\d+\.\d+\.\d+$') { $errors += "plugininfo.json: modVersion '$($pi.modVersion)' not in #.#.# format" }
$paramKeys = @()
foreach ($p in $pi.parameters) { $paramKeys += $p.key }

# Cache type props
$propCache = @{}
foreach ($rel in $pi.files) {
    $abs = Join-Path $PluginRoot ($rel -replace '/', '\')
    if (!(Test-Path $abs)) { $errors += "MISSING file '$rel'"; continue }
    try {
        $ops = Get-Content $abs -Raw | ConvertFrom-Json
    } catch {
        $errors += "'$rel' invalid JSON: $_"
        continue
    }
    if ($ops -isnot [array]) { $ops = @($ops) }
    if ($ops.Count -eq 0) { $errors += "'$rel' has no operations"; continue }
    foreach ($op in $ops) {
        $ctx = "'$rel' op"
        if (-not $op.file) { $errors += "$ctx missing file"; continue }
        if (-not $registry.ContainsKey($op.file)) { $errors += "$ctx targets unsupported file '$($op.file)'"; continue }
        $target = $registry[$op.file]
        $typeName = $target[0]; $usesRowId = $target[1]
        if ([string]::IsNullOrWhiteSpace($op.rowIdentifier)) { $errors += "$ctx (file=$($op.file)) missing rowIdentifier" }
        elseif ($usesRowId) {
            $n = 0
            if (-not [int]::TryParse($op.rowIdentifier, [ref]$n)) { $errors += "$ctx (file=$($op.file)) non-numeric rowIdentifier '$($op.rowIdentifier)' but file uses numeric row IDs" }
        }
        if ([string]::IsNullOrWhiteSpace($op.column)) { $errors += "$ctx (file=$($op.file)) missing column"; continue }
        if (-not $propCache.ContainsKey($typeName)) { $propCache[$typeName] = Get-TypeProps $typeName }
        $props = $propCache[$typeName]
        $colExists = $false
        foreach ($k in $props.Keys) { if ($k.ToLower() -eq $op.column.ToLower()) { $colExists = $true; break } }
        if (-not $colExists) { $errors += "$ctx (file=$($op.file)) column '$($op.column)' is not a property of $typeName" }
        if ($op.operation -and $op.operation.ToLower() -eq "multiplyexisting") {
            if ([string]::IsNullOrWhiteSpace($op.parameterKey) -and [string]::IsNullOrWhiteSpace($op.updatedValue)) {
                $errors += "$ctx (file=$($op.file)) multiplyExisting without parameterKey or updatedValue"
            }
        }
        if ($op.parameterKey -and -not ($paramKeys -contains $op.parameterKey)) {
            $errors += "$ctx (file=$($op.file)) parameterKey '$($op.parameterKey)' not declared in plugininfo.json"
        }
    }
}

if ($errors.Count -eq 0) {
    Write-Host "OK: plugin validates cleanly ($($pi.files.Count) operation files)."
} else {
    Write-Host "FOUND $($errors.Count) error(s):"
    $errors | ForEach-Object { Write-Host "  $_" }
    exit 2
}
