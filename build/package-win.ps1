<#
.SYNOPSIS
  Packages Hisui PDF as an MSIX installer for Windows x64.

.PARAMETER Version
  App version string (default: 1.0.0). MSIX requires 4-part format; ".0" is appended automatically.

.PARAMETER CertPath
  Path to a PFX code-signing certificate.
  The certificate Subject must be "CN=Emanuele Filardo" to match Package.appxmanifest.

.PARAMETER CertPassword
  Password for the PFX (plain text). Required when -CertPath is supplied.

.PARAMETER GenerateCert
  Generate a self-signed development certificate at build/dev-cert.pfx if none is found.
  The generated cert is valid for 5 years with password "dev".
  For production, obtain an EV Code Signing certificate from DigiCert or Sectigo.

.PARAMETER DownloadSdk
  Download makeappx.exe/signtool.exe from the NuGet package Microsoft.Windows.SDK.BuildTools
  into build/tools/winsdk/ — no Windows SDK installation required. Run once; cached on disk.

.EXAMPLE
  # First time / no Windows SDK installed — download tools + generate cert
  .\build\package-win.ps1 -Version 1.0.0 -GenerateCert -DownloadSdk

  # Subsequent runs (tools already cached)
  .\build\package-win.ps1 -Version 1.0.0 -GenerateCert

  # Production — use a CA-issued cert
  .\build\package-win.ps1 -Version 1.0.0 -CertPath build\release.pfx -CertPassword "s3cr3t"
#>
param(
    [string]$Version      = "1.0.0",
    [string]$CertPath     = "",
    [string]$CertPassword = "",
    [switch]$GenerateCert,
    [switch]$DownloadSdk
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# ---- helpers ----------------------------------------------------------------

# Downloads Microsoft.Windows.SDK.BuildTools from NuGet and extracts it to build/tools/.
# This provides makeappx.exe and signtool.exe without requiring a full Windows SDK install.
function Install-WinSdkBuildTools {
    $pkgId  = "Microsoft.Windows.SDK.BuildTools"
    $pkgVer = "10.0.26100.1742"    # Windows 11 SDK 26100; update as needed
    $toolsDir = "$root\build\tools\winsdk"

    if (Test-Path "$toolsDir\bin") {
        Write-Host "  Windows SDK build tools already in $toolsDir"
        return $toolsDir
    }

    Write-Host "  Downloading $pkgId $pkgVer from NuGet..."
    $nupkg  = "$root\build\tools\$pkgId.$pkgVer.nupkg"
    $url    = "https://api.nuget.org/v3-flatcontainer/$($pkgId.ToLower())/$pkgVer/$($pkgId.ToLower()).$pkgVer.nupkg"
    New-Item -ItemType Directory -Path "$root\build\tools" -Force | Out-Null
    Invoke-WebRequest -Uri $url -OutFile $nupkg -UseBasicParsing
    Expand-Archive -Path $nupkg -DestinationPath $toolsDir -Force
    Remove-Item $nupkg -Force
    Write-Host "  Extracted to: $toolsDir"
    return $toolsDir
}

function Find-WinSdkTool([string]$Name) {
    # 1. System Windows Kits (versioned subdirs, e.g. 10.0.22621.0\x64)
    $kitsBase = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (Test-Path $kitsBase) {
        $tool = Get-ChildItem -Path $kitsBase -Recurse -Filter $Name -ErrorAction SilentlyContinue |
                Where-Object { $_.DirectoryName -match "x64" } |
                Sort-Object { try { [version]($_.DirectoryName -replace '.*\\(\d+\.\d+\.\d+\.\d+)\\.*','$1') } catch { [version]"0.0.0.0" } } -Descending |
                Select-Object -First 1
        if ($tool) { return $tool.FullName }
    }

    # 2. NuGet global packages cache (populated by VS or manual dotnet add package)
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

    $hint = @"
Cannot find $Name.

Options to fix:
  A) Install Windows SDK 10.0.19041+ :
       winget install --id Microsoft.WindowsSDK.10.0.26100 --silent
  B) Let this script download the tools from NuGet (no install required):
       .\build\package-win.ps1 -DownloadSdk [other params]
"@
    throw $hint
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
    Write-Host "  To trust this cert for sideload install, run once as admin:"
    Write-Host "    certutil -addstore TrustedPeople `"$PfxPath`""
    return $PfxPath
}

# ---- locate SDK tools -------------------------------------------------------

Write-Host "Locating Windows SDK tools..."
if ($DownloadSdk) {
    Write-Host "  Downloading SDK build tools (NuGet)..."
    Install-WinSdkBuildTools | Out-Null
}
$makeappx = Find-WinSdkTool "makeappx.exe"
$signtool  = Find-WinSdkTool "signtool.exe"
Write-Host "  makeappx : $makeappx"
Write-Host "  signtool : $signtool"

# ---- 1. publish -------------------------------------------------------------

Write-Host ""
Write-Host "Publishing win-x64..."
& dotnet publish "$root\src\Hisui.Pdf.App\Hisui.Pdf.App.csproj" /p:PublishProfile=win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$publishDir = "$root\bin\publish\win-x64"

# ---- 2. check icon assets ---------------------------------------------------

$appxAssetsDir = "$root\src\Hisui.Pdf.App\Assets\appx"
$requiredAssets = @("Square44x44Logo.png","Square150x150Logo.png","Wide310x150Logo.png","StoreLogo.png","SplashScreen.png")
foreach ($asset in $requiredAssets) {
    if (-not (Test-Path "$appxAssetsDir\$asset")) {
        throw "Missing icon: $appxAssetsDir\$asset`nRun: .\build\create-icons.ps1"
    }
}

# ---- 3. staging -------------------------------------------------------------

Write-Host ""
Write-Host "Staging MSIX layout..."
$staging = "$root\build\.staging\win"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path "$staging\Assets" -Force | Out-Null

# Copy EXE only — .pdb excluded (debug symbols), empty dirs excluded
# tessdata is bundled inside the single-file EXE (self-extracting at first run)
Copy-Item "$publishDir\Hisui.Pdf.App.exe" $staging -Force

# License files alongside the app in the package
foreach ($licFile in @("LICENSE","THIRD-PARTY-NOTICES.txt")) {
    $src = "$root\$licFile"
    if (Test-Path $src) { Copy-Item $src $staging -Force }
}
$licDir = "$root\licenses"
if (Test-Path $licDir) {
    New-Item -ItemType Directory -Path "$staging\licenses" -Force | Out-Null
    Copy-Item "$licDir\*.txt" "$staging\licenses\" -Force
}

# Assets
Copy-Item "$appxAssetsDir\*.png" "$staging\Assets\" -Force

# Manifest — makeappx.exe requires AppxManifest.xml, UTF-8 without BOM, 4-part version
$appxVer = "$Version.0"
[xml]$xmlDoc = Get-Content "$root\src\Hisui.Pdf.App\Package.appxmanifest" -Raw
$xmlDoc.Package.Identity.Version = $appxVer
$xmlSettings = New-Object System.Xml.XmlWriterSettings
$xmlSettings.Indent = $true
$xmlSettings.Encoding = New-Object System.Text.UTF8Encoding($false)
$stream = New-Object System.IO.MemoryStream
$xw = [System.Xml.XmlWriter]::Create($stream, $xmlSettings)
$xmlDoc.Save($xw); $xw.Flush()
[System.IO.File]::WriteAllBytes("$staging\AppxManifest.xml", $stream.ToArray())

# ---- 4. pack ----------------------------------------------------------------

Write-Host ""
Write-Host "Packing MSIX..."
$outDir  = "$root\bin\installer"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$msixOut = "$outDir\HisuiPDF-$Version-win-x64.msix"

& $makeappx pack /d $staging /p $msixOut /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }

# ---- 5. sign ----------------------------------------------------------------

if (-not $CertPath) {
    if ($GenerateCert) {
        $CertPath     = "$root\build\dev-cert.pfx"
        $CertPassword = "dev"
        Write-Host ""
        Write-Host "Certificate..."
        $CertPath = Get-OrCreateDevCert $CertPath
    } else {
        Write-Warning "No certificate provided and -GenerateCert not set. Package is unsigned."
        Write-Host ""
        Write-Host "Output (unsigned): $msixOut"
        exit 0
    }
}

Write-Host ""
Write-Host "Signing MSIX..."
$signArgs = @("sign", "/fd", "SHA256", "/f", $CertPath)
if ($CertPassword) { $signArgs += @("/p", $CertPassword) }
$signArgs += $msixOut
& $signtool @signArgs
if ($LASTEXITCODE -ne 0) { throw "signtool sign failed" }

Write-Host ""
Write-Host "Output: $msixOut"
Write-Host ""
Write-Host "Install (sideload):"
Write-Host "  Add-AppxPackage `"$msixOut`""
Write-Host "  # Note: the signing cert must be trusted on the target machine."
