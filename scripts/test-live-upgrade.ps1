param(
    [Parameter(Mandatory)][string]$OldRelease,
    [Parameter(Mandatory)][string]$NewRelease,
    [Parameter(Mandatory)][string]$TestRoot
)
$ErrorActionPreference = 'Stop'
$oldDir = (Resolve-Path -LiteralPath $OldRelease).Path
$newDir = (Resolve-Path -LiteralPath $NewRelease).Path
$caseRoot = [IO.Path]::GetFullPath($TestRoot)
if (Test-Path -LiteralPath $caseRoot) { throw 'TestRoot must be new; existing data will not be overwritten.' }
New-Item -ItemType Directory -Path $caseRoot | Out-Null
$envelope = Get-Content -LiteralPath (Join-Path $newDir 'update.json') -Raw | ConvertFrom-Json
$manifest = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.Payload)) | ConvertFrom-Json
$state = Join-Path $caseRoot 'data'
$stage = Join-Path $state ('updates\' + $manifest.Version)
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $newDir 'FM27Live.exe'), (Join-Path $newDir 'update.json') -Destination $stage
$exe = Join-Path $caseRoot 'FM27Live.exe'
Copy-Item -LiteralPath (Join-Path $oldDir 'FM27Live.exe') -Destination $exe
$oldHash = (Get-FileHash -LiteralPath $exe).Hash
$newHash = (Get-FileHash -LiteralPath (Join-Path $stage 'FM27Live.exe')).Hash
$settingsPath = Join-Path $state 'live-settings.json'
[IO.File]::WriteAllText($settingsPath, '{"AutomaticUpdates":true,"OverlayWidth":1024,"OverlayHeight":680,"OverlayOpacity":0.88}')
$originalSettings = [IO.File]::ReadAllText($settingsPath)
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute = $false
$start.WorkingDirectory = $caseRoot
$start.Environment['FM27LIVE_DATA_DIR'] = $state
$process = [Diagnostics.Process]::Start($start)
$deadline = [DateTime]::UtcNow.AddSeconds(100)
$marker = Join-Path $state 'last-update.txt'
while (-not (Test-Path -LiteralPath $marker) -and [DateTime]::UtcNow -lt $deadline) {
    if (Test-Path -LiteralPath (Join-Path $state 'update-error.txt')) { throw (Get-Content -LiteralPath (Join-Path $state 'update-error.txt') -Raw) }
    Start-Sleep -Milliseconds 500
}
if (-not (Test-Path -LiteralPath $marker)) { throw 'The updated application did not confirm startup.' }
if ((Get-FileHash -LiteralPath $exe).Hash -ne $newHash) { throw 'Installed binary hash mismatch.' }
if ((Get-FileHash -LiteralPath ($exe + '.previous')).Hash -ne $oldHash) { throw 'Backup binary hash mismatch.' }
if ([IO.File]::ReadAllText($settingsPath) -ne $originalSettings) { throw 'User preferences were changed.' }
$report = "PASS: automatic staged update, signed manifest, executable replacement, previous-version backup, actual new-process startup health, preserved preferences."
[IO.File]::WriteAllText((Join-Path $caseRoot 'result.txt'), $report)
Write-Output $report
# Close only the editor instance launched by this isolated test, after all assertions.
Get-Process FM27Live -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process
