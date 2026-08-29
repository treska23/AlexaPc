param(
    [ValidateSet('Code', 'Model')]
    [string]$Copy = 'Code'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$localDir = Join-Path $env:LOCALAPPDATA 'AlexaPc'
$cloudConfigPath = Join-Path $localDir 'cloud-relay.json'
$templatePath = Join-Path $repoRoot 'skill\alexa-hosted\index.template.js'
$modelPath = Join-Path $repoRoot 'skill\es-ES\interactionModel.json'
$outputPath = Join-Path $localDir 'alexa-hosted-index.js'

function Read-Utf8Text([string]$path) {
    return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}

function Write-Utf8Text([string]$path, [string]$text) {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($path, $text, $utf8NoBom)
}

function Assert-NoMojibake([string]$text, [string]$source) {
    $badC3 = [string][char]0x00C3
    $badC2 = [string][char]0x00C2
    if ($text.Contains($badC3) -or $text.Contains($badC2)) {
        throw "Encoding problem detected while reading $source. Nothing was copied to Alexa."
    }
}

if ($Copy -eq 'Model') {
    if (-not (Test-Path $modelPath)) {
        throw "Alexa model not found: $modelPath"
    }

    $model = Read-Utf8Text $modelPath
    Assert-NoMojibake $model $modelPath
    Set-Clipboard -Value $model
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

$config = Read-Utf8Text $cloudConfigPath | ConvertFrom-Json
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

New-Item -ItemType Directory -Path $localDir -Force | Out-Null
Write-Utf8Text $outputPath $code
Set-Clipboard -Value $code

Write-Host 'Alexa skill code copied to clipboard with correct UTF-8 encoding.' -ForegroundColor Green
Write-Host "Local copy: $outputPath"
Write-Host 'This local file contains the private relay key. Do not commit it.' -ForegroundColor Yellow
Write-Host 'Alexa Developer Console: Code > lambda > index.js > Ctrl+A > Ctrl+V > Save > Deploy.'
