param(
    [Parameter(Mandatory = $true)]
    [string]$BaseRef
)

$ErrorActionPreference = 'Stop'

git fetch origin $BaseRef --depth=200

$commitSubjects = @(git log "origin/$BaseRef..HEAD" --pretty=format:'%s')
$allowedTypes = 'feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert'
$pattern = "^(?:$allowedTypes)(?:\([^)]+\))?!?: .+"

$matchingSubjects = @()
foreach ($subject in $commitSubjects) {
    if ([string]::IsNullOrWhiteSpace($subject)) {
        continue
    }

    if ($subject -like 'Merge *') {
        continue
    }

    if ($subject -match $pattern) {
        $matchingSubjects += $subject
    }
}

if ($matchingSubjects.Count -gt 0) {
    Write-Host "Found at least one semantic commit message in the PR:"
    $matchingSubjects | ForEach-Object { Write-Host "- $_" }
    exit 0
}

Write-Error @"
This PR must include at least one commit message that follows the semantic commit format.

Expected format:
  type(scope): description
  type: description

Allowed types:
  feat, fix, docs, style, refactor, perf, test, build, ci, chore, revert

Examples:
  feat(launcher): add release channel selector
  fix(auth): handle expired session refresh
  chore: update release workflow

Commit subjects checked:
$($commitSubjects | ForEach-Object { "- $_" } | Out-String)
"@
