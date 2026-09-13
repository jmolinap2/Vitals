$ErrorActionPreference = 'Stop'

Write-Host '== Vitals repository integrity checks =='

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location $repoRoot

$failures = New-Object System.Collections.Generic.List[string]
$allTrackedFiles = @(git ls-files)
$self = '.github/scripts/repository-integrity.ps1'

function Add-Failure([string]$message) {
    $script:failures.Add($message)
    Write-Host "::error::$message"
}

function Is-TextCandidate([string]$path) {
    $extension = [System.IO.Path]::GetExtension($path).ToLowerInvariant()
    return $extension -in @(
        '.cs', '.csproj', '.sln', '.slnx', '.props', '.targets',
        '.json', '.yml', '.yaml', '.xml', '.config', '.manifest',
        '.ps1', '.cmd', '.bat', '.md', '.txt', '.toml', '.ini'
    )
}

# -----------------------------------------------------------------------------
# Check 1: secrets and sensitive files must never be committed.
# This is intentionally conservative and runs only on tracked repository files.
# -----------------------------------------------------------------------------
Write-Host 'Check 1/2 - secrets and sensitive files'

$sensitiveExtensions = @('.pfx', '.p12', '.p8', '.key', '.pem', '.cer', '.crt', '.kdbx')
$sensitiveNames = @(
    'id_rsa', 'id_ed25519', '.env', '.env.local', '.env.production',
    'secrets.json', 'credentials.json', 'service-account.json'
)

foreach ($file in $allTrackedFiles) {
    $leaf = [System.IO.Path]::GetFileName($file).ToLowerInvariant()
    $ext = [System.IO.Path]::GetExtension($file).ToLowerInvariant()

    if ($sensitiveExtensions -contains $ext -or $sensitiveNames -contains $leaf) {
        Add-Failure "Sensitive file is tracked: $file"
    }
}

$secretPatterns = @(
    @{ Name = 'Private key'; Pattern = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----' },
    @{ Name = 'GitHub token'; Pattern = '\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}\b' },
    @{ Name = 'AWS access key'; Pattern = '\bAKIA[0-9A-Z]{16}\b' },
    @{ Name = 'OpenAI-style secret key'; Pattern = '\bsk-[A-Za-z0-9_-]{20,}\b' },
    @{ Name = 'Connection-string password'; Pattern = '(?i)(?:Password|Pwd)\s*=\s*[^;\r\n]{4,}' },
    @{ Name = 'Generic assigned secret'; Pattern = '(?i)\b(?:api[_-]?key|client[_-]?secret|access[_-]?token|auth[_-]?token)\b\s*[:=]\s*["''][A-Za-z0-9_\-\./+=]{16,}["'']' }
)

foreach ($file in $allTrackedFiles) {
    if ($file -eq $self -or -not (Is-TextCandidate $file)) { continue }

    $fullPath = Join-Path $repoRoot $file
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }

    $content = Get-Content -LiteralPath $fullPath -Raw -ErrorAction Stop
    foreach ($entry in $secretPatterns) {
        if ($content -match $entry.Pattern) {
            Add-Failure "$($entry.Name) pattern detected in $file"
        }
    }
}

# -----------------------------------------------------------------------------
# Check 2: Vitals must remain a normal user application.
# If privileged/elevated execution is ever intentionally introduced, this check
# must be changed explicitly in the same reviewed commit.
# -----------------------------------------------------------------------------
Write-Host 'Check 2/2 - privilege and dangerous execution guard'

$securitySourceFiles = $allTrackedFiles | Where-Object {
    $_ -ne $self -and ([System.IO.Path]::GetExtension($_).ToLowerInvariant() -in @(
        '.cs', '.csproj', '.props', '.targets', '.manifest', '.xml', '.config'
    ))
}

$forbiddenPatterns = @(
    @{ Name = 'Administrator manifest'; Pattern = '(?i)requireAdministrator' },
    @{ Name = 'Highest-available elevation'; Pattern = '(?i)highestAvailable' },
    @{ Name = 'UAC runas elevation'; Pattern = '(?i)\bVerb\s*=\s*["'']runas["'']' },
    @{ Name = 'Token privilege manipulation'; Pattern = '(?i)\bAdjustTokenPrivileges\b' },
    @{ Name = 'Debug privilege'; Pattern = '(?i)\bSeDebugPrivilege\b' },
    @{ Name = 'Credentialed Windows logon'; Pattern = '(?i)\bLogonUser(?:W|A)?\b' },
    @{ Name = 'Create process with token'; Pattern = '(?i)\bCreateProcessWithTokenW\b' },
    @{ Name = 'Embedded cmd shell'; Pattern = '(?i)["''](?:cmd(?:\.exe)?)["'']' },
    @{ Name = 'Embedded PowerShell shell'; Pattern = '(?i)["''](?:powershell|pwsh)(?:\.exe)?["'']' },
    @{ Name = 'Encoded PowerShell command'; Pattern = '(?i)(?:-EncodedCommand|-enc\s+[A-Za-z0-9+/=]{8,})' },
    @{ Name = 'PowerShell execution-policy bypass'; Pattern = '(?i)-ExecutionPolicy\s+Bypass' }
)

foreach ($file in $securitySourceFiles) {
    $fullPath = Join-Path $repoRoot $file
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }

    $content = Get-Content -LiteralPath $fullPath -Raw -ErrorAction Stop
    foreach ($entry in $forbiddenPatterns) {
        if ($content -match $entry.Pattern) {
            Add-Failure "$($entry.Name) detected in $file"
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Repository integrity FAILED with $($failures.Count) finding(s)."
    exit 1
}

Write-Host ''
Write-Host 'Repository integrity PASSED: no tracked secrets/sensitive files and no privileged execution surface detected.'
