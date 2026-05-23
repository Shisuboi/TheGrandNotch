@echo off
:: Verifie les droits admin
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo.
    echo  Erreur : ce script doit etre execute en tant qu'Administrateur.
    echo  Clic droit sur install.bat ^> "Executer en tant qu'administrateur"
    echo.
    pause
    exit /b 1
)

echo Installation de TheGrandNotch...
powershell.exe -ExecutionPolicy Bypass -File "%~dp0install-uiaccess.ps1"

if %errorLevel% neq 0 (
    echo.
    echo  L'installation a echoue. Voir les messages ci-dessus.
    pause
    exit /b 1
)

pause
