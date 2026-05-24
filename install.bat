@echo off
:: Auto-elevation : relance ce script en administrateur si besoin
net session >nul 2>&1
if %errorLevel% neq 0 (
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo.
echo  Installation de TheGrandNotch...
echo.
powershell.exe -ExecutionPolicy Bypass -File "%~dp0install-uiaccess.ps1"

if %errorLevel% neq 0 (
    echo.
    echo  L'installation a echoue. Voir les messages ci-dessus.
    pause
    exit /b 1
)

exit /b 0
