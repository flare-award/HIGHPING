[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$version = '2.2.2'
$url = 'https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip'
$expectedArchiveSha256 = '63cb41763bb4b20f600b6de04e991a9c2be73279e317d4d82f237b150c5f3f15'
$sourceCommit = '1789526ecfb9ff5397c94f9f54c1a3dc2fb60440'
$sourceUrl = "https://codeload.github.com/basil00/Divert/zip/$sourceCommit"
$expectedSourceArchiveSha256 = '25054821d47ec0ef2227c79f3d6688a3a11d6a128d8f103710d1022e92ea1054'
$expectedFiles = @{
    'WinDivert.dll' = 'c1e060ee19444a259b2162f8af0f3fe8c4428a1c6f694dce20de194ac8d7d9a2'
    'WinDivert64.sys' = '8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2'
    'LICENSE' = '14a0cb5214d536e4fdae6aa3f5696f981eeda106cd026e9794bba489ee79d628'
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("LatencyStudio-WinDivert-" + [Guid]::NewGuid().ToString('N'))
$archive = Join-Path $work 'WinDivert-2.2.2-A.zip'
$sourceArchive = Join-Path $work 'WinDivert-2.2.2-Source.zip'
$extract = Join-Path $work 'extract'
$destination = [IO.Path]::GetFullPath($Destination)
$notices = Join-Path $destination 'ThirdPartyNotices'

function Get-PinnedDownload([string] $Uri, [string] $OutFile, [string] $Description) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
            Invoke-WebRequest -Uri $Uri -OutFile $OutFile -UseBasicParsing -TimeoutSec 180
            return
        }
        catch {
            if ($attempt -eq 3) {
                throw "Could not download $Description after $attempt attempts from ${Uri}: $($_.Exception.Message)"
            }

            Write-Warning "Download attempt $attempt for $Description failed: $($_.Exception.Message)"
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

try {
    New-Item -ItemType Directory -Force -Path $work, $extract, $destination, $notices | Out-Null
    Write-Host "Downloading the pinned official WinDivert $version release..."
    Get-PinnedDownload $url $archive "WinDivert $version binary archive"

    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($archiveHash -ne $expectedArchiveSha256) {
        throw "WinDivert archive SHA-256 mismatch. Expected $expectedArchiveSha256, got $archiveHash. No native files were installed."
    }
    Write-Host "WinDivert binary archive checksum verified."

    Write-Host "Downloading the corresponding upstream source for LGPL compliance..."
    Get-PinnedDownload $sourceUrl $sourceArchive "WinDivert $version source archive"
    $sourceHash = (Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -ne $expectedSourceArchiveSha256) {
        throw "WinDivert source archive SHA-256 mismatch. Expected $expectedSourceArchiveSha256, got $sourceHash. No native files were installed."
    }
    Write-Host "WinDivert source archive checksum verified."

    Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force
    $root = Join-Path $extract 'WinDivert-2.2.2-A'
    $sourceFiles = @{
        'WinDivert.dll' = Join-Path $root 'x64\WinDivert.dll'
        'WinDivert64.sys' = Join-Path $root 'x64\WinDivert64.sys'
        'LICENSE' = Join-Path $root 'LICENSE'
    }

    foreach ($name in $expectedFiles.Keys) {
        $source = $sourceFiles[$name]
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "The pinned WinDivert archive is missing $name."
        }

        $fileHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($fileHash -ne $expectedFiles[$name]) {
            throw "WinDivert $name SHA-256 mismatch. Expected $($expectedFiles[$name]), got $fileHash."
        }
    }

    Copy-Item -LiteralPath $sourceFiles['WinDivert.dll'] -Destination (Join-Path $destination 'WinDivert.dll') -Force
    Copy-Item -LiteralPath $sourceFiles['WinDivert64.sys'] -Destination (Join-Path $destination 'WinDivert64.sys') -Force
    Copy-Item -LiteralPath $sourceFiles['LICENSE'] -Destination (Join-Path $notices 'WinDivert-LICENSE.txt') -Force
    Copy-Item -LiteralPath $sourceArchive -Destination (Join-Path $notices 'WinDivert-2.2.2-Source.zip') -Force
    $sourceNotice = @"
Corresponding WinDivert source archive, pinned to commit $sourceCommit:
$sourceUrl
SHA-256: $expectedSourceArchiveSha256
"@
    Set-Content -LiteralPath (Join-Path $notices 'WinDivert-SOURCE-URL.txt') -Value $sourceNotice -Encoding utf8

    $dependencyNotice = @"
WinDivert $version (official x64 binary release, variant A)

Official binary release: $url
Corresponding upstream source: https://github.com/basil00/Divert/tree/v$version
Pinned source archive: $sourceUrl
License: dual-licensed by upstream under LGPL-3.0-or-later or GPL-2.0; this distribution uses the LGPL option.
The source ZIP and upstream LICENSE are included in ThirdPartyNotices. The library, driver, and license file are redistributed unmodified.

Verified SHA-256:
  WinDivert-2.2.2-A.zip  $expectedArchiveSha256
  WinDivert source ZIP   $expectedSourceArchiveSha256
  WinDivert.dll         $($expectedFiles['WinDivert.dll'])
  WinDivert64.sys       $($expectedFiles['WinDivert64.sys'])
  LICENSE               $($expectedFiles['LICENSE'])
"@
    Set-Content -LiteralPath (Join-Path $notices 'WinDivert-DEPENDENCY.txt') -Value $dependencyNotice -Encoding utf8
    Write-Host "Verified WinDivert $version archive and x64 components; DLL and driver staged in $destination."
}
catch {
    $message = "WinDivert staging failed: $($_.Exception.Message)"
    $annotation = $message.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    Write-Host "::error title=WinDivert staging failed::$annotation"
    Write-Host "WinDivert staging exception: $($_.Exception.ToString())"
    throw
}
finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
