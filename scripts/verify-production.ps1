param(
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-ProductionNativeRoot([string]$NativePath) {
    $resolvedNative = [IO.Path]::GetFullPath($NativePath)
    $nativeDriveRoot = [IO.Path]::GetPathRoot($resolvedNative)
    if ($resolvedNative.Length -gt $nativeDriveRoot.Length) {
        $resolvedNative = $resolvedNative.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    }
    return $resolvedNative
}

$bridgeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$producer = Join-Path $PSScriptRoot 'production-evidence.mjs'
if (!$IsWindows) { throw 'The production producer requires its dedicated Windows runner.' }
if ($env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $env:GITHUB_REPOSITORY -cne 'Illyavrs/Transfiguration') {
    throw 'Only a manual dispatch in the component repository may produce release input.'
}
if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $SourceCommit -cne $env:GITHUB_SHA) {
    throw 'Source commit differs from the exact dispatched run head.'
}
if (!$env:ILLYAVERSE_NATIVE_ROOT -or ![IO.Path]::IsPathFullyQualified($env:ILLYAVERSE_NATIVE_ROOT) -or $env:ILLYAVERSE_NATIVE_ROOT.StartsWith('\\')) {
    throw 'The operator must preinstall and pin an absolute local ILLYAVERSE_NATIVE_ROOT.'
}
if (![IO.Path]::IsPathFullyQualified($OutputRoot) -or !$env:RUNNER_TEMP) { throw 'An absolute runner temporary output root is required.' }
$productionRoot = [IO.Path]::GetFullPath($OutputRoot)
$runnerTemporary = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (!$productionRoot.StartsWith($runnerTemporary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must stay inside RUNNER_TEMP.' }
if (Test-Path -LiteralPath $productionRoot) { throw 'Production output exists; use a fresh run directory.' }
$bridgeNative = Resolve-ProductionNativeRoot -NativePath $env:ILLYAVERSE_NATIVE_ROOT
foreach ($overrideName in 'DOTNET_STARTUP_HOOKS','DOTNET_ADDITIONAL_DEPS','DOTNET_SHARED_STORE') {
    if ([Environment]::GetEnvironmentVariable($overrideName)) { throw 'Inherited .NET hooks or dependency overrides are not allowed in detached proofs.' }
}
foreach ($profileName in 'CORECLR_ENABLE_PROFILING','COR_ENABLE_PROFILING') {
    $profileValue = [Environment]::GetEnvironmentVariable($profileName)
    if ($profileValue -and $profileValue -ne '0') { throw 'Inherited native profiling is not allowed in detached proofs.' }
}
$nodeVersion = & node --version
if ($LASTEXITCODE -ne 0 -or $nodeVersion -cnotmatch '^v24\.') { throw 'Preinstalled Node 24 is required.' }
$installedSdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or !($installedSdks | Where-Object { $_ -match '^10\.' })) { throw 'Preinstalled .NET 10 SDK is required.' }
foreach ($stalePath in 'artifacts','bin/Release') {
    if (Test-Path -LiteralPath (Join-Path $bridgeRoot $stalePath)) { throw 'Build outputs already exist; a clean dedicated runner checkout is required.' }
}

function Invoke-Producer([string]$Mode) {
    & node $producer $Mode '--source-root' $bridgeRoot '--source-commit' $SourceCommit '--repository' $env:GITHUB_REPOSITORY '--native-root' $bridgeNative '--output-root' $productionRoot
    if ($LASTEXITCODE -ne 0) { throw "Production evidence $Mode failed." }
}

# This first operation validates the clean pinned checkout and freezes only
# top-level native DLL bytes. It does not open game.ini, auth, Songs or Realm DBs.
Invoke-Producer 'freeze'
$provenance = Join-Path $productionRoot 'provenance'
$proofRuntime = Join-Path $productionRoot 'proof-runtime'
$proofExe = Join-Path $proofRuntime 'Transfiguration.Proof.exe'
$bridgeDll = Join-Path $bridgeRoot 'artifacts/osu.Game.Rulesets.Transfiguration.dll'
$frozenNative = Join-Path $provenance 'frozen-native.json'
$commands = [Collections.Generic.List[object]]::new()

function Invoke-RecordedCommand([string]$Kind, [string]$Command, [string[]]$CommandArgs) {
    $commandLog = Join-Path $provenance "$Kind.log"
    $started = [DateTime]::UtcNow.ToString('o')
    $exitCode = 1
    try {
        # Only build.ps1 and the exact four detached proof entry points are called.
        # Native stdout/stderr stays in a separate provenance artifact.
        if ($Kind -ceq 'build') {
            # String-array splatting into a .ps1 binds strings positionally.
            # Use the actual named parameter while retaining canonical argv in
            # the receipt; native dotnet/proof executables use array arguments.
            if ($CommandArgs.Count -ne 2 -or $CommandArgs[0] -cne '-NativeRoot' -or $CommandArgs[1] -cne $bridgeNative) { throw 'Unexpected production build arguments.' }
            & $Command -NativeRoot $bridgeNative 2>&1 | Tee-Object -FilePath $commandLog
        }
        else { & $Command @CommandArgs 2>&1 | Tee-Object -FilePath $commandLog }
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) { throw "$Kind failed with exit code $exitCode." }
    }
    finally {
        $commands.Add([ordered]@{ kind = $Kind; command = $Command; args = @($CommandArgs); exit_code = $exitCode; started_at = $started; finished_at = [DateTime]::UtcNow.ToString('o') })
        ConvertTo-Json -InputObject ([ordered]@{ protocol = 1; commands = @($commands.ToArray()) }) -Depth 8 |
            Set-Content -LiteralPath (Join-Path $provenance 'commands.json') -Encoding utf8NoBOM
    }
}

Push-Location $bridgeRoot
try {
    Invoke-RecordedCommand 'build' (Join-Path $bridgeRoot 'build.ps1') @('-NativeRoot', $bridgeNative)
    Invoke-RecordedCommand 'publish-proof' 'dotnet' @('publish', (Join-Path $bridgeRoot 'tests/Proof.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:UseAppHost=true', '-p:RuntimeFrameworkVersion=10.0.12', "-p:NativeRoot=$bridgeNative", '-o', $proofRuntime, '--nologo')
    # Bind the fresh DLL and proof runtime before any proof executes.
    Invoke-Producer 'bind'
    Invoke-RecordedCommand 'production-browser-only' $proofExe @('--production-browser-only', $bridgeDll)
    Invoke-RecordedCommand 'production-logout-contracts' $proofExe @('--production-logout-contracts', $bridgeDll, $bridgeNative, $frozenNative)
    Invoke-RecordedCommand 'production-welcome-contracts' $proofExe @('--production-welcome-contracts', $bridgeDll, $bridgeNative, $frozenNative)
    Invoke-RecordedCommand 'production-relax-contracts' $proofExe @('--production-relax-contracts', $bridgeDll, $bridgeNative, $frozenNative)
    # Produces verification only after all four exit 0 and frozen bytes match.
    Invoke-Producer 'finalize'
}
finally { Pop-Location }
Write-Output "Verified production input: $(Join-Path $productionRoot 'verified')"
Write-Output 'Native GUI, real login and actual sign-out remain pending human testing.'
