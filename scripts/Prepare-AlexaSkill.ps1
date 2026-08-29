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
    return Get-Content -Path $path -Raw -Encoding UTF8
}

function Assert-NoMojibake([string]$text, [string]$source) {
    if ($text -match 'Ã|Â') {
        throw "He detectado texto mal codificado al leer $source. No voy a copiarlo a Alexa."
    }
}

if ($Copy -eq 'Model') {
    if (-not (Test-Path $modelPath)) {
        throw "No encuentro el modelo de Alexa: $modelPath"
    }

    $model = Read-Utf8Text $modelPath
    Assert-NoMojibake $model $modelPath
    Set-Clipboard -Value $model
    Write-Host 'Modelo de interacción copiado al portapapeles.' -ForegroundColor Green
    Write-Host 'En Alexa Developer Console: Build > Interaction Model > JSON Editor > Ctrl+A > Ctrl+V > Save Model > Build Model.'
    exit 0
}

if (-not (Test-Path $cloudConfigPath)) {
    throw "No encuentro $cloudConfigPath. Ejecuta primero Deploy-CloudRelay.ps1."
}

if (-not (Test-Path $templatePath)) {
    throw "No encuentro la plantilla de la Skill: $templatePath"
}

$config = Read-Utf8Text $cloudConfigPath | ConvertFrom-Json
if (-not $config.relayUrl -or -not $config.apiKey -or -not $config.deviceId) {
    throw 'cloud-relay.json no contiene relayUrl, apiKey y deviceId.'
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
Set-Content -Path $outputPath -Value $code -Encoding UTF8
Set-Clipboard -Value $code

Write-Host 'Código de la Skill copiado al portapapeles en UTF-8 correcto.' -ForegroundColor Green
Write-Host "También lo he guardado localmente en: $outputPath"
Write-Host 'Ese archivo contiene tu clave privada del relay: no lo subas a GitHub.' -ForegroundColor Yellow
Write-Host 'En Alexa Developer Console: Code > lambda > index.js > Ctrl+A > Ctrl+V > Save > Deploy.'
