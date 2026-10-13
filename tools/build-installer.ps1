# Gera artifacts/GameStream-Setup-<versão>.exe
#   1) publica o app "self-contained" (com o .NET embutido: quem instala não precisa do .NET)
#   2) compila o instalador com o Inno Setup
# Uso: powershell -ExecutionPolicy Bypass -File tools/build-installer.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'artifacts\app'

$version = ([xml](Get-Content (Join-Path $root 'GameStream\GameStream.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = '0.1.0' }

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }

$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 não encontrado. Instale com: winget install JRSoftware.InnoSetup' }

Write-Host "Publicando GameStream $version (self-contained)..."
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
& $dotnet publish (Join-Path $root 'GameStream') -c Release -r win-x64 --self-contained true -o $app -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou' }
Get-ChildItem $app -Filter *.pdb | Remove-Item -Force

# Assinatura digital (opcional, mas necessária para o Smart App Control do Windows 11 não bloquear
# o app e para o SmartScreen parar de avisar). Defina a impressão digital do certificado de
# assinatura de código instalado neste PC:
#   $env:GAMESTREAM_SIGN_THUMBPRINT = '<thumbprint>'
#   $env:GAMESTREAM_SIGN_TIMESTAMP  = 'http://time.certum.pl'   # opcional (padrão: DigiCert)
function Sign-Files([string[]]$files) {
    $thumb = $env:GAMESTREAM_SIGN_THUMBPRINT
    if (-not $thumb) { return $false }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe não encontrado. Instale o Windows SDK (winget install Microsoft.WindowsSDK.10.0.26100).' }
    $ts = if ($env:GAMESTREAM_SIGN_TIMESTAMP) { $env:GAMESTREAM_SIGN_TIMESTAMP } else { 'http://timestamp.digicert.com' }
    & $signtool.FullName sign /sha1 $thumb /fd SHA256 /tr $ts /td SHA256 /d 'GameStream' $files
    if ($LASTEXITCODE -ne 0) { throw 'A assinatura digital falhou' }
    return $true
}

$signed = Sign-Files @((Join-Path $app 'GameStream.exe'), (Join-Path $app 'GameStream.dll'))

Write-Host 'Compilando o instalador...'
& $iscc /Q "/DAppVersion=$version" (Join-Path $root 'installer\GameStream.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup falhou' }

$setup = Join-Path $root "artifacts\GameStream-Setup-$version.exe"
if ($signed) { [void](Sign-Files @($setup)) }
else { Write-Warning 'Instalador SEM assinatura digital: PCs com o Smart App Control ligado vão bloquear o app.' }
Write-Host ("Pronto: {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
