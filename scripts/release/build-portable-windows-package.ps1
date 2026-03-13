param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("cpmigrate-portable-" + [System.Guid]::NewGuid().ToString("N"))
$stagingRoot = Join-Path $tempRoot "portable"

try {
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

    Copy-Item -Path (Join-Path $PublishDirectory "*") -Destination $stagingRoot -Recurse -Force

    if (Test-Path $OutputPath) {
        Remove-Item $OutputPath -Force
    }

    Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $OutputPath -Force
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item $tempRoot -Recurse -Force
    }
}
