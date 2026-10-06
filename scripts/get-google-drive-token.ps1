<#
.SYNOPSIS
  Obtiene el refresh token de Google Drive (scope drive.file) para el pipeline de Sepius.

.DESCRIPTION
  Abre el navegador para que autorices la app, recibe el código en http://127.0.0.1:<puerto>/
  y lo cambia por un refresh token. El token se muestra SOLO en esta consola: no se guarda en
  ningún fichero. Cópialo al .env de la VPS como GOOGLE_DRIVE_REFRESH_TOKEN.

  Requisitos en Google Cloud Console (el mismo proyecto que usa YouTube):
    1. APIs y servicios > Biblioteca > "Google Drive API" > Habilitar.
    2. Pantalla de consentimiento OAuth > añadir el scope ".../auth/drive.file"
       y poner la app en "En producción" (si se queda en "Pruebas" el token caduca a los 7 días).
    3. El cliente OAuth debe ser de tipo "Aplicación de escritorio". Si es "Aplicación web",
       añade http://127.0.0.1:8765/ como URI de redirección autorizado.

.EXAMPLE
  .\scripts\get-google-drive-token.ps1
#>
param(
    [string]$ClientId,
    [string]$ClientSecret,
    [int]$Port = 8765
)

$ErrorActionPreference = 'Stop'

if (-not $ClientId) { $ClientId = Read-Host 'Client ID (el de tu proyecto de Google Cloud)' }
if (-not $ClientSecret) {
    $secure = Read-Host 'Client Secret' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $ClientSecret = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$redirect = "http://127.0.0.1:$Port/"
$scope = 'https://www.googleapis.com/auth/drive.file'
$state = [guid]::NewGuid().ToString('N')

$authUrl = 'https://accounts.google.com/o/oauth2/v2/auth' +
    '?client_id=' + [uri]::EscapeDataString($ClientId) +
    '&redirect_uri=' + [uri]::EscapeDataString($redirect) +
    '&response_type=code' +
    '&scope=' + [uri]::EscapeDataString($scope) +
    '&access_type=offline' +
    '&prompt=consent' +
    '&state=' + $state

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add($redirect)
$listener.Start()

Write-Host ''
Write-Host 'Se abre el navegador. Inicia sesión con la cuenta de Google donde quieres guardar las copias.' -ForegroundColor Yellow
Write-Host "(Si no se abre, copia esta URL):`n$authUrl`n"
Start-Process $authUrl

try {
    $pending = $listener.GetContextAsync()
    if (-not $pending.Wait(300000)) { throw 'Tiempo agotado (5 min) esperando la autorización.' }
    $ctx = $pending.Result

    $query = $ctx.Request.QueryString
    $html = '<html><body style="font-family:sans-serif;background:#111;color:#eee;padding:40px">' +
        '<h2>Listo. Puedes cerrar esta pestaña y volver a la consola.</h2></body></html>'
    $bytes = [Text.Encoding]::UTF8.GetBytes($html)
    $ctx.Response.ContentType = 'text/html; charset=utf-8'
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $ctx.Response.Close()
}
finally {
    $listener.Stop()
}

if ($query['error']) { throw "Google devolvió un error: $($query['error'])" }
if ($query['state'] -ne $state) { throw 'El parámetro state no coincide; se aborta por seguridad.' }
$code = $query['code']
if (-not $code) { throw 'No llegó ningún código de autorización.' }

$tokens = Invoke-RestMethod -Method Post -Uri 'https://oauth2.googleapis.com/token' -Body @{
    code          = $code
    client_id     = $ClientId
    client_secret = $ClientSecret
    redirect_uri  = $redirect
    grant_type    = 'authorization_code'
}

if (-not $tokens.refresh_token) {
    throw ('Google no devolvió refresh_token. Revoca el acceso previo de la app en ' +
           'https://myaccount.google.com/permissions y vuelve a ejecutar el script.')
}

Write-Host ''
Write-Host "Scope concedido: $($tokens.scope)" -ForegroundColor Green
Write-Host ''
Write-Host 'REFRESH TOKEN (cópialo ahora; no se guarda en ningún sitio):' -ForegroundColor Cyan
Write-Host $tokens.refresh_token
Write-Host ''
Write-Host 'Siguiente paso: añade estas líneas al .env de la VPS (/opt/sepius/sepius-backend/.env):' -ForegroundColor Yellow
Write-Host '  GOOGLE_DRIVE_ENABLED=true'
Write-Host '  GOOGLE_DRIVE_REFRESH_TOKEN=<el token de arriba>'
Write-Host '(El Client ID/Secret se reutilizan de YouTube si no defines GOOGLE_DRIVE_CLIENT_ID/SECRET.)'
