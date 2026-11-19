param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$PrivateKey,
    [string]$Output = 'release-live',
    [string]$Notes = 'FM27 Live güncellemesi',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot
$keyPath = (Resolve-Path -LiteralPath $PrivateKey).Path
$releaseDir = [IO.Path]::GetFullPath((Join-Path $repoRoot $Output))
if (-not $SkipTests) {
    dotnet test Fm27Editor.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; release stopped.' }
}
$appDir = Join-Path $releaseDir 'publish'
dotnet publish src/Fm27Editor.App/Fm27Editor.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none "-p:Version=$Version" -o $appDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$exe = Join-Path $releaseDir 'FM27Live.exe'
Copy-Item -LiteralPath (Join-Path $appDir 'Fm27Editor.exe') -Destination $exe -Force
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    Schema = 1; Product = 'FM27Live'; Version = $Version
    Url = "https://github.com/2dfindomjinx-lang/FM27Live/releases/download/v$Version/FM27Live.exe"
    Size = (Get-Item -LiteralPath $exe).Length; Sha256 = $hash; Notes = $Notes
}
$payload = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
$signingKey = [Security.Cryptography.ECDsa]::Create()
try {
    $signingKey.ImportFromPem([IO.File]::ReadAllText($keyPath))
    $publicKey = [IO.File]::ReadAllText((Join-Path $repoRoot 'src/Fm27Editor.App/Live/update-public.pem'))
    $verifier = [Security.Cryptography.ECDsa]::Create()
    try {
        $verifier.ImportFromPem($publicKey)
        $signature = $signingKey.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
        if (-not $verifier.VerifyData($payload, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'The private key does not match the app public key.' }
    } finally { $verifier.Dispose() }
} finally { $signingKey.Dispose() }
$envelope = @{ Payload = [Convert]::ToBase64String($payload); Signature = [Convert]::ToBase64String($signature) } | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $releaseDir 'update.json'), $envelope)
[IO.File]::WriteAllText((Join-Path $releaseDir 'SHA256SUMS.txt'), "$hash  FM27Live.exe`n")
Copy-Item LICENSE, NOTICE-LIVE.txt, README-LIVE.md -Destination $releaseDir -Force
Write-Output "Release prepared: $releaseDir"
Write-Output "Publish FM27Live.exe and update.json together. Never publish the private key."
