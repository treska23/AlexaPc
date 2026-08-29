param(
    [ValidateSet('Code', 'Model')]
    [string]$Copy = 'Code',

    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$localDir = Join-Path $env:LOCALAPPDATA 'AlexaPc'
$cloudConfigPath = Join-Path $localDir 'cloud-relay.json'
$templatePath = Join-Path $repoRoot 'skill\alexa-hosted\index.template.js'
$modelPath = Join-Path $repoRoot 'skill\es-ES\interactionModel.json'
$outputPath = Join-Path $localDir 'alexa-hosted-index.js'
$utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-Utf8Text([string]$path) {
    try {
        $bytes = [System.IO.File]::ReadAllBytes($path)
        $text = $utf8Strict.GetString($bytes)
    }
    catch [System.Text.DecoderFallbackException] {
        throw "Invalid UTF-8 in ${path}. Nothing was copied to Alexa."
    }

    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) {
        return $text.Substring(1)
    }

    return $text
}

function Write-Utf8Text([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, $utf8NoBom)

    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF
    if ($hasBom) {
        throw "UTF-8 BOM detected after writing ${path}."
    }

    $roundTrip = Read-Utf8Text $path
    if (-not [string]::Equals($text, $roundTrip, [System.StringComparison]::Ordinal)) {
        throw "UTF-8 round-trip verification failed for ${path}."
    }
}

function Assert-NoMojibake([string]$text, [string]$source) {
    $markers = @(
        [string][char]0xFFFD,
        [string][char]0x00C3,
        [string][char]0x00C2,
        ([string][char]0x00E2 + [string][char]0x20AC),
        ([string][char]0x00EF + [string][char]0x00BF + [string][char]0x00BD)
    )

    foreach ($marker in $markers) {
        if ($text.Contains($marker)) {
            throw "Encoding problem detected while reading ${source}. Nothing was copied to Alexa."
        }
    }
}

function Set-VerifiedClipboard([string]$text) {
    Set-Clipboard -Value $text
    $roundTrip = Get-Clipboard -Raw

    if (-not [string]::Equals($text, $roundTrip, [System.StringComparison]::Ordinal)) {
        throw 'The Windows clipboard changed the text. Nothing should be pasted into Alexa.'
    }

    Assert-NoMojibake $roundTrip 'Windows clipboard'
}

if ($Copy -eq 'Model') {
    if (-not (Test-Path $modelPath)) {
        throw "Alexa model not found: $modelPath"
    }

    $model = Read-Utf8Text $modelPath
    Assert-NoMojibake $model $modelPath
    try {
        $model | ConvertFrom-Json | Out-Null
    }
    catch {
        throw "Alexa interaction model is not valid JSON: $($_.Exception.Message)"
    }

    if ($ValidateOnly) {
        Write-Host 'Alexa interaction model is valid UTF-8 JSON and contains no mojibake.' -ForegroundColor Green
        exit 0
    }

    Set-VerifiedClipboard $model
    Write-Host 'Alexa interaction model copied to clipboard.' -ForegroundColor Green
    Write-Host 'Alexa Developer Console: Build > Interaction Model > JSON Editor > Ctrl+A > Ctrl+V > Save Model > Build Model.'
    exit 0
}

if (-not (Test-Path $cloudConfigPath)) {
    throw "Cloud relay config not found: $cloudConfigPath. Run Deploy-CloudRelay.ps1 first."
}

if (-not (Test-Path $templatePath)) {
    throw "Alexa skill template not found: $templatePath"
}

$configText = Read-Utf8Text $cloudConfigPath
Assert-NoMojibake $configText $cloudConfigPath
$config = $configText | ConvertFrom-Json
if (-not $config.relayUrl -or -not $config.apiKey -or -not $config.deviceId) {
    throw 'cloud-relay.json must contain relayUrl, apiKey and deviceId.'
}

function To-JsJsonString([string]$value) {
    return ConvertTo-Json -Compress -InputObject $value
}

$code = Read-Utf8Text $templatePath
Assert-NoMojibake $code $templatePath
$code = $code.Replace('__RELAY_URL_JSON__', (To-JsJsonString ([string]$config.relayUrl)))
$code = $code.Replace('__RELAY_API_KEY_JSON__', (To-JsJsonString ([string]$config.apiKey)))
$code = $code.Replace('__DEVICE_ID_JSON__', (To-JsJsonString ([string]$config.deviceId)))
Assert-NoMojibake $code 'generated Alexa-hosted code'

if ($code.Contains('__RELAY_URL_JSON__') -or
    $code.Contains('__RELAY_API_KEY_JSON__') -or
    $code.Contains('__DEVICE_ID_JSON__')) {
    throw 'Generated Alexa-hosted code still contains an unresolved placeholder.'
}

if ($ValidateOnly) {
    Write-Host 'Alexa-hosted code is valid UTF-8, contains no mojibake and has no unresolved placeholders.' -ForegroundColor Green
    exit 0
}

New-Item -ItemType Directory -Path $localDir -Force | Out-Null
Write-Utf8Text $outputPath $code
Set-VerifiedClipboard $code

Write-Host 'Alexa skill code copied to clipboard with correct UTF-8 encoding.' -ForegroundColor Green
Write-Host "Local copy: $outputPath"
Write-Host 'This local file contains the private relay key. Do not commit it.' -ForegroundColor Yellow
Write-Host 'Alexa Developer Console: Code > lambda > index.js > Ctrl+A > Ctrl+V > Save > Deploy.'
