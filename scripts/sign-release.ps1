#requires -Version 7
<#
Signs the win-x64 self-contained exe with the local code-signing cert (AD CS),
then re-zips it. Run locally/on a domain machine after `dotnet publish` or
after unzipping a release asset — never in the GitHub Actions runner (no
access to the AD CS cert store).

Usage:
  scripts\sign-release.ps1 -Path publish\win-x64\Hisui.Pdf.App.exe
  scripts\sign-release.ps1 -Path Hisui.Pdf-v1.0.0-win-x64.zip
#>
param(
    [Parameter(Mandatory)] [string]$Path,
    [string]$Thumbprint = "d415c08768df6224e901ba77d251efbb4073af0e",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signtool) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*x64*' } | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK." }

function Sign-Exe([string]$exePath) {
    & $signtool sign /sha1 $Thumbprint /fd sha256 /tr $TimestampUrl /td sha256 $exePath
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $exePath" }
    & $signtool verify /pa $exePath
}

if ($Path -like '*.zip') {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid())
    Expand-Archive -Path $Path -DestinationPath $tmp
    $exe = Get-ChildItem $tmp -Filter Hisui.Pdf.App.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
    if (-not $exe) { throw "Hisui.Pdf.App.exe not found in $Path" }
    Sign-Exe $exe
    Remove-Item $Path -Force
    Compress-Archive -Path "$tmp\*" -DestinationPath $Path
    Remove-Item $tmp -Recurse -Force
}
else {
    Sign-Exe $Path
}
