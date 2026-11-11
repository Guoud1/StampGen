# Сборка StampGen в exe для Windows (self-contained).
# Требуется .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0
# Опция -Obfuscate: защита IL от реверс-инженеринга (Obfuscar, см. obfuscar.xml).
#   Перед первым использованием: dotnet tool install --global Obfuscar.GlobalTool
param([switch]$Obfuscate)
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot

Write-Host "=== Публикация StampGen (win-x64, один exe + wwwroot) ===" -ForegroundColor Cyan
dotnet publish StampGen\StampGen.csproj -c Release -r win-x64 -o publish\win-x64
if ($LASTEXITCODE -ne 0) { Pop-Location; exit 1 }

# v2.2: опциональная обфускация IL (приватные имена + скрытие строк).
# ВАЖНО: сборка перевыпускается БЕЗ single-file (Obfuscar работает с StampGen.dll),
# результат распространяется папкой publish\win-x64 (exe + dll + wwwroot) в zip.
if ($Obfuscate) {
    Write-Host "=== Обфускация IL (Obfuscar) ===" -ForegroundColor Cyan
    if (-not (Get-Command obfuscar.console -ErrorAction SilentlyContinue)) {
        Write-Host "Obfuscar не найден. Установите: dotnet tool install --global Obfuscar.GlobalTool" -ForegroundColor Red
        Pop-Location; exit 1
    }
    obfuscar.console obfuscar.xml
    if ($LASTEXITCODE -ne 0) { Pop-Location; exit 1 }
    Copy-Item "publish\win-x64-obf\StampGen.dll" "publish\win-x64\StampGen.dll" -Force
    Remove-Item "publish\win-x64-obf" -Recurse -Force
    Write-Host "Obfuscated StampGen.dll подложен в publish\win-x64." -ForegroundColor Green
    Write-Host "ОБЯЗАТЕЛЬНО проверьте работу exe (запуск, библиотеки, STL) перед распространением!" -ForegroundColor Yellow
}

$exe = Join-Path $PSScriptRoot "publish\win-x64\StampGen.exe"
if (Test-Path $exe) {
    Write-Host ""
    Write-Host "Готово! Запускайте: $exe" -ForegroundColor Green
    Write-Host "Браузер откроется сам. Папку publish\win-x64 можно заархивировать и распространять."
} else {
    Write-Host "publish\win-x64 готова. Ищите StampGen.exe в этой папке." -ForegroundColor Yellow
}
Pop-Location
