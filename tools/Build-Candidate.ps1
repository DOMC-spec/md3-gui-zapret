# Produces an alpha candidate only after every automated check succeeds.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    $sdk = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
    $actualSdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $actualSdk.Trim() -ne $sdk) { throw "Install .NET SDK $sdk for Windows x64." }
    $commit = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' }
    $sourceStatus = @(& git status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source changes.' }
    if ($sourceStatus.Count -ne 0) { throw 'Commit source changes before building a traceable candidate.' }

    $output = Join-Path $repo ('artifacts/candidate-' + [guid]::NewGuid().ToString('N'))
    $logs = Join-Path $output 'checks'
    $package = Join-Path $output 'package'
    New-Item -ItemType Directory -Path $logs, $package | Out-Null

    function Invoke-Dotnet([string] $name, [string[]] $arguments) {
        Write-Host "Running $name"
        & dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $logs "$name.log") | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE. See $logs" }
    }

    Invoke-Dotnet 'restore' @('restore', 'Zapret.sln', '--locked-mode')
    Invoke-Dotnet 'build' @('build', 'Zapret.sln', '-c', 'Release', '--no-restore', '-m:1', '-p:UseSharedCompilation=false')
    Invoke-Dotnet 'core' @('run', '--project', 'tests/Zapret.Core.Tests', '-c', 'Release', '--no-build')
    # These runners share a global mutex and must remain sequential.
    Invoke-Dotnet 'guard' @('run', '--project', 'tests/Zapret.Guard.Tests', '-c', 'Release', '--no-build')
    Invoke-Dotnet 'wpf-light' @('run', '--project', 'tests/Zapret.Gui.Smoke', '-c', 'Release', '--no-build', '--', (Join-Path $logs 'ui-light.png'))
    Invoke-Dotnet 'wpf-dark' @('run', '--project', 'tests/Zapret.Gui.Smoke', '-c', 'Release', '--no-build', '--', (Join-Path $logs 'ui-dark.png'), '--dark', '--small', '--scale200')
    Invoke-Dotnet 'publish' @('publish', 'src/Zapret.Gui/Zapret.Gui.csproj', '-c', 'Release', '--no-restore', '--no-build', '-o', $package)

    $exe = Join-Path $package 'Zapret.Configurator.exe'
    if (!(Test-Path -LiteralPath $exe)) { throw 'Publish did not produce Zapret.Configurator.exe.' }
    Copy-Item -LiteralPath 'README.md', 'LICENSE', 'docs/release/flowseal-1.10.3-acceptance.md' -Destination $package
    Copy-Item -LiteralPath 'src/Zapret.Core/Compatibility/flowseal-1.10.3.json' -Destination $package
    $reference = Get-Content 'src/Zapret.Core/Compatibility/flowseal-1.10.3.json' -Raw | ConvertFrom-Json
    [ordered]@{
        status = 'alpha-candidate; dedicated Windows acceptance pending'
        sourceCommit = $commit.Trim()
        sdk = $sdk
        runtime = 'win-x64; self-contained; untrimmed'
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
        flowsealCommit = $reference.commit
        flowsealArchiveSha256 = $reference.archiveSha256
        executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        checks = @('core', 'guard (SCM/TCP/journal simulated; inert native fixtures)', 'wpf-light', 'wpf-dark-small-200-percent-layout')
        pending = @('real services/driver/TCP', '22-strategy native parity', 'physical keyboard', 'screen reader', 'per-monitor DPI', 'video/voice availability not certified')
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $package 'build.json') -Encoding utf8
    Get-ChildItem -LiteralPath $package -File | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Destination $package
    $archive = Join-Path $output 'Zapret-Configurator-win-x64-alpha.zip'
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive
    '{0}  {1}' -f (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $archive -Leaf) |
        Set-Content -LiteralPath (Join-Path $output 'archive.sha256') -Encoding utf8
    Write-Host "Candidate and check evidence: $output"
} finally {
    Pop-Location
}
