<#
  Builds every CPUx release file into artifacts\:
    installer\CPUx-Setup-<version>-x64.msi    Windows installer (double-click to install; includes .NET)
    store\CPUx_<version>.0_x64.msix            Package to upload to Microsoft Store (Partner Center)
    portable\CPUx.exe                          Single-file exe that needs the .NET 8 Desktop Runtime

  Usage (Windows PowerShell or pwsh, from the repository root):
    powershell -ExecutionPolicy Bypass -File packaging\build.ps1
    powershell -ExecutionPolicy Bypass -File packaging\build.ps1 -Only Msi
    powershell -ExecutionPolicy Bypass -File packaging\build.ps1 -TestSign   # also sign the MSIX for sideload testing

  Only the .NET 8 SDK is required; WiX and the Windows SDK build tools are fetched from NuGet.
#>
param(
    [ValidateSet('All', 'Msi', 'Msix', 'Portable')] [string]$Only = 'All',
    [switch]$TestSign,
    # Adds the allowElevation capability to the MSIX (only after Microsoft has agreed to approve it).
    [switch]$AllowElevation
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src\CPUx\CPUx.csproj'
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Run($exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}
# For the chatty SDK tools: output is shown only when they fail (makepri writes UTF-16, hence the NUL cleanup).
function RunQuiet($exe, [string[]]$arguments) {
    $output = & $exe @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        ($output | ForEach-Object { "$_" -replace "`0", '' } | Where-Object { $_.Trim() }) | Select-Object -Last 15 | Write-Host
        throw "$(Split-Path $exe -Leaf) failed with exit code $LASTEXITCODE"
    }
}

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$identity = Get-Content (Join-Path $PSScriptRoot 'store\identity.json') -Raw | ConvertFrom-Json
$manufacturer = if ($identity.PublisherDisplayName -and $identity.PublisherDisplayName -ne 'Your publisher name') { $identity.PublisherDisplayName } else { 'CPUx' }
Write-Host "CPUx $version"

# Builds go to artifacts\ (not bin\), so a copy of CPUx that is running from bin\ or dist\ doesn't block them.
if ($Only -in 'All', 'Msi', 'Msix') {
    Step 'Publishing self-contained build (app + .NET runtime)'
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    Run dotnet @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-nologo', '-v', 'q',
        '-p:PublishSingleFile=false', '-p:DebugType=none', "-p:OutputPath=$artifacts\build\selfcontained\", '-o', $publish)
    $mb = [Math]::Round(((Get-ChildItem $publish -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
    Write-Host "  $((Get-ChildItem $publish -Recurse -File).Count) files, $mb MB"
}

if ($Only -in 'All', 'Msi') {
    Step 'Building Windows Installer package (WiX)'
    $out = Join-Path $artifacts 'installer'
    Run dotnet @('build', (Join-Path $PSScriptRoot 'msi\CPUx.Installer.wixproj'), '-c', 'Release', '-nologo', '-v', 'q',
        "-p:Version=$version", "-p:PublishDir=$publish\", "-p:Manufacturer=$manufacturer", '-o', $out)
    Get-ChildItem $out -Filter *.wixpdb | Remove-Item
    Get-ChildItem $out -Filter *.msi | ForEach-Object { Write-Host ("  {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
}

if ($Only -in 'All', 'Msix') {
    Step 'Building MSIX package for Microsoft Store'
    Run dotnet @('restore', (Join-Path $PSScriptRoot 'tools\BuildTools.proj'), '-nologo', '-v', 'q')
    $nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $tools = Get-ChildItem (Join-Path $nuget 'microsoft.windows.sdk.buildtools') -Recurse -Filter makeappx.exe |
        Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1
    if (-not $tools) { throw 'makeappx.exe not found in the Microsoft.Windows.SDK.BuildTools package' }
    $bin = $tools.DirectoryName

    $layout = Join-Path $artifacts 'msix-layout'
    $priStage = Join-Path $artifacts 'msix-pri'
    foreach ($d in $layout, $priStage) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
    New-Item -ItemType Directory -Force $layout, $priStage | Out-Null

    $msixVersion = "$version.0" # the Store requires the fourth part to be 0
    $manifest = (Get-Content (Join-Path $PSScriptRoot 'msix\AppxManifest.template.xml') -Raw).
        Replace('{{DisplayName}}', [Security.SecurityElement]::Escape($(if ($identity.DisplayName) { $identity.DisplayName } else { 'CPUx' }))).
        Replace('{{IdentityName}}', $identity.IdentityName).
        Replace('{{Publisher}}', $identity.Publisher).
        Replace('{{PublisherDisplayName}}', [Security.SecurityElement]::Escape($identity.PublisherDisplayName)).
        Replace('{{Version}}', $msixVersion).
        Replace('{{ElevationCapability}}', $(if ($AllowElevation) { '    <rescap:Capability Name="allowElevation" />' } else { '' }))
    $utf8 = New-Object Text.UTF8Encoding $false

    # resources.pri only needs the images, so it is built from a small staging folder instead of all 300 runtime files.
    Copy-Item (Join-Path $PSScriptRoot 'msix\Assets') (Join-Path $priStage 'Assets') -Recurse
    [IO.File]::WriteAllText((Join-Path $priStage 'AppxManifest.xml'), $manifest, $utf8)
    Push-Location $priStage
    try {
        RunQuiet (Join-Path $bin 'makepri.exe') @('createconfig', '/cf', 'priconfig.xml', '/dq', 'en-US', '/pv', '10.0.0', '/o')
        RunQuiet (Join-Path $bin 'makepri.exe') @('new', '/pr', $priStage, '/cf', 'priconfig.xml', '/mn', 'AppxManifest.xml', '/of', 'resources.pri', '/o')
    } finally { Pop-Location }

    Copy-Item "$publish\*" $layout -Recurse
    Copy-Item (Join-Path $priStage 'Assets') (Join-Path $layout 'Assets') -Recurse
    Copy-Item (Join-Path $priStage 'resources.pri') $layout
    [IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, $utf8)

    $storeOut = Join-Path $artifacts 'store'
    New-Item -ItemType Directory -Force $storeOut | Out-Null
    $msix = Join-Path $storeOut "CPUx_${msixVersion}_x64.msix"
    RunQuiet (Join-Path $bin 'makeappx.exe') @('pack', '/d', $layout, '/p', $msix, '/o')
    Write-Host ("  {0} ({1:N1} MB)" -f $msix, ((Get-Item $msix).Length / 1MB))
    if ($identity.IdentityName -eq 'CPUx.Placeholder') {
        Write-Warning 'store\identity.json still has placeholder values. Fill them in from Partner Center before uploading.'
    }

    if ($TestSign) {
        # For trying the MSIX locally before uploading. The Store signs uploaded packages itself.
        $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $identity.Publisher -and $_.HasPrivateKey } | Select-Object -First 1
        if (-not $cert) {
            $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $identity.Publisher -CertStoreLocation Cert:\CurrentUser\My `
                -KeyUsage DigitalSignature -FriendlyName 'CPUx MSIX test signing' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        }
        Export-Certificate -Cert $cert -FilePath (Join-Path $storeOut 'CPUx-test-signing.cer') | Out-Null
        RunQuiet (Join-Path $bin 'signtool.exe') @('sign', '/fd', 'SHA256', '/sha1', $cert.Thumbprint, $msix)
        Write-Host "  Test-signed. Trust $storeOut\CPUx-test-signing.cer under Local Machine > Trusted People to install it."
    }
}

if ($Only -in 'All', 'Portable') {
    Step 'Building portable single-file exe (needs .NET 8 Desktop Runtime)'
    $portable = Join-Path $artifacts 'portable'
    Run dotnet @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-nologo', '-v', 'q',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none',
        "-p:OutputPath=$artifacts\build\portable\", '-o', $portable)
    Get-ChildItem $portable -Filter CPUx.exe | ForEach-Object { Write-Host ("  {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
}

Step 'Done'
