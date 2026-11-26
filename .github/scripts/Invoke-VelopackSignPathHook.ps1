[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('Collect', 'Apply')]
    [string]$Mode = $env:SIGNPATH_HOOK_MODE,

    [string]$WorkDir = $env:SIGNPATH_HOOK_WORKDIR,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Files
)

$ErrorActionPreference = 'Stop'

# Two-pass SignPath hook for `vpk pack --signTemplate`:
#   Collect: stage the unsigned app exe, Squirrel.exe, and execution stub with SHA-256 manifest.
#   Apply:   swap in the SignPath-signed copies (matched by original SHA-256) before vpk hashes the nupkg.
# Setup.exe is skipped here and signed in its own SignPath step after packing.

if ([string]::IsNullOrWhiteSpace($Mode)) {
    throw 'Mode is required via -Mode or SIGNPATH_HOOK_MODE (Collect or Apply).'
}

if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    throw 'WorkDir is required via -WorkDir or SIGNPATH_HOOK_WORKDIR.'
}

$manifestPath = Join-Path $WorkDir 'manifest.json'
$unsignedDir = Join-Path $WorkDir 'unsigned'
$signedDir = Join-Path $WorkDir 'signed'

function Test-ManagedSigningTarget {
    param([string]$FileName)

    return $FileName -ieq 'ReimaginedLauncher.exe' -or
        $FileName -ieq 'Squirrel.exe' -or
        $FileName -ilike '*_ExecutionStub.exe'
}

$manifest = [ordered]@{}
if (Test-Path -LiteralPath $manifestPath) {
    $existing = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($prop in $existing.PSObject.Properties) {
        $manifest[$prop.Name] = [string]$prop.Value
    }
}

$targets = [System.Collections.Generic.List[string]]::new()
foreach ($file in $Files) {
    if ([string]::IsNullOrWhiteSpace($file)) {
        continue
    }

    $name = [System.IO.Path]::GetFileName($file)
    if ($name -ilike '*-Setup.exe') {
        Write-Host "Skipping $name (signed after vpk pack)."
        continue
    }

    if (-not (Test-ManagedSigningTarget -FileName $name)) {
        Write-Host "Skipping unmanaged PE file: $file"
        continue
    }

    $targets.Add($file)
}

if ($Mode -eq 'Collect') {
    New-Item -ItemType Directory -Path $unsignedDir -Force | Out-Null

    foreach ($file in $targets) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        $name = [System.IO.Path]::GetFileName($file)

        foreach ($entry in $manifest.GetEnumerator()) {
            if ($entry.Value -ieq $name -and $entry.Key -ne $hash) {
                throw "Two different files share name '$name' ($($entry.Key) vs $hash)."
            }
        }

        $manifest[$hash] = $name
        Copy-Item -LiteralPath $file -Destination (Join-Path $unsignedDir $name) -Force
        Write-Host "Collected $name ($hash)"
    }

    $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
    exit 0
}

if ($targets.Count -eq 0) {
    exit 0
}

if ($manifest.Count -eq 0) {
    throw "Signing manifest '$manifestPath' is missing or empty. Run Collect pass first."
}

foreach ($file in $targets) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = [System.IO.Path]::GetFileName($file)

    if (-not $manifest.Contains($hash)) {
        throw "Unsigned file '$name' ($hash) does not match any file collected in pass 1."
    }

    $expectedName = $manifest[$hash]
    if ($expectedName -ine $name) {
        throw "Collected file name mismatch for $hash`: expected '$expectedName', got '$name'."
    }

    $signedPath = Join-Path $signedDir $expectedName
    if (-not (Test-Path -LiteralPath $signedPath -PathType Leaf)) {
        throw "Signed copy '$signedPath' was not returned by SignPath."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $signedPath
    if (-not $signature.SignerCertificate -or $signature.Status -notin @('Valid', 'UnknownError')) {
        throw "Signed copy '$signedPath' does not have a valid Authenticode signature (Status: $($signature.Status))."
    }

    Copy-Item -LiteralPath $signedPath -Destination $file -Force
    Write-Host "Applied signed $name ($($signature.SignerCertificate.Subject))"
}
