$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$workerDir = Join-Path $repoRoot 'cloudflare\worker'
$localDir = Join-Path $env:LOCALAPPDATA 'AlexaPc'
$relayConfigPath = Join-Path $localDir 'relay.json'
$cloudConfigPath = Join-Path $localDir 'cloud-relay.json'

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw 'No encuentro npm. Instala Node.js LTS y vuelve a ejecutar este script.'
}

Write-Host 'Preparando AlexaPc Cloud Relay...' -ForegroundColor Cyan
Push-Location $workerDir
try {
    npm install
    if ($LASTEXITCODE -ne 0) { throw 'npm install ha fallado.' }

    npx wrangler whoami *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Se abrirá Cloudflare para iniciar sesión una sola vez.' -ForegroundColor Yellow
        npx wrangler login
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo iniciar sesión en Cloudflare.' }
    }

    Write-Host 'Creando el Worker...' -ForegroundColor Cyan
    $firstDeploy = (& npx wrangler deploy 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "No se pudo desplegar el Worker.`n$firstDeploy" }

    $apiKey = ([guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N'))
    $deviceToken = ([guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N'))

    $apiKey | npx wrangler secret put RELAY_API_KEY
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo guardar RELAY_API_KEY.' }

    $deviceToken | npx wrangler secret put DEVICE_TOKEN
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo guardar DEVICE_TOKEN.' }

    $deployOutput = (& npx wrangler deploy 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "No se pudo completar el despliegue.`n$deployOutput" }

    $allOutput = $firstDeploy + "`n" + $deployOutput
    $match = [regex]::Match($allOutput, 'https://[a-zA-Z0-9.-]+\.workers\.dev')
    if (-not $match.Success) {
        Write-Host $deployOutput
        throw 'El Worker se desplegó, pero no pude detectar automáticamente su URL workers.dev.'
    }

    $httpsUrl = $match.Value.TrimEnd('/')
    $wsUrl = ($httpsUrl -replace '^https://', 'wss://') + '/ws/agent'

    New-Item -ItemType Directory -Path $localDir -Force | Out-Null

    $deviceId = 'pc-principal'
    if (Test-Path $relayConfigPath) {
        try {
            $oldConfig = Get-Content $relayConfigPath -Raw | ConvertFrom-Json
            if ($oldConfig.deviceId) { $deviceId = [string]$oldConfig.deviceId }
        } catch {
        }
    }

    [ordered]@{
        enabled = $true
        relayUrl = $wsUrl
        deviceId = $deviceId
        deviceToken = $deviceToken
    } | ConvertTo-Json | Set-Content -Path $relayConfigPath -Encoding UTF8

    [ordered]@{
        relayUrl = $httpsUrl
        apiKey = $apiKey
        deviceId = $deviceId
        deviceToken = $deviceToken
    } | ConvertTo-Json | Set-Content -Path $cloudConfigPath -Encoding UTF8

    Write-Host ''
    Write-Host 'AlexaPc Cloud Relay desplegado.' -ForegroundColor Green
    Write-Host "HTTPS: $httpsUrl" -ForegroundColor Green
    Write-Host "WSS:   $wsUrl" -ForegroundColor Green
    Write-Host ''
    Write-Host "He actualizado: $relayConfigPath"
    Write-Host "He guardado la configuración privada para la Skill en: $cloudConfigPath"
    Write-Host 'Reinicia AlexaPc para que se conecte al relay cloud.' -ForegroundColor Yellow
}
finally {
    Pop-Location
}
