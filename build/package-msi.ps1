<#
.SYNOPSIS
  Builds and signs the MSI installer for Hisui PDF (Windows x64).

.DESCRIPTION
  1. Installs the WiX v4 dotnet tool if not already present.
  2. Runs dotnet publish (win-x64 profile).
  3. Builds the WiX project → bin/installer/HisuiPDF-{Version}-win-x64.msi
  4. Signs the MSI with signtool.exe.

.PARAMETER Version
  Version string embedded in the MSI (default: 1.0.0).

.PARAMETER CertPath
  Path to a PFX code-signing certificate (default: build\dev-cert.pfx).

.PARAMETER CertPassword
  Password for the PFX (plain text, default: dev).

.PARAMETER GenerateCert
  Generate a self-signed dev cert at build\dev-cert.pfx if none is found.
  Valid for 5 years, password "dev". For production use a CA-issued EV cert.

.PARAMETER DownloadSdk
  Download signtool.exe from NuGet Microsoft.Windows.SDK.BuildTools into
  build/tools/winsdk/ — no Windows SDK installation required.

.EXAMPLE
  # First time — generate cert + download SDK tools if needed
  .\build\package-msi.ps1 -Version 1.0.0 -GenerateCert -DownloadSdk

  # Subsequent runs (cert + tools already present)
  .\build\package-msi.ps1 -Version 1.0.0 -GenerateCert

  # Production — CA-issued cert
  .\build\package-msi.ps1 -Version 1.0.0 -CertPath build\release.pfx -CertPassword "s3cr3t"
#>
param(
    [string]$Version      = "1.0.0",
    [string]$CertPath     = "",
    [string]$CertPassword = "dev",
    [switch]$GenerateCert,
    [switch]$DownloadSdk
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# ---- helpers (shared with package-win.ps1) ----------------------------------

function Install-WinSdkBuildTools {
    $pkgId  = "Microsoft.Windows.SDK.BuildTools"
    $pkgVer = "10.0.26100.1742"
    $toolsDir = "$root\build\tools\winsdk"
    if (Test-Path "$toolsDir\bin") {
        Write-Host "  Windows SDK build tools already in $toolsDir"
        return $toolsDir
    }
    Write-Host "  Downloading $pkgId $pkgVer from NuGet..."
    $nupkg = "$root\build\tools\$pkgId.$pkgVer.nupkg"
    $url   = "https://api.nuget.org/v3-flatcontainer/$($pkgId.ToLower())/$pkgVer/$($pkgId.ToLower()).$pkgVer.nupkg"
    New-Item -ItemType Directory -Path "$root\build\tools" -Force | Out-Null
    Invoke-WebRequest -Uri $url -OutFile $nupkg -UseBasicParsing
    Expand-Archive -Path $nupkg -DestinationPath $toolsDir -Force
    Remove-Item $nupkg -Force
    Write-Host "  Extracted to: $toolsDir"
    return $toolsDir
}

function Find-WinSdkTool([string]$Name) {
    # 1. System Windows Kits
    $kitsBase = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (Test-Path $kitsBase) {
        $tool = Get-ChildItem -Path $kitsBase -Recurse -Filter $Name -ErrorAction SilentlyContinue |
                Where-Object { $_.DirectoryName -match "x64" } |
                Sort-Object { try { [version]($_.DirectoryName -replace '.*\\(\d+\.\d+\.\d+\.\d+)\\.*','$1') } catch { [version]"0.0.0.0" } } -Descending |
                Select-Object -First 1
        if ($tool) { return $tool.FullName }
    }
    # 2. NuGet global packages cache
    $nugetCache = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools"
    if (Test-Path $nugetCache) {
        $tool = Get-ChildItem -Path $nugetCache -Recurse -Filter $Name -ErrorAction SilentlyContinue |
                Where-Object { $_.DirectoryName -match "x64" } |
                Sort-Object DirectoryName -Descending |
                Select-Object -First 1
        if ($tool) { return $tool.FullName }
    }
    # 3. Local build/tools (downloaded by -DownloadSdk)
    $localTools = "$root\build\tools\winsdk"
    if (Test-Path $localTools) {
        $tool = Get-ChildItem -Path $localTools -Recurse -Filter $Name -ErrorAction SilentlyContinue |
                Where-Object { $_.DirectoryName -match "x64" } |
                Select-Object -First 1
        if ($tool) { return $tool.FullName }
    }
    throw "Cannot find $Name. Run with -DownloadSdk to fetch it from NuGet automatically."
}

function Get-OrCreateDevCert([string]$PfxPath) {
    if (Test-Path $PfxPath) {
        Write-Host "  Using existing dev cert: $PfxPath"
        return $PfxPath
    }
    Write-Host "  Generating self-signed development certificate..."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigning `
        -Subject "CN=Emanuele Filardo" `
        -KeyUsage DigitalSignature `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter (Get-Date).AddYears(5) `
        -HashAlgorithm SHA256
    $secPwd = ConvertTo-SecureString -String "dev" -Force -AsPlainText
    New-Item -ItemType Directory -Path (Split-Path $PfxPath -Parent) -Force | Out-Null
    Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $secPwd | Out-Null
    Write-Host "  Saved: $PfxPath  (password: dev)"
    Write-Host ""
    Write-Host "  To trust this cert (run once as admin):"
    Write-Host "    certutil -addstore TrustedPeople `"$PfxPath`""
    return $PfxPath
}

# ---- 1. locate signtool ------------------------------------------------------

Write-Host "Locating signtool.exe..."
if ($DownloadSdk) { Install-WinSdkBuildTools | Out-Null }
$signtool = Find-WinSdkTool "signtool.exe"
Write-Host "  Found: $signtool"

# ---- 2. resolve certificate --------------------------------------------------

if (-not $CertPath) { $CertPath = "$root\build\dev-cert.pfx" }
$CertPath = [System.IO.Path]::GetFullPath($CertPath)

if ($GenerateCert) {
    Write-Host ""
    Write-Host "Resolving code-signing certificate..."
    $CertPath = Get-OrCreateDevCert $CertPath
} elseif (-not (Test-Path $CertPath)) {
    throw "Certificate not found: $CertPath`nRun with -GenerateCert to create a dev cert, or supply -CertPath."
}

# ---- 3. ensure WiX toolset ---------------------------------------------------

Write-Host ""
Write-Host "Checking WiX toolset..."
$wixInstalled = & dotnet tool list --global 2>$null | Select-String "wix"
if (-not $wixInstalled) {
    Write-Host "  Installing WiX v4..."
    & dotnet tool install --global wix --version 4.0.5
    if ($LASTEXITCODE -ne 0) { throw "Failed to install WiX toolset" }
} else {
    Write-Host "  WiX already installed: $wixInstalled"
}

# ---- 4. publish win-x64 -----------------------------------------------------

Write-Host ""
Write-Host "Publishing win-x64..."
& dotnet publish "$root\src\Hisui.Pdf.App\Hisui.Pdf.App.csproj" /p:PublishProfile=win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# ---- 5. build MSI -----------------------------------------------------------

Write-Host ""
Write-Host "Building MSI (Version $Version)..."
$outDir  = "$root\bin\installer"
$msiPath = "$outDir\HisuiPDF-$Version-win-x64.msi"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

& dotnet build "$root\src\Hisui.Pdf.Installer.Win\Hisui.Pdf.Installer.Win.wixproj" `
    -c Release `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "WiX build failed" }

# ---- 6. sign MSI ------------------------------------------------------------

Write-Host ""
Write-Host "Signing MSI..."
& $signtool sign /fd SHA256 /f $CertPath /p $CertPassword /tr "http://timestamp.digicert.com" /td SHA256 $msiPath
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Timestamping failed (no internet?); retrying without timestamp..."
    & $signtool sign /fd SHA256 /f $CertPath /p $CertPassword $msiPath
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed" }
}

Write-Host ""
Write-Host "Output: $msiPath"
$size = (Get-Item $msiPath).Length / 1MB
Write-Host ("Size:   {0:N1} MB" -f $size)
Write-Host ""
Write-Host "Install interactively:"
Write-Host "  msiexec /i `"$msiPath`""
Write-Host ""
Write-Host "Install silently (admin):"
Write-Host "  msiexec /i `"$msiPath`" /qn"
