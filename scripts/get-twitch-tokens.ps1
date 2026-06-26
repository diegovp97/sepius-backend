# Script para obtener tokens de Twitch via Authorization Code Flow
# Ejecutar: .\get-twitch-tokens.ps1
#
# Pasos:
# 1. Abre la URL que el script genera en tu navegador
# 2. Autoriza la aplicacion
# 3. Twitch redirigira a http://localhost/?code=CODIGO&scope=...
# 4. Copia el CODIGO de la URL
# 5. Pega el codigo cuando el script te lo pida

param(
    [string]$ClientId = "700yf59xz5keizu85ih0aw36hbimk3",
    [string]$ClientSecret = "mkukfehbtmnf2tl5n47dcfd2h6q3ri",
    [string]$RedirectUri = "http://localhost"
)

$Scopes = "user:read:follows"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Twitch Authorization Code Flow" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 1. Generar URL de autorizacion
$authUrl = "https://id.twitch.tv/oauth2/authorize?" +
    "client_id=$ClientId" +
    "&redirect_uri=$([System.Uri]::EscapeDataString($RedirectUri))" +
    "&response_type=code" +
    "&scope=$([System.Uri]::EscapeDataString($Scopes))"

Write-Host "1. Abre esta URL en tu navegador:" -ForegroundColor Yellow
Write-Host ""
Write-Host $authUrl -ForegroundColor Green
Write-Host ""

# Abrir en navegador por defecto
Start-Process $authUrl

Write-Host "2. Autoriza la aplicacion en Twitch." -ForegroundColor Yellow
Write-Host "3. Copia el 'code' de la URL de redireccion (http://localhost/?code=XXXXXX&scope=...)" -ForegroundColor Yellow
Write-Host ""

# 2. Pedir el codigo
$code = Read-Host "Pega el authorization code aqui"

if ([string]::IsNullOrWhiteSpace($code)) {
    Write-Error "No se proporciono un codigo valido."
    exit 1
}

Write-Host ""
Write-Host "Intercambiando codigo por tokens..." -ForegroundColor Yellow

# 3. Intercambiar code por tokens
$body = @{
    client_id     = $ClientId
    client_secret = $ClientSecret
    code          = $code
    grant_type    = "authorization_code"
    redirect_uri  = $RedirectUri
}

try {
    $response = Invoke-RestMethod -Uri "https://id.twitch.tv/oauth2/token" -Method Post -Body $body
}
catch {
    Write-Error "Error al intercambiar el codigo: $_"
    exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Tokens obtenidos correctamente!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "User Access Token: $($response.access_token)" -ForegroundColor Cyan
Write-Host "Refresh Token:     $($response.refresh_token)" -ForegroundColor Cyan
Write-Host "Expira en:         $($response.expires_in) segundos" -ForegroundColor Cyan
Write-Host ""

# 4. Actualizar .env automaticamente
$envPath = Join-Path $PSScriptRoot "..\.env"

if (Test-Path $envPath) {
    $envContent = Get-Content $envPath -Raw

    # Reemplazar o agregar las variables
    if ($envContent -match "TWITCH_USER_ACCESS_TOKEN=") {
        $envContent = $envContent -replace "TWITCH_USER_ACCESS_TOKEN=.*", "TWITCH_USER_ACCESS_TOKEN=$($response.access_token)"
    } else {
        $envContent += "`nTWITCH_USER_ACCESS_TOKEN=$($response.access_token)"
    }

    if ($envContent -match "TWITCH_REFRESH_TOKEN=") {
        $envContent = $envContent -replace "TWITCH_REFRESH_TOKEN=.*", "TWITCH_REFRESH_TOKEN=$($response.refresh_token)"
    } else {
        $envContent += "`nTWITCH_REFRESH_TOKEN=$($response.refresh_token)"
    }

    Set-Content -Path $envPath -Value $envContent -NoNewline

    Write-Host ".env actualizado automaticamente." -ForegroundColor Green
    Write-Host ""
    Write-Host "Ahora sube los cambios a la VPS:" -ForegroundColor Yellow
    Write-Host '  scp -r .\sepius-backend root@46.224.11.233:/opt/sepius/' -ForegroundColor White
    Write-Host '  ssh root@46.224.11.233 "cd /opt/sepius/sepius-backend && docker compose down && docker compose up -d --build --force-recreate"' -ForegroundColor White
} else {
    Write-Host "No se encontro .env. Agrega manualmente:" -ForegroundColor Yellow
    Write-Host "  TWITCH_USER_ACCESS_TOKEN=$($response.access_token)" -ForegroundColor White
    Write-Host "  TWITCH_REFRESH_TOKEN=$($response.refresh_token)" -ForegroundColor White
}

Write-Host ""
