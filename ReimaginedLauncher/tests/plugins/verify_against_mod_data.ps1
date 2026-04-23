#requires -Version 5.1
# Cross-checks AllFilesTestPlugin operations against actual mod data files.
# - File exists
# - Column header matches (case-sensitive as the launcher does it; we also flag case mismatches)
# - rowIdentifier resolves (by RowId for usesRowId files, else by the registered lookup column)
# - multiplyExisting targets a numeric cell value
param(
    [string]$ModExcelDir = "C:\z_GitHub\d2r-reimagined-mod\data\global\excel",
    [string]$PluginDir   = (Join-Path $PSScriptRoot "AllFilesTestPlugin")
)

# Mirror PluginsService.BuildParserRegistry: file -> @{ Column=<lookup column>; UsesRowId=<bool> }
$registry = @{
    "actinfo.txt"       = @{ Column = "act";            UsesRowId = $true  }  # registered usesRowId:true? verify below
}

# Build registry by reading PluginsService.cs to stay accurate.
$svc = Get-Content "$PSScriptRoot\..\..\Utilities\PluginsService.cs" -Raw
$registry = @{}
$rx = [regex]'(?s)Register<([^>]+)>\(\s*"([^"]+\.txt)"\s*,\s*"([^"]+)"\s*,.*?\)\s*;'
foreach ($m in $rx.Matches($svc)) {
    $entryType = $m.Groups[1].Value
    $file = $m.Groups[2].Value
    $col  = $m.Groups[3].Value
    $block = $m.Value
    $uses = ($block -match 'usesRowId\s*:\s*true')
    $registry[$file] = @{ Column = $col; UsesRowId = $uses; EntryType = $entryType }
}

Write-Host "Registry entries parsed: $($registry.Count)"

# Read PropertyColumnAliases from each *Parser.cs so we can resolve property-name -> file-header
# exactly like the library does at runtime (HeaderMappedTextFileParser).
$parsersDir = "C:\z_GitHub\d2r-dotnet-tools\FileExtensions\TextFileParsers"
$aliasMap = @{}   # EntryType -> @{ PropName(lowercase) = @(aliasHeader,...) }
if (Test-Path $parsersDir) {
    foreach ($cs in Get-ChildItem $parsersDir -Filter *Parser.cs) {
        $src = Get-Content $cs.FullName -Raw
        # detect generic base: HeaderMappedTextFileParser<EntryType, ParserType>
        $tm = [regex]::Match($src, 'HeaderMappedTextFileParser<\s*([A-Za-z_][A-Za-z0-9_]*)\s*,')
        if (-not $tm.Success) { continue }
        $entry = $tm.Groups[1].Value
        $dict = @{}
        # Scan every `new Dictionary<string, string[]> { ... }` block in the parser file.
        # (Covers both direct PropertyColumnAliases initializers and the `Aliases` backing-field pattern.)
        foreach ($bm in [regex]::Matches($src, 'new\s+Dictionary<\s*string\s*,\s*string\s*\[\s*\]\s*>\s*\{([\s\S]*?)\}\s*;')) {
            $body = $bm.Groups[1].Value
            foreach ($em in [regex]::Matches($body, '\[(?:"(?<q>[^"]+)"|nameof\([^.]+\.(?<n>[A-Za-z_][A-Za-z0-9_]*)\))\]\s*=\s*\[(?<v>[^\]]*)\]')) {
                $prop = if ($em.Groups['q'].Success) { $em.Groups['q'].Value } else { $em.Groups['n'].Value }
                $vals = @()
                foreach ($vm in [regex]::Matches($em.Groups['v'].Value, '"([^"]+)"')) {
                    $vals += $vm.Groups[1].Value
                }
                $dict[$prop.ToLowerInvariant()] = $vals
            }
        }
        $aliasMap[$entry] = $dict
    }
}

function Normalize-ColumnName([string]$name) {
    # Mirror HeaderMappedTextFileParser.NormalizeColumnName: keep only letters/digits, lowercase.
    if ($null -eq $name) { return '' }
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $name.Trim().ToCharArray()) {
        if ([char]::IsLetterOrDigit($ch)) { [void]$sb.Append([char]::ToLowerInvariant($ch)) }
    }
    return $sb.ToString()
}

function Find-HeaderIndex($header, $entryType, $propertyName) {
    # Mirror HeaderMappedTextFileParser.GetColumnCandidates with NormalizeColumnName.
    $candidates = New-Object System.Collections.Generic.List[string]
    [void]$candidates.Add((Normalize-ColumnName $propertyName))
    if ($aliasMap.ContainsKey($entryType)) {
        $d = $aliasMap[$entryType]
        $key = $propertyName.ToLowerInvariant()
        if ($d.ContainsKey($key)) {
            foreach ($a in $d[$key]) { [void]$candidates.Add((Normalize-ColumnName $a)) }
        }
    }
    for ($c = 0; $c -lt $header.Count; $c++) {
        $hn = Normalize-ColumnName $header[$c]
        foreach ($cand in $candidates) {
            if ($hn -eq $cand) { return $c }
        }
    }
    return -1
}

# Scan model .cs files for property names and types (static analysis — avoids assembly-load issues)
$modelsDir = "C:\z_GitHub\d2r-dotnet-tools\FileExtensions\Models"
if (-not (Test-Path $modelsDir)) { Write-Host "Models dir not found: $modelsDir" -ForegroundColor Red; exit 2 }
$modelIndex = @{}
# Types to collect inheritance chains so derived records include inherited props (Equipment base)
$typeDefs = @{}
foreach ($cs in Get-ChildItem $modelsDir -Filter *.cs) {
    $src = Get-Content $cs.FullName -Raw
    # capture record/class declarations with optional inheritance
    foreach ($m in [regex]::Matches($src, '(?:public\s+)?(?:sealed\s+|abstract\s+)?(?:partial\s+)?(?:record|class)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?::\s*([A-Za-z_][A-Za-z0-9_]*))?')) {
        $name = $m.Groups[1].Value
        $base = if ($m.Groups[2].Success) { $m.Groups[2].Value } else { $null }
        $typeDefs[$name] = @{ Source = $src; Base = $base }
    }
}
function Get-OwnProps($typeName) {
    if (-not $typeDefs.ContainsKey($typeName)) { return @() }
    $src = $typeDefs[$typeName].Source
    $result = @()
    foreach ($pm in [regex]::Matches($src, 'public\s+([A-Za-z_][A-Za-z0-9_<>,\.\?\s]*?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{\s*get;\s*(?:set|init);\s*\}')) {
        $result += ,@{ Name = $pm.Groups[2].Value.Trim(); Type = $pm.Groups[1].Value.Trim() }
    }
    return $result
}
function Get-AllProps($typeName) {
    $acc = @()
    $cur = $typeName
    $seen = @{}
    while ($cur -and $typeDefs.ContainsKey($cur) -and -not $seen.ContainsKey($cur)) {
        $seen[$cur] = $true
        $acc += Get-OwnProps $cur
        $cur = $typeDefs[$cur].Base
    }
    return $acc
}
function Get-ModelProperties($typeName) {
    if (-not $typeDefs.ContainsKey($typeName)) { return $null }
    return (Get-AllProps $typeName) | ForEach-Object { $_.Name }
}
function Get-ModelPropertyType($typeName, $propName) {
    if (-not $typeDefs.ContainsKey($typeName)) { return $null }
    $p = (Get-AllProps $typeName) | Where-Object { $_.Name -ieq $propName } | Select-Object -First 1
    if ($null -eq $p) { return $null }
    return $p.Type
}
function Test-NumericType($typeName) {
    if (-not $typeName) { return $false }
    $t = $typeName.TrimEnd('?').Trim()
    return $t -match '^(int|long|short|byte|sbyte|uint|ulong|ushort|float|double|decimal|Int32|Int64|Int16|UInt32|UInt64|Single|Double|Decimal|Byte)$'
}

function Test-IsNumeric($s) {
    if ($null -eq $s) { return $false }
    $t = ([string]$s).Trim()
    if ($t -eq '') { return $false }
    return [double]::TryParse($t, [ref]([double]0))
}

$opDir = Join-Path $PluginDir "operations"
$problems = New-Object System.Collections.Generic.List[string]
$fileCache = @{}

function Get-FileData($path) {
    if ($fileCache.ContainsKey($path)) { return $fileCache[$path] }
    $lines = Get-Content -LiteralPath $path
    $header = $lines[0] -split "`t"
    $rows = @()
    for ($i = 1; $i -lt $lines.Count; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i])) { continue }
        $rows += ,($lines[$i] -split "`t")
    }
    $data = @{ Header = $header; Rows = $rows }
    $fileCache[$path] = $data
    return $data
}

$opFiles = Get-ChildItem $opDir -Filter *.json | Sort-Object Name
foreach ($f in $opFiles) {
    $ops = Get-Content $f.FullName -Raw | ConvertFrom-Json
    if ($ops -isnot [System.Array]) { $ops = @($ops) }
    $i = 0
    foreach ($op in $ops) {
        $i++
        $ctx = "$($f.Name)#$i [$($op.file)]"
        $txt = $op.file
        $dataPath = Join-Path $ModExcelDir $txt
        if (-not (Test-Path $dataPath)) { $problems.Add("$ctx : DATA FILE MISSING at $dataPath"); continue }
        if (-not $registry.ContainsKey($txt)) { $problems.Add("$ctx : NOT REGISTERED in PluginsService"); continue }
        $reg = $registry[$txt]
        $data = Get-FileData $dataPath

        # Column existence — runtime requires the column name to match a property on the entry model (case-insensitive)
        $props = Get-ModelProperties $reg.EntryType
        if ($null -eq $props) { $problems.Add("$ctx : model type '$($reg.EntryType)' not found in FileExtensions.dll"); continue }
        if (-not ($props | Where-Object { $_ -ieq $op.column })) {
            $problems.Add("$ctx : COLUMN '$($op.column)' is not a property on $($reg.EntryType)")
            continue
        }
        # Resolve op.column (a model property name) to a real file header, using parser aliases.
        $colIdx = Find-HeaderIndex $data.Header $reg.EntryType $op.column
        if ($colIdx -lt 0) {
            $problems.Add("$ctx : COLUMN '$($op.column)' has no matching header in $txt (aliases checked)"); continue
        }

        # Resolve row
        $rowIdx = -1
        if ($reg.UsesRowId) {
            $n = 0
            if (-not [int]::TryParse([string]$op.rowIdentifier, [ref]$n)) {
                $problems.Add("$ctx : rowIdentifier '$($op.rowIdentifier)' is not an integer but file uses RowId"); continue
            }
            if ($n -lt 0 -or $n -ge $data.Rows.Count) {
                $problems.Add("$ctx : RowId $n out of range (0..$($data.Rows.Count-1))"); continue
            }
            $rowIdx = $n
        } else {
            $lookupIdx = Find-HeaderIndex $data.Header $reg.EntryType $reg.Column
            if ($lookupIdx -lt 0) {
                $problems.Add("$ctx : registry lookup column '$($reg.Column)' not present in $txt header (aliases checked)"); continue
            }
            for ($r = 0; $r -lt $data.Rows.Count; $r++) {
                $row = $data.Rows[$r]
                if ($lookupIdx -lt $row.Count -and $row[$lookupIdx] -ieq $op.rowIdentifier) {
                    $rowIdx = $r; break
                }
            }
            if ($rowIdx -lt 0) {
                $problems.Add("$ctx : rowIdentifier '$($op.rowIdentifier)' not found in lookup column '$($reg.Column)'"); continue
            }
        }

        # multiplyExisting must resolve to a numeric existing value
        if ($op.operation -eq 'multiplyExisting') {
            $propType = Get-ModelPropertyType $reg.EntryType $op.column
            $propNumeric = Test-NumericType $propType
            if ($colIdx -ge 0) {
                $row = $data.Rows[$rowIdx]
                $cell = ""
                if ($colIdx -lt $row.Count) { $cell = $row[$colIdx] }
                if (-not (Test-IsNumeric $cell)) {
                    $problems.Add("$ctx : multiplyExisting targets non-numeric cell in '$($op.column)' (value='$cell', propType=$propType) at row '$($op.rowIdentifier)'")
                }
            } else {
                if (-not $propNumeric) {
                    $problems.Add("$ctx : multiplyExisting targets non-numeric property '$($op.column)' (propType=$propType) - and no header alias resolved to verify cell value")
                }
            }
        }
    }
}

Write-Host ""
if ($problems.Count -eq 0) {
    Write-Host "OK: All operations resolve against mod data." -ForegroundColor Green
    exit 0
} else {
    Write-Host "FOUND $($problems.Count) PROBLEM(S):" -ForegroundColor Yellow
    $problems | ForEach-Object { Write-Host " - $_" }
    exit 1
}
