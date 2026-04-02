param(
    [Parameter(Mandatory = $true)]
    [string]$CurrentTag,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$categories = [ordered]@{
    'Features' = @('feat')
    'Fixes' = @('fix')
    'Performance' = @('perf')
    'Refactoring' = @('refactor')
    'Documentation' = @('docs')
    'Tests' = @('test')
    'Build' = @('build')
    'CI' = @('ci')
    'Chores' = @('chore')
    'Reverts' = @('revert')
}

$typeToCategory = @{}
foreach ($entry in $categories.GetEnumerator()) {
    foreach ($type in $entry.Value) {
        $typeToCategory[$type] = $entry.Key
    }
}

$allTags = @(git tag --sort=-creatordate)
$previousTag = $allTags | Where-Object { $_ -ne $CurrentTag } | Select-Object -First 1

if ($previousTag) {
    $range = "$previousTag..$CurrentTag"
} else {
    $range = $CurrentTag
}

$rawCommits = git log $range --pretty=format:'%H%x1f%s%x1f%b%x1e'
$items = @{}

foreach ($category in $categories.Keys) {
    $items[$category] = [System.Collections.Generic.List[string]]::new()
}

$breakingChanges = [System.Collections.Generic.List[string]]::new()
$otherChanges = [System.Collections.Generic.List[string]]::new()

$records = $rawCommits -split [char]0x1e
foreach ($record in $records) {
    if ([string]::IsNullOrWhiteSpace($record)) {
        continue
    }

    $parts = $record -split [char]0x1f
    if ($parts.Length -lt 2) {
        continue
    }

    $subject = $parts[1].Trim()
    $body = if ($parts.Length -ge 3) { $parts[2].Trim() } else { '' }

    if ($subject -match '^(?<type>[a-z]+)(?:\((?<scope>[^)]+)\))?(?<breaking>!)?: (?<description>.+)$') {
        $type = $Matches['type']
        $scope = $Matches['scope']
        $description = $Matches['description'].Trim()
        $isBreaking = $Matches['breaking'] -eq '!' -or $body -match '(?m)^BREAKING CHANGE:'

        $prefix = if ([string]::IsNullOrWhiteSpace($scope)) {
            ''
        } else {
            "**${scope}:** "
        }

        $line = "- $prefix$description"

        if ($isBreaking) {
            $breakingChanges.Add($line)
        }

        if ($typeToCategory.ContainsKey($type)) {
            $items[$typeToCategory[$type]].Add($line)
        } else {
            $otherChanges.Add("- $subject")
        }

        continue
    }

    if ($subject -like 'Merge *') {
        continue
    }

    $otherChanges.Add("- $subject")
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# Release Notes")
$lines.Add('')

if ($previousTag) {
    $lines.Add("Changes since $previousTag")
} else {
    $lines.Add("Changes included in $CurrentTag")
}

$lines.Add('')

if ($breakingChanges.Count -gt 0) {
    $lines.Add('## Breaking Changes')
    $lines.AddRange($breakingChanges)
    $lines.Add('')
}

foreach ($category in $categories.Keys) {
    if ($items[$category].Count -eq 0) {
        continue
    }

    $lines.Add("## $category")
    $lines.AddRange($items[$category])
    $lines.Add('')
}

if ($otherChanges.Count -gt 0) {
    $lines.Add('## Other Changes')
    $lines.AddRange($otherChanges)
    $lines.Add('')
}

if ($lines[$lines.Count - 1] -eq '') {
    $lines.RemoveAt($lines.Count - 1)
}

Set-Content -Path $OutputPath -Value ($lines -join [Environment]::NewLine)
