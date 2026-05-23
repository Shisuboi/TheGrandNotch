# install-uiaccess.ps1
# Publie en self-contained (win-x64), signe l'exe, installe dans Program Files.
# UIAccess exige : exe signe + repertoire securise (Program Files).
# A executer une seule fois en tant qu'Administrateur.

#Requires -RunAsAdministrator

$ProjectDir  = $PSScriptRoot
$InstallDir  = "C:\Program Files\TheGrandNotch"
$CertSubject = "CN=TheGrandNotch-Dev"
$ExeName     = "TheGrandNotch.exe"
$PublishDir  = "$ProjectDir\publish"

Write-Host "=== Publish self-contained Release x64 ===" -ForegroundColor Cyan
dotnet publish "$ProjectDir\TheGrandNotch.csproj" `
    /p:PublishProfile=Release-x64 `
    --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Error "Publish echoue"; exit 1 }

$ExePath = "$PublishDir\$ExeName"
if (-not (Test-Path $ExePath)) { Write-Error "Exe introuvable : $ExePath"; exit 1 }

# --- Certificat auto-signe ---
Write-Host "`n=== Certificat ===" -ForegroundColor Cyan
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq $CertSubject } | Select-Object -First 1
if (-not $cert) {
    Write-Host "Creation du certificat auto-signe..."
    $cert = New-SelfSignedCertificate `
        -Subject $CertSubject `
        -CertStoreLocation "Cert:\LocalMachine\My" `
        -KeyUsage DigitalSignature `
        -Type CodeSigningCert `
        -NotAfter (Get-Date).AddYears(10)
    # Ajouter aux racines de confiance (necessaire pour UIAccess sans test-signing)
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root","LocalMachine")
    $store.Open("ReadWrite")
    $store.Add($cert)
    $store.Close()
    Write-Host "Certificat cree et ajoute aux racines de confiance." -ForegroundColor Green
} else {
    Write-Host "Certificat existant : $($cert.Thumbprint)"
}

# --- Signature de l'exe dans publish\ ---
Write-Host "`n=== Signature ===" -ForegroundColor Cyan
$signResult = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert `
    -TimestampServer "http://timestamp.digicert.com" -ErrorAction SilentlyContinue
if ($signResult.Status -ne "Valid") {
    # Fallback sans timestamp si le serveur est inaccessible
    $signResult = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert
}
Write-Host "Signature : $($signResult.Status)"

# --- Installation dans Program Files ---
Write-Host "`n=== Installation dans $InstallDir ===" -ForegroundColor Cyan
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }

Copy-Item "$PublishDir\*" $InstallDir -Recurse -Force
Write-Host "Fichiers copies." -ForegroundColor Green

# Re-signer l'exe installe (la copie annule la signature Authenticode)
Set-AuthenticodeSignature -FilePath "$InstallDir\$ExeName" -Certificate $cert | Out-Null
Write-Host "Exe installe resigne." -ForegroundColor Green

Write-Host "`n=== OK ===" -ForegroundColor Green
Write-Host "Lance : $InstallDir\$ExeName"
Write-Host "La notch sera dans la bande ZBID_UIACCESS, au-dessus de tous les HWND_TOPMOST."
