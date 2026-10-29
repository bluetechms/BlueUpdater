<#
 Builds, packages, signs and (optionally) uploads a BluePOSPrinting release for BlueUpdater.

   .\publish-printing-release.ps1 -Version 1.1.1.0 -KeyPath D:\keys\primary.private.pk8.b64
   .\publish-printing-release.ps1 -Version 1.1.1.0 -KeyPath D:\keys\primary.private.pk8.b64 -Upload -Canary "12,34" -Notes "fix X"

 The release is uploaded INACTIVE. Activate it (canary first, then everyone) with BlueRelease `state`.
 Secrets stay out of the command line: the key passphrase and the super-admin token are read from the
 BLUERELEASE_PASSPHRASE / BLUERELEASE_TOKEN environment variables, or prompted for.
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^\d{1,5}(\.\d{1,5}){3}$')][string]$Version,
    [Parameter(Mandatory)][string]$KeyPath,
    [string]$Api = 'https://api.bluehms.com/api',
    [switch]$Upload,
    [string]$Canary,
    [string]$Notes,
    [int]$ExpiresDays = 400
)
$ErrorActionPreference = 'Stop'

$repo    = Split-Path -Parent $PSScriptRoot
$app     = Join-Path $repo '..\BluePOSPrinting\BluePOSPrinting\BluePOSPrinting.csproj'
$work    = Join-Path $repo "publish\printing-outlet\$Version"
$publish = Join-Path $work 'files'
$out     = Join-Path $work 'release'
$tool    = Join-Path $repo 'tools\BlueRelease\BlueRelease.csproj'

function Read-Secret([string]$name, [string]$prompt) {
    if (-not [Environment]::GetEnvironmentVariable($name)) {
        $s = Read-Host -AsSecureString $prompt
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s))
        [Environment]::SetEnvironmentVariable($name, $plain, 'Process')
    }
}

if (-not (Test-Path $app)) { throw "BluePOSPrinting project not found at $app" }
if (-not (Test-Path $KeyPath)) { throw "Signing key not found: $KeyPath" }
Read-Secret 'BLUERELEASE_PASSPHRASE' 'Signing key passphrase'

if (Test-Path $work) { Remove-Item $work -Recurse -Force }   # a clean publish: stale files must not ship
New-Item -ItemType Directory -Path $publish, $out | Out-Null

Write-Host "Publishing BluePOSPrinting $Version ..."
dotnet publish $app -c Release -p:PublishProfile=SelfContained "-p:Version=$Version" "-p:FileVersion=$Version" "-p:AssemblyVersion=$Version" -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host 'Packaging and signing ...'
# appsettings*.json, logo and the NSSM binary are site-owned: BlueRelease leaves them out on its own.
dotnet run --project $tool -c Release -- pack --product printing-outlet --version $Version --src $publish `
    --exe BluePOSPrinting.exe --key $KeyPath --out $out --expires-days $ExpiresDays
if ($LASTEXITCODE -ne 0) { throw 'pack failed' }

$pub = Join-Path (Split-Path $KeyPath) ((Split-Path $KeyPath -Leaf) -replace '\.private\.pk8\.b64$', '.public.b64')
if (Test-Path $pub) {
    dotnet run --project $tool -c Release -- verify --dir $out --pubkey $pub --product printing-outlet
    if ($LASTEXITCODE -ne 0) { throw 'verification failed' }
} else {
    Write-Warning "Public key $pub not found next to the private key; skipped the verify step."
}

if ($Upload) {
    Read-Secret 'BLUERELEASE_TOKEN' 'Super-admin session token (the `auth` header value)'
    $extra = @()
    if ($Canary) { $extra += @('--canary', $Canary) }
    if ($Notes)  { $extra += @('--notes', $Notes) }
    dotnet run --project $tool -c Release -- upload --api $Api --dir $out --product printing-outlet @extra
    if ($LASTEXITCODE -ne 0) { throw 'upload failed' }
} else {
    Write-Host "Not uploaded. Output: $out   (re-run with -Upload, or use BlueRelease upload)"
}
