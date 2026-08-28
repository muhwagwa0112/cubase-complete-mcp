[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$releaseId = Get-Date -Format 'yyyyMMdd-HHmmss'
$output = Join-Path $root "artifacts\publish\$releaseId"

dotnet restore (Join-Path $root 'CubaseCompleteMcp.sln') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed' }
dotnet build (Join-Path $root 'CubaseCompleteMcp.sln') -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }
dotnet test (Join-Path $root 'CubaseCompleteMcp.sln') -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed' }

foreach ($project in @('CubaseCompleteMcp.Broker','CubaseCompleteMcp.Server','CubaseCompleteMcp.Cli')) {
    $path = Join-Path $root "src\$project\$project.csproj"
    dotnet publish $path -c $Configuration --no-restore -o $output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $project" }
}

$gitSha = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitSha)) { throw 'git rev-parse failed' }
$sourceDirty = @(& git -C $root status --porcelain).Count -gt 0
$dotnetSdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetSdk)) { throw 'dotnet --version failed' }
$componentFiles = [ordered]@{
    server = 'CubaseCompleteMcp.Server.exe'
    broker = 'CubaseCompleteMcp.Broker.exe'
    core = 'CubaseCompleteMcp.Core.dll'
    cli = 'CubaseCompleteMcp.Cli.exe'
}
$components = [ordered]@{}
foreach ($entry in $componentFiles.GetEnumerator()) {
    $componentPath = Join-Path $output $entry.Value
    if (-not (Test-Path -LiteralPath $componentPath -PathType Leaf)) { throw "release component missing: $($entry.Value)" }
    $components[$entry.Key] = [ordered]@{
        path = $entry.Value
        sha256 = (Get-FileHash -LiteralPath $componentPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifest = [ordered]@{
    schema_version = 1
    release_id = $releaseId
    created_at_utc = (Get-Date).ToUniversalTime().ToString('o')
    git_sha = $gitSha
    source_dirty = $sourceDirty
    configuration = $Configuration
    dotnet_sdk = $dotnetSdk
    components = $components
}
$manifestJson = $manifest | ConvertTo-Json -Depth 6
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $output 'release-manifest.json'), $manifestJson, $utf8NoBom)

Write-Output $output
