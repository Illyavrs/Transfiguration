import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { resolve, win32 } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { COMPONENT_FILES, NATIVE_ASSEMBLIES, REQUIRED_CONTRACTS, digest, validateVerificationEvidence } from './component-release.mjs';
import { createProductionEvidence, detachedProofChecks, validateCommands, validateDispatch, validateFrozenNative } from './production-evidence.mjs';

// These are synthetic in-memory observations. No installed assembly is read,
// process is built/launched, network is used, or production receipt is written.
const BRIDGE = COMPONENT_FILES[0];
const sourceRoot = resolve('synthetic-transfiguration-source');
const nativeRoot = resolve('synthetic-native-dll-references');
const outputRoot = resolve('synthetic-owned-ci-output');
const sourceCommit = 'a'.repeat(40);
const bridgeHash = digest('synthetic production DLL, never a compiled artifact');
const paths = {
  sourceRoot, nativeRoot, proofRuntime: resolve(outputRoot, 'proof-runtime'),
  proofExe: resolve(outputRoot, 'proof-runtime/Transfiguration.Proof.exe'),
  bridgeDll: resolve(sourceRoot, 'artifacts', BRIDGE), frozenNative: resolve(outputRoot, 'provenance/frozen-native.json'),
};
const environment = () => ({ GITHUB_EVENT_NAME: 'workflow_dispatch', GITHUB_REPOSITORY: 'Illyavrs/Transfiguration', GITHUB_SHA: sourceCommit, ILLYAVERSE_LAZER_SERVER_ENDPOINT: 'https://illya.test' });
const checkout = () => ({ head: sourceCommit, root: sourceRoot, clean: true });
const expected = () => ({ sourceRoot, nativeRoot, outputRoot, sourceCommit, repository: 'Illyavrs/Transfiguration', bridgeSha256: bridgeHash, sourceCompatibilitySha256: digest('synthetic exact source compatibility bytes') });
const security = () => ({ protocol: 1, artifact: BRIDGE, default_endpoint: 'https://illya.test', endpoint_mode: 'https_origin_per_session', configurable_endpoint: true, private_auth_filename: 'auth.illyaverse.json', shared_game_preferences: true, fixture: false });
const compatibility = () => ({
  ...security(), platform: 'windows-x64', runtime: '10.0.12', ruleset_api: '2026.818.0', harmony: '2.4.2',
  assemblies: Object.fromEntries(NATIVE_ASSEMBLIES.map((name) => [name, { version: ['osu.Game.dll', 'osu!.dll'].includes(name) ? '2026.1005.1.0' : '10.0.0.0', sha256: digest(`synthetic pinned ${name}`).toUpperCase() }])),
  contracts: [...REQUIRED_CONTRACTS], verification: { native_metadata_and_endpoints: 'passed', http_websocket_and_redirects: 'passed', mandatory_bootstrap_before_headless_main: 'passed', production_destination_policy: 'passed', real_api_worker_refresh_and_login: 'pending_human_test', native_gui_and_login: 'pending_human_test' },
});
function nativeReceipt() {
  const names = [...NATIVE_ASSEMBLIES, 'Realm.dll', 'Newtonsoft.Json.dll', 'osu.Game.Rulesets.Osu.dll'].sort((a, b) => a.toLowerCase() < b.toLowerCase() ? -1 : 1);
  return { protocol: 1, native_root: nativeRoot, frozen_inputs: names.map((name) => ({ path: resolve(nativeRoot, name), sha256: digest(`synthetic pinned ${name}`) })) };
}
function commands() {
  const values = [
    { kind: 'build', command: resolve(sourceRoot, 'build.ps1'), args: ['-NativeRoot', nativeRoot] },
    { kind: 'publish-proof', command: 'dotnet', args: ['publish', resolve(sourceRoot, 'tests/Proof.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:UseAppHost=true', '-p:RuntimeFrameworkVersion=10.0.12', `-p:NativeRoot=${nativeRoot}`, '-o', paths.proofRuntime, '--nologo'] },
    { kind: 'production-browser-only', command: paths.proofExe, args: ['--production-browser-only', paths.bridgeDll] },
    { kind: 'production-logout-contracts', command: paths.proofExe, args: ['--production-logout-contracts', paths.bridgeDll, nativeRoot, paths.frozenNative] },
    { kind: 'production-welcome-contracts', command: paths.proofExe, args: ['--production-welcome-contracts', paths.bridgeDll, nativeRoot, paths.frozenNative] },
    { kind: 'production-relax-contracts', command: paths.proofExe, args: ['--production-relax-contracts', paths.bridgeDll, nativeRoot, paths.frozenNative] },
  ];
  return { protocol: 1, commands: values.map((value, index) => ({ ...value, exit_code: 0, started_at: `2026-10-10T07:0${index}:00.000Z`, finished_at: `2026-10-10T07:0${index}:01.000Z` })) };
}
function snapshot() {
  return { source: { checkout: checkout(), files: { 'src/Guard.cs': { sha256: digest('synthetic source') } } }, native: nativeReceipt(), component: Object.fromEntries(COMPONENT_FILES.map((name) => [name, name === BRIDGE ? bridgeHash : name === 'compatibility.json' ? expected().sourceCompatibilitySha256 : digest(name)])), proof_runtime: { 'Transfiguration.Proof.exe': digest('synthetic proof exe'), 'Transfiguration.Proof.dll': digest('synthetic proof dll') } };
}
function input() {
  return { source: { environment: environment(), checkout: checkout() }, expected: expected(), compatibility: compatibility(), artifact: { ...security(), sha256: bridgeHash }, commands: commands(), paths, before: snapshot(), after: snapshot() };
}

test('actual PowerShell native-root normalizer matches Node Windows resolve and preserves drive roots', { skip: process.platform !== 'win32' }, () => {
  // Load only the production function AST. The rest of verify-production.ps1,
  // installed native files, SDK commands, build and proof entry points never run.
  const script = `
$ErrorActionPreference = 'Stop'
$taskTokens = $null
$taskErrors = $null
$taskAst = [System.Management.Automation.Language.Parser]::ParseFile($env:TRANSFIGURATION_NORMALIZER_SOURCE, [ref]$taskTokens, [ref]$taskErrors)
if ($taskErrors.Count -ne 0) { throw 'Production script syntax failed.' }
$normalizer = $taskAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Resolve-ProductionNativeRoot' }, $true)
if (!$normalizer) { throw 'Production native-root normalizer missing.' }
Invoke-Expression -Command $normalizer.Extent.Text
$nativeExamples = ConvertFrom-Json -InputObject ([Console]::In.ReadToEnd())
$normalizedExamples = @($nativeExamples | ForEach-Object { Resolve-ProductionNativeRoot -NativePath $_ })
ConvertTo-Json -InputObject $normalizedExamples -Compress
`;
  const cases = ['C:\\PinnedClient\\', 'C:\\PinnedClient', 'C:/PinnedClient/', 'C:\\PinnedClient\\..\\NativeReferences\\', 'C:\\', 'D:\\'];
  const output = execFileSync('pwsh', ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', script], {
    encoding: 'utf8', input: JSON.stringify(cases), timeout: 15_000, maxBuffer: 1_048_576,
    windowsHide: true, env: { ...process.env, TRANSFIGURATION_NORMALIZER_SOURCE: fileURLToPath(new URL('./verify-production.ps1', import.meta.url)) },
  });
  assert.deepEqual(JSON.parse(output.trim()), cases.map((value) => win32.resolve(value)));
  assert.equal(JSON.parse(output.trim())[4], 'C:\\');
});

test('manual dispatch binds repository, event, reviewed source and clean component checkout', () => {
  validateDispatch(expected(), environment(), checkout());
  for (const change of [ { GITHUB_EVENT_NAME: 'pull_request' }, { GITHUB_EVENT_NAME: 'pull_request_target' }, { GITHUB_EVENT_NAME: 'push' }, { GITHUB_REPOSITORY: 'other/Transfiguration' }, { GITHUB_SHA: 'b'.repeat(40) } ]) assert.throws(() => validateDispatch(expected(), { ...environment(), ...change }, checkout()));
  for (const change of [{ sourceCommit: 'A'.repeat(40) }, { sourceCommit: 'a'.repeat(39) }, { sourceCommit: sourceCommit + '\n' }, { repository: 'other/Transfiguration' }, { sourceRoot: outputRoot }]) assert.throws(() => validateDispatch({ ...expected(), ...change }, environment(), checkout()));
  for (const change of [{ head: 'b'.repeat(40) }, { clean: false }, { clean: 'true' }, { root: outputRoot }]) assert.throws(() => validateDispatch(expected(), environment(), { ...checkout(), ...change }));
  for (const endpoint of [undefined, '', 'http://illya.test', 'https://other.example.test']) assert.throws(() => validateDispatch(expected(), { ...environment(), ILLYAVERSE_LAZER_SERVER_ENDPOINT: endpoint }, checkout()));
});

test('detached proof dispatch rejects inherited startup hooks and native profilers', () => {
  for (const name of ['DOTNET_STARTUP_HOOKS', 'DOTNET_ADDITIONAL_DEPS', 'DOTNET_SHARED_STORE', 'CORECLR_ENABLE_PROFILING', 'COR_ENABLE_PROFILING']) assert.throws(() => validateDispatch(expected(), { ...environment(), [name]: 'synthetic-unwanted-bootstrap' }, checkout()));
  validateDispatch(expected(), { ...environment(), DOTNET_STARTUP_HOOKS: '', CORECLR_ENABLE_PROFILING: '0', COR_ENABLE_PROFILING: '0' }, checkout());
});

test('frozen native inventory includes all loader dependencies and source compatibility pins', () => {
  const receipt = nativeReceipt(); validateFrozenNative(receipt, compatibility());
  assert(receipt.frozen_inputs.some((entry) => entry.path.endsWith('Realm.dll')));
  assert(receipt.frozen_inputs.some((entry) => entry.path.endsWith('Newtonsoft.Json.dll')));
  for (const name of ['Realm.dll', ...NATIVE_ASSEMBLIES]) {
    const changed = structuredClone(receipt); changed.frozen_inputs = changed.frozen_inputs.filter((entry) => !entry.path.endsWith(name));
    assert.throws(() => validateFrozenNative(changed, compatibility()));
  }
  const mismatched = structuredClone(receipt); mismatched.frozen_inputs.find((entry) => entry.path.endsWith('osu.Game.dll')).sha256 = digest('another client');
  assert.throws(() => validateFrozenNative(mismatched, compatibility()), /compatibility/);
});

test('native receipts reject case aliases, links outside root, relative paths, databases and noncanonical hashes', () => {
  const cases = [
    (r) => { r.frozen_inputs.push(r.frozen_inputs[0]); },
    (r) => { r.frozen_inputs[0].path = r.frozen_inputs[0].path.toUpperCase(); r.frozen_inputs.push({ ...r.frozen_inputs[0], path: r.frozen_inputs[0].path.toLowerCase() }); },
    (r) => { r.frozen_inputs[0].path = resolve(outputRoot, 'outside.dll'); },
    (r) => { r.frozen_inputs[0].path = resolve(nativeRoot, 'nested/extra.dll'); },
    (r) => { r.frozen_inputs[0].path = 'relative.dll'; },
    (r) => { r.frozen_inputs[0].path = resolve(nativeRoot, 'client.realm'); },
    (r) => { r.frozen_inputs[0].sha256 = r.frozen_inputs[0].sha256.toUpperCase(); },
    (r) => { r.frozen_inputs[0].extra = true; },
    (r) => { r.frozen_inputs.reverse(); },
    (r) => { r.protocol = 2; },
  ];
  for (const mutate of cases) { const receipt = nativeReceipt(); mutate(receipt); assert.throws(() => validateFrozenNative(receipt, compatibility())); }
});

test('command receipt admits only fresh production build and exactly the four detached proof modes', () => {
  validateCommands(commands(), paths);
  const cases = [
    (r) => { r.commands.pop(); },
    (r) => { r.commands.push(r.commands[3]); },
    (r) => { r.commands.reverse(); },
    (r) => { r.commands[0].command = resolve(sourceRoot, 'test.ps1'); },
    (r) => { r.commands[1].args[9] = '-p:RuntimeFrameworkVersion=10.0.13'; },
    (r) => { r.commands[2].args[0] = '--browser-only'; },
    (r) => { r.commands[2].args[1] = resolve(sourceRoot, 'bin/Fixture/net10.0', BRIDGE); },
    (r) => { r.commands[3].args[0] = '--account-only'; },
    (r) => { r.commands[3].args[2] = outputRoot; },
    (r) => { r.commands[3].args[3] = resolve(outputRoot, 'different-receipt.json'); },
    (r) => { r.commands[3].command = resolve(nativeRoot, 'osu!.exe'); },
    (r) => { r.commands[3].started_at = '2026-10-10T07:00:00Z'; },
    (r) => { r.commands[3].finished_at = '2026-10-10T07:01:00Z'; },
    (r) => { r.commands[3].started_at = 'not-a-date'; },
    (r) => { r.commands[3].extra = true; },
  ];
  for (const mutate of cases) { const receipt = commands(); mutate(receipt); assert.throws(() => validateCommands(receipt, paths)); }
});

test('every nonzero, absent or nonnumeric command exit rejects evidence', () => {
  for (let index = 0; index < 6; index++) for (const exitCode of [1, 5, 32, -1, null, '0', undefined]) {
    const value = input(); value.commands.commands[index].exit_code = exitCode;
    assert.throws(() => createProductionEvidence(value), /successfully/);
  }
});

test('welcome metadata proof is mandatory and binds the same fresh DLL, native root and frozen receipt', () => {
  const cases = [
    (v) => { v.commands.commands.splice(4, 1); },
    (v) => { v.commands.commands[4].exit_code = 1; },
    (v) => { v.commands.commands[4].kind = 'production-logout-contracts'; },
    (v) => { v.commands.commands[4].args[0] = '--account-only'; },
    (v) => { v.commands.commands[4].args[1] = resolve(sourceRoot, 'bin/Fixture/net10.0', BRIDGE); },
    (v) => { v.commands.commands[4].args[2] = outputRoot; },
    (v) => { v.commands.commands[4].args[3] = resolve(outputRoot, 'different-receipt.json'); },
    (v) => { v.commands.commands[4].command = resolve(nativeRoot, 'osu!.exe'); },
  ];
  for (const mutate of cases) {
    const value = input(); mutate(value);
    assert.throws(() => createProductionEvidence(value));
    assert.throws(() => detachedProofChecks(value.commands, paths));
  }
  const evidence = createProductionEvidence(input());
  // The publisher's exact twelve-field evidence keeps its two existing checks.
  // The mandatory welcome result belongs to separate producer provenance.
  assert.equal(Object.keys(evidence).length, 12);
  assert.deepEqual(Object.keys(evidence.checks), ['production_browser_only', 'explicit_logout']);
  assert.deepEqual(detachedProofChecks(commands(), paths), {
    production_browser_only: 'passed_compiled_policy', explicit_logout: 'passed_pinned_ui_metadata',
    production_welcome_contracts: 'passed_pinned_metadata_and_synthetic_callbacks',
    production_relax_contracts: 'passed_pinned_metadata_and_synthetic_queries',
  });
});

test('RX request metadata proof is mandatory and cannot use fixtures or different frozen inputs', () => {
  const cases = [
    (v) => { v.commands.commands.pop(); },
    (v) => { v.commands.commands[5].exit_code = 1; },
    (v) => { v.commands.commands[5].kind = 'production-welcome-contracts'; },
    (v) => { v.commands.commands[5].args[0] = '--policy'; },
    (v) => { v.commands.commands[5].args[1] = resolve(sourceRoot, 'bin/Fixture/net10.0', BRIDGE); },
    (v) => { v.commands.commands[5].args[2] = outputRoot; },
    (v) => { v.commands.commands[5].args[3] = resolve(outputRoot, 'different-receipt.json'); },
    (v) => { v.commands.commands[5].command = resolve(nativeRoot, 'osu!.exe'); },
  ];
  for (const mutate of cases) {
    const value = input(); mutate(value);
    assert.throws(() => createProductionEvidence(value));
    assert.throws(() => detachedProofChecks(value.commands, paths));
  }
  assert.equal(detachedProofChecks(commands(), paths).production_relax_contracts, 'passed_pinned_metadata_and_synthetic_queries');
  assert.deepEqual(Object.keys(createProductionEvidence(input()).checks), ['production_browser_only', 'explicit_logout']);
});

test('evidence is exactly compatible with the independent component publisher and remains pending native tests', () => {
  const value = input(), evidence = createProductionEvidence(value);
  validateVerificationEvidence(evidence, { transfigurationCommit: sourceCommit, bridgeSha256: bridgeHash, sourceCompatibilitySha256: expected().sourceCompatibilitySha256 }, compatibility());
  assert.deepEqual(evidence.checks, { production_browser_only: 'passed_compiled_policy', explicit_logout: 'passed_pinned_ui_metadata' });
  assert.equal(evidence.native_gui_and_login, 'pending_human_test');
  assert.equal(evidence.source_compatibility_sha256, expected().sourceCompatibilitySha256);
  assert.equal(evidence.bridge_sha256, bridgeHash);
  assert.equal(evidence.native_client_version, '2026.1005.1');
  assert(!Object.hasOwn(evidence, 'native_data_preservation'));
});

test('source, native dependencies, bridge and proof runtime must retain exact frozen bytes and inventories', () => {
  const cases = [
    (v) => { v.after.source.files['src/Guard.cs'].sha256 = digest('changed source'); },
    (v) => { v.after.source.files['new.cs'] = { sha256: digest('new source') }; },
    (v) => { v.after.native.frozen_inputs.find((entry) => entry.path.endsWith('Realm.dll')).sha256 = digest('changed dependency'); },
    (v) => { v.after.native.frozen_inputs = v.after.native.frozen_inputs.filter((entry) => !entry.path.endsWith('Newtonsoft.Json.dll')); },
    (v) => { v.after.component[BRIDGE] = digest('changed DLL after first proof'); },
    (v) => { v.after.component['compatibility.json'] = digest('changed compatibility'); },
    (v) => { v.after.proof_runtime['Transfiguration.Proof.exe'] = digest('changed proof'); },
    (v) => { v.after.proof_runtime['extra.dll'] = digest('added runtime'); },
  ];
  for (const mutate of cases) { const value = input(); mutate(value); assert.throws(() => createProductionEvidence(value), /changed/); }
});

test('production evidence cannot relabel fixture output, different reviewed hashes or real human verification', () => {
  const cases = [
    (v) => { v.artifact.fixture = true; },
    (v) => { v.expected.bridgeSha256 = digest('different reviewed bridge'); },
    (v) => { v.expected.sourceCompatibilitySha256 = digest('different reviewed source'); },
    (v) => { v.compatibility.runtime = '10.0.13'; },
    (v) => { v.compatibility.verification.native_gui_and_login = 'passed'; },
    (v) => { v.source.checkout.clean = false; },
    (v) => { v.source.environment.GITHUB_SHA = 'b'.repeat(40); },
  ];
  for (const mutate of cases) { const value = input(); mutate(value); assert.throws(() => createProductionEvidence(value)); }
});
