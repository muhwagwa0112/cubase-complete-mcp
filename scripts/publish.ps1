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

Write-Output $output
