# Download unmodified desktop fonts and their license directly from Fontshare.
param([string]$Output = (Join-Path $PSScriptRoot '../re/local-fonts'))
$ErrorActionPreference = 'Stop'
$files = @(
    @{ Suffix = '/Fonts/OTF/CabinetGrotesk-Regular.otf'; Name = 'CabinetGrotesk-Regular.otf'; Hash = '893258BC136D7EC7487333FA4511F7C5794F6AD7C77EEE2437881C6DF985C0B1' },
    @{ Suffix = '/Fonts/OTF/CabinetGrotesk-Medium.otf'; Name = 'CabinetGrotesk-Medium.otf'; Hash = '59F01617D86090D924EF1C65D79E3902CAD919AFB3040173AFFB31314118B0F3' },
    @{ Suffix = '/Fonts/OTF/CabinetGrotesk-Bold.otf'; Name = 'CabinetGrotesk-Bold.otf'; Hash = 'F3AA0CE9AF505408D9A0420515C6C26DC215130B56ACC057D24B3BFC2CDA292E' },
    @{ Suffix = '/License/FFL.txt'; Name = 'Cabinet-FFL.txt'; Hash = '145E7FE2429A3336BA215C070EF722000E01348A3E1BAAA127E871BB5012F554' }
)
$client = [Net.Http.HttpClient]::new()
try {
    $bytes = $client.GetByteArrayAsync('https://api.fontshare.com/v2/fonts/download/cabinet-grotesk').GetAwaiter().GetResult()
} finally { $client.Dispose() }
$stream = [IO.MemoryStream]::new($bytes, $false)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
try {
    foreach ($file in $files) {
        $entries = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/').EndsWith($file.Suffix, [StringComparison]::Ordinal) })
        if ($entries.Count -ne 1) { throw "Expected exactly one font archive entry for $($file.Name)." }
        $inputStream = $entries[0].Open()
        $buffer = [IO.MemoryStream]::new()
        try { $inputStream.CopyTo($buffer); $file.Bytes = $buffer.ToArray() }
        finally { $inputStream.Dispose(); $buffer.Dispose() }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($file.Bytes))
        if ($hash -ne $file.Hash) { throw "Fontshare changed $($file.Name); review the new font/license before updating the pinned hash." }
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Output)) | Out-Null
    foreach ($file in $files) { [IO.File]::WriteAllBytes((Join-Path $Output $file.Name), $file.Bytes) }
} finally { $archive.Dispose(); $stream.Dispose() }
Write-Output 'Cabinet Grotesk Regular, Medium, Bold and FFL license verified.'
