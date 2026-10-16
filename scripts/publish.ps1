[CmdletBinding()]
param(
    # Directory that receives <release-id>/ (created atomically from a staging directory on the same volume).
    [string]$Output,
    [ValidateSet('Release')][string]$Configuration = 'Release',
    # Development artifacts only: a dirty tree is recorded as source_dirty=true and is refused by registration.
    [switch]$AllowDirty,
    # Directory for restore/build/test logs and TRX files (outside the payload).
    [string]$LogDirectory
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'artifacts\publish' }
if (-not $LogDirectory) { $LogDirectory = Join-Path $root 'artifacts\publish-logs' }
New-Item -ItemType Directory -Force -Path $Output, $LogDirectory | Out-Null

$gitSha = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $gitSha -notmatch '^[0-9a-f]{40}$') { throw 'git rev-parse failed' }
$sourceDirty = @(& git -C $root status --porcelain).Count -gt 0
if ($sourceDirty -and -not $AllowDirty) { throw 'The source tree is dirty. Commit first, or pass -AllowDirty for a development artifact.' }
$dotnetSdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'dotnet --version failed' }
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) { throw 'Node.js is required: the generated-driver contract tests are part of the release gate.' }
$nodeVersion = (& node --version).Trim()

$releaseId = '{0}-{1}' -f (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss'), $gitSha.Substring(0, 8)
$staging = Join-Path $Output ".staging-$releaseId"
$final = Join-Path $Output $releaseId
if (Test-Path -LiteralPath $final) { throw "Release directory already exists: $final" }

function Invoke-Logged([string]$Name, [scriptblock]$Command) {
    $log = Join-Path $LogDirectory "$releaseId-$Name.log"
    & $Command *>&1 | Tee-Object -FilePath $log | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (see $log)" }
    return $log
}

$solution = Join-Path $root 'CubaseCompleteMcp.sln'
Invoke-Logged 'restore' { dotnet restore $solution --locked-mode } | Out-Null
Invoke-Logged 'build' { dotnet build $solution -c $Configuration --no-restore } | Out-Null
$trxDirectory = Join-Path $LogDirectory 'trx'
Invoke-Logged 'test' { dotnet test $solution -c $Configuration --no-build --logger "trx;LogFilePrefix=$releaseId" --results-directory $trxDirectory } | Out-Null
# A release gate must never pass on zero or skipped tests. The TRX counters are authoritative (console text is
# localized and code-page dependent). Every test project must have produced its own TRX file.
$testProjects = @(Get-ChildItem -Path (Join-Path $root 'tests') -Filter '*.csproj' -Recurse).Count
$trxFiles = @(Get-ChildItem -LiteralPath $trxDirectory -Filter "$releaseId*.trx")
if ($trxFiles.Count -ne $testProjects) { throw "Expected $testProjects TRX files, found $($trxFiles.Count); refusing to publish." }
foreach ($file in $trxFiles) {
    [xml]$trx = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    $counters = $trx.TestRun.ResultSummary.Counters
    if ([int]$counters.total -eq 0 -or [int]$counters.executed -eq 0) { throw "A test assembly ran zero tests: $($file.Name)" }
    if ([int]$counters.notExecuted -gt 0 -or [int]$counters.executed -ne [int]$counters.total) { throw "Tests were skipped: $($file.Name)" }
    if ([int]$counters.failed -gt 0 -or [int]$counters.passed -ne [int]$counters.total) { throw "Tests failed: $($file.Name)" }
}

New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    foreach ($project in @('CubaseCompleteMcp.Broker', 'CubaseCompleteMcp.Server', 'CubaseCompleteMcp.Cli')) {
        $path = Join-Path $root "src\$project\$project.csproj"
        Invoke-Logged "publish-$project" { dotnet publish $path -c $Configuration --no-restore -o $staging } | Out-Null
    }
    # License, notices and the user documentation travel with the binaries (listed in the manifest as payload).
    foreach ($document in @('LICENSE', 'README.md', 'CHANGELOG.md', 'SECURITY.md')) {
        Copy-Item -LiteralPath (Join-Path $root $document) -Destination $staging
    }
    Copy-Item -LiteralPath (Join-Path $root 'docs\USAGE.md') -Destination (Join-Path $staging 'USAGE.md')
    $cli = Join-Path $staging 'CubaseCompleteMcp.Cli.dll'
    $catalog = Join-Path $root 'catalog\reference-key-commands.xml'
    $manifestOutput = & dotnet $cli release-manifest $staging --release-id $releaseId --candidate-sha $gitSha `
        --source-dirty ($sourceDirty.ToString().ToLowerInvariant()) --dotnet-sdk $dotnetSdk --node-version $nodeVersion --catalog $catalog
    if ($LASTEXITCODE -ne 0) { throw 'release-manifest failed' }
    $validateArgs = @('validate-release', $staging)
    if ($sourceDirty) { $validateArgs += '--allow-dirty' }
    & dotnet $cli @validateArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The staged release did not validate.' }
    Move-Item -LiteralPath $staging -Destination $final
}
catch {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    throw
}

$manifestSha = (Get-FileHash -LiteralPath (Join-Path $final 'release-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{ release = $final; release_id = $releaseId; manifest_sha256 = $manifestSha; candidate_git_sha = $gitSha; source_dirty = $sourceDirty } |
    ConvertTo-Json | Write-Output
