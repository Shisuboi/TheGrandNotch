# install-uiaccess.ps1
# Signe l'exe et installe dans Program Files.
# UIAccess exige : exe signe + repertoire securise (Program Files).
# A executer une seule fois en tant qu'Administrateur.

#Requires -RunAsAdministrator

$SourceDir   = $PSScriptRoot
$InstallDir  = "C:\Program Files\TheGrandNotch"
$CertSubject = "CN=TheGrandNotch-Dev"
$ExeName     = "TheGrandNotch.exe"
$ExePath     = "$SourceDir\$ExeName"

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
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root","LocalMachine")
    $store.Open("ReadWrite")
    $store.Add($cert)
    $store.Close()
    Write-Host "Certificat cree et ajoute aux racines de confiance." -ForegroundColor Green
} else {
    Write-Host "Certificat existant : $($cert.Thumbprint)"
}

# --- Signature ---
Write-Host "`n=== Signature ===" -ForegroundColor Cyan
$signResult = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert `
    -TimestampServer "http://timestamp.digicert.com" -ErrorAction SilentlyContinue
if ($signResult.Status -ne "Valid") {
    $signResult = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert
}
Write-Host "Signature : $($signResult.Status)"

# --- Installation dans Program Files ---
Write-Host "`n=== Installation dans $InstallDir ===" -ForegroundColor Cyan
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }

Copy-Item "$SourceDir\*" $InstallDir -Recurse -Force -Exclude "install.bat","install-uiaccess.ps1"
Write-Host "Fichiers copies." -ForegroundColor Green

Set-AuthenticodeSignature -FilePath "$InstallDir\$ExeName" -Certificate $cert | Out-Null
Write-Host "Exe installe resigne." -ForegroundColor Green

Write-Host "`n=== OK ===" -ForegroundColor Green
Write-Host "Lance : $InstallDir\$ExeName"
