param(
    [Parameter(Mandatory = $true)]
    [string]$BaseRef
)

$ErrorActionPreference = 'Stop'

git fetch origin $BaseRef --depth=200

$commitSubjects = @(git log "origin/$BaseRef..HEAD" --pretty=format:'%s')
$allowedTypes = 'feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert'
$pattern = "^(?:$allowedTypes)(?:\([^)]+\))?!?: .+"

$invalidSubjects = @()
foreach ($subject in $commitSubjects) {
    if ([string]::IsNullOrWhiteSpace($subject)) {
        continue
    }

    if ($subject -like 'Merge *') {
        continue
    }

    if ($subject -notmatch $pattern) {
        $invalidSubjects += $subject
    }
}

if ($invalidSubjects.Count -eq 0) {
    Write-Host "All commit messages match the semantic commit convention."
    exit 0
}

Write-Error @"
The following commit messages do not follow the required semantic commit format:

$($invalidSubjects | ForEach-Object { "- $_" } | Out-String)
Expected format:
  type(scope): description
  type: description

Allowed types:
  feat, fix, docs, style, refactor, perf, test, build, ci, chore, revert

Examples:
  feat(launcher): add release channel selector
  fix(auth): handle expired session refresh
  chore: update release workflow
"@
