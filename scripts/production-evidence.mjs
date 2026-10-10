import { execFile } from 'node:child_process';
import { lstat, mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { dirname, isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import {
  COMPONENT_FILES, NATIVE_ASSEMBLIES, assertPlainPath, collectComponent, digest,
  nativeClientVersion, readStrictJson, validateHash, validateProductionMetadata,
  validateRepository, validateVerificationEvidence,
} from './component-release.mjs';

// Only the dedicated CI producer reads an operator-provisioned installation.
// It hashes top-level DLLs and never reads player configuration or databases.
const gitExec = promisify(execFile);
const BRIDGE = COMPONENT_FILES[0];
const REQUIRED_NATIVE = [...NATIVE_ASSEMBLIES, 'Realm.dll'];
const MAX_NATIVE_BYTES = 4_294_967_296;
const MAX_FILE_BYTES = 536_870_912;
const MAX_SOURCE_BYTES = 134_217_728;
const TEXT_LIMIT = 8_388_608;
const SOURCE_PAYLOAD = COMPONENT_FILES.filter((name) => ![BRIDGE, 'artifact.json'].includes(name));
const MODES = ['freeze', 'bind', 'finalize'];

function requireValue(value, message) { if (!value) throw new Error(message); }
function sourceCommit(value) { requireValue(typeof value === 'string' && value.length === 40 && /^[0-9a-f]{40}$/.test(value), 'A full lowercase source commit is required.'); return value; }
function same(value, expected, label) { requireValue(JSON.stringify(value) === JSON.stringify(expected), `${label} changed after freezing.`); }
function contained(parent, child) { const suffix = relative(parent, child); return suffix === '' || (suffix !== '..' && !suffix.startsWith(`..${sep}`) && !isAbsolute(suffix)); }
function fields(value, names, label) { requireValue(value && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).length === names.length && names.every((name) => Object.hasOwn(value, name)), `Invalid ${label} fields.`); }
function localAbsolute(path) { requireValue(typeof path === 'string' && isAbsolute(path) && !path.startsWith('\\\\') && !path.startsWith('//'), 'Absolute local paths are required.'); return resolve(path); }
const sourceName = (name) => name === 'Transfiguration-README.md' ? 'README.md' : name;

export function validateDispatch(options, environment, source) {
  sourceCommit(options.sourceCommit); validateRepository(options.repository);
  requireValue(environment.GITHUB_EVENT_NAME === 'workflow_dispatch' && environment.GITHUB_REPOSITORY === 'Illyavrs/Transfiguration' && options.repository === environment.GITHUB_REPOSITORY, 'Only the trusted component repository manual dispatch is accepted.');
  requireValue(sourceCommit(environment.GITHUB_SHA) === options.sourceCommit && source.head === options.sourceCommit, 'Dispatch, requested source and checkout commit must match.');
  requireValue(source.clean === true, 'A committed clean source checkout is required.');
  requireValue(resolve(source.root).toLowerCase() === resolve(options.sourceRoot).toLowerCase(), 'Source root must be the component Git checkout.');
  requireValue(environment.ILLYAVERSE_LAZER_SERVER_ENDPOINT === 'https://illya.test', 'Detached proofs require the fixed public HTTPS task origin without network execution.');
  for (const name of ['DOTNET_STARTUP_HOOKS', 'DOTNET_ADDITIONAL_DEPS', 'DOTNET_SHARED_STORE']) requireValue(!environment[name], 'Inherited .NET hooks or dependency overrides are not allowed in detached proofs.');
  for (const name of ['CORECLR_ENABLE_PROFILING', 'COR_ENABLE_PROFILING']) requireValue(!environment[name] || environment[name] === '0', 'Inherited native profiling is not allowed in detached proofs.');
}

export function validateFrozenNative(receipt, compatibility) {
  fields(receipt, ['protocol', 'native_root', 'frozen_inputs'], 'frozen native receipt');
  requireValue(receipt.protocol === 1 && Array.isArray(receipt.frozen_inputs) && receipt.frozen_inputs.length >= REQUIRED_NATIVE.length && receipt.frozen_inputs.length <= 1024, 'Invalid frozen native inventory.');
  const root = localAbsolute(receipt.native_root), names = new Map();
  let previous = '';
  for (const entry of receipt.frozen_inputs) {
    fields(entry, ['path', 'sha256'], 'frozen native entry');
    const path = localAbsolute(entry.path), name = path.slice(path.lastIndexOf(sep) + 1), key = name.toLowerCase();
    requireValue(dirname(path).toLowerCase() === root.toLowerCase() && /^[^/\\]+\.dll$/i.test(name) && !names.has(key) && key > previous, 'Native receipt must contain unique sorted top-level DLL paths.');
    previous = key; names.set(key, validateHash(entry.sha256));
  }
  for (const name of REQUIRED_NATIVE) requireValue(names.has(name.toLowerCase()), `Required pinned native reference missing: ${name}.`);
  for (const name of NATIVE_ASSEMBLIES) requireValue(names.get(name.toLowerCase()) === compatibility.assemblies[name].sha256.toLowerCase(), `Native compatibility mismatch: ${name}.`);
  return receipt;
}

export function validateCommands(receipt, paths) {
  fields(receipt, ['protocol', 'commands'], 'command receipt');
  const expected = [
    { kind: 'build', command: resolve(paths.sourceRoot, 'build.ps1'), args: ['-NativeRoot', paths.nativeRoot] },
    { kind: 'publish-proof', command: 'dotnet', args: ['publish', resolve(paths.sourceRoot, 'tests/Proof.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:UseAppHost=true', '-p:RuntimeFrameworkVersion=10.0.12', `-p:NativeRoot=${paths.nativeRoot}`, '-o', paths.proofRuntime, '--nologo'] },
    { kind: 'production-browser-only', command: paths.proofExe, args: ['--production-browser-only', paths.bridgeDll] },
    { kind: 'production-logout-contracts', command: paths.proofExe, args: ['--production-logout-contracts', paths.bridgeDll, paths.nativeRoot, paths.frozenNative] },
    { kind: 'production-welcome-contracts', command: paths.proofExe, args: ['--production-welcome-contracts', paths.bridgeDll, paths.nativeRoot, paths.frozenNative] },
    { kind: 'production-relax-contracts', command: paths.proofExe, args: ['--production-relax-contracts', paths.bridgeDll, paths.nativeRoot, paths.frozenNative] },
  ];
  requireValue(receipt.protocol === 1 && Array.isArray(receipt.commands) && receipt.commands.length === expected.length, 'Exactly the build, publish and four detached production proofs are required.');
  for (const [index, result] of receipt.commands.entries()) {
    fields(result, ['kind', 'command', 'args', 'exit_code', 'started_at', 'finished_at'], 'command result');
    const command = expected[index];
    requireValue(result.kind === command.kind && result.command === command.command && Array.isArray(result.args) && JSON.stringify(result.args) === JSON.stringify(command.args), 'Unexpected production command or fixture/bootstrap invocation.');
    requireValue(result.exit_code === 0 && typeof result.started_at === 'string' && typeof result.finished_at === 'string' && /^\d{4}-\d{2}-\d{2}T/.test(result.started_at) && Number.isFinite(Date.parse(result.started_at)) && Number.isFinite(Date.parse(result.finished_at)) && Date.parse(result.finished_at) >= Date.parse(result.started_at), 'A production command did not complete successfully.');
    if (index > 0) requireValue(Date.parse(result.started_at) >= Date.parse(receipt.commands[index - 1].finished_at), 'Production commands must execute sequentially.');
  }
  return receipt;
}

export function detachedProofChecks(commands, paths) {
  validateCommands(commands, paths);
  return {
    production_browser_only: 'passed_compiled_policy', explicit_logout: 'passed_pinned_ui_metadata',
    production_welcome_contracts: 'passed_pinned_metadata_and_synthetic_callbacks',
    production_relax_contracts: 'passed_pinned_metadata_and_synthetic_queries',
  };
}

export function createProductionEvidence({ source, expected, compatibility, artifact, commands, paths, before, after }) {
  // Callers supply observed snapshots, never booleans which could skip checks.
  validateDispatch(expected, source.environment, source.checkout);
  validateCommands(commands, paths);
  requireValue(compatibility.runtime === '10.0.12' && compatibility.ruleset_api === '2026.818.0' && compatibility.harmony === '2.4.2', 'Producer runtime and dependency pins must be reviewed before changing.');
  validateFrozenNative(before.native, compatibility); validateFrozenNative(after.native, compatibility);
  for (const name of ['source', 'native', 'component', 'proof_runtime']) same(after[name], before[name], name);
  requireValue(before.component[BRIDGE] === expected.bridgeSha256 && before.component['compatibility.json'] === expected.sourceCompatibilitySha256, 'Production bytes are not the reviewed component.');
  validateProductionMetadata(artifact, compatibility, expected.bridgeSha256);
  const evidence = {
    protocol: 1, kind: 'transfiguration_production_metadata', transfiguration_commit: expected.sourceCommit,
    bridge_sha256: expected.bridgeSha256, source_compatibility_sha256: expected.sourceCompatibilitySha256,
    native_client_version: nativeClientVersion(compatibility), runtime: compatibility.runtime,
    ruleset_api: compatibility.ruleset_api, harmony: compatibility.harmony,
    native_assemblies: Object.fromEntries(NATIVE_ASSEMBLIES.map((name) => [name, compatibility.assemblies[name].sha256.toLowerCase()])),
    checks: { production_browser_only: 'passed_compiled_policy', explicit_logout: 'passed_pinned_ui_metadata' },
    native_gui_and_login: 'pending_human_test',
  };
  validateVerificationEvidence(evidence, { transfigurationCommit: expected.sourceCommit, bridgeSha256: expected.bridgeSha256, sourceCompatibilitySha256: expected.sourceCompatibilitySha256 }, compatibility);
  return evidence;
}

async function bytes(path, maximum = MAX_FILE_BYTES) {
  await assertPlainPath(path);
  const item = await lstat(path); requireValue(item.isFile() && item.size > 0 && item.size <= maximum, 'Input is empty, oversized or not a regular file.');
  const value = await readFile(path); requireValue(value.length === item.size, 'Input changed during reading.'); return value;
}
async function json(path) { return readStrictJson(await bytes(path, 1_048_576), 'producer receipt'); }
async function exclusiveJson(path, value) { await writeFile(path, `${JSON.stringify(value, null, 2)}\n`, { encoding: 'utf8', flag: 'wx' }); }
async function git(root, args) {
  const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/TOKEN|SECRET|PASSWORD|SIGNING_KEY/i.test(name)));
  const result = await gitExec('git', ['--no-replace-objects', ...args], { cwd: root, env: environment, encoding: 'buffer', timeout: 30_000, maxBuffer: MAX_SOURCE_BYTES, windowsHide: true });
  return result.stdout;
}

async function snapshotSource(options, environment) {
  const root = await assertPlainPath(options.sourceRoot);
  const checkout = {
    head: (await git(root, ['rev-parse', '--verify', 'HEAD'])).toString('utf8').trim(),
    root: (await git(root, ['rev-parse', '--show-toplevel'])).toString('utf8').trim(),
    clean: (await git(root, ['status', '--porcelain', '--untracked-files=all'])).length === 0,
  };
  validateDispatch(options, environment, checkout);
  const records = (await git(root, ['ls-tree', '-rz', '--full-tree', 'HEAD'])).toString('utf8').split('\0').filter(Boolean);
  requireValue(records.length > 0 && records.length <= 4096, 'Source inventory exceeds the producer limit.');
  const files = {}; let total = 0;
  for (const record of records) {
    const match = /^(100644|100755) blob ([0-9a-f]{40})\t([^\r\n]+)$/.exec(record);
    requireValue(match && !isAbsolute(match[3]) && !match[3].split('/').some((part) => !part || part === '.' || part === '..'), 'Only regular tracked component source files are accepted.');
    const value = await bytes(resolve(root, match[3]), MAX_SOURCE_BYTES); total += value.length;
    requireValue(total <= MAX_SOURCE_BYTES, 'Source bytes exceed the producer limit.');
    files[match[3]] = { mode: match[1], blob: match[2], sha256: digest(value) };
  }
  // Default Windows autocrlf must not alter any text copied into the release.
  for (const name of SOURCE_PAYLOAD) {
    const path = sourceName(name), blob = await git(root, ['cat-file', 'blob', `${options.sourceCommit}:${path}`]);
    requireValue(files[path]?.sha256 === digest(blob), 'Packaged source text must equal exact Git blob bytes; disable autocrlf before checkout.');
  }
  return { checkout, files };
}

export async function snapshotNative(nativeRoot, compatibility) {
  const root = await assertPlainPath(nativeRoot);
  requireValue((await lstat(root)).isDirectory(), 'Pinned native installation must be an existing directory.');
  const entries = (await readdir(root, { withFileTypes: true })).filter((entry) => /\.dll$/i.test(entry.name)).sort((a, b) => a.name.toLowerCase().localeCompare(b.name.toLowerCase(), 'en'));
  requireValue(entries.length <= 1024, 'Native DLL inventory exceeds the producer limit.');
  const frozen = []; let total = 0;
  for (const entry of entries) {
    requireValue(entry.isFile() && !entry.isSymbolicLink(), 'Native DLL references must be regular files.');
    const path = resolve(root, entry.name), value = await bytes(path); total += value.length;
    requireValue(total <= MAX_NATIVE_BYTES, 'Native DLL bytes exceed the producer limit.');
    frozen.push({ path, sha256: digest(value) });
  }
  // Ordinal ordering is used by the proof inventory validator.
  frozen.sort((a, b) => a.path.toLowerCase() < b.path.toLowerCase() ? -1 : 1);
  return validateFrozenNative({ protocol: 1, native_root: root, frozen_inputs: frozen }, compatibility);
}

export async function snapshotRuntime(directory) {
  const root = await assertPlainPath(directory), files = {};
  async function visit(path, prefix = '') {
    const entries = (await readdir(path, { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name, 'en'));
    for (const entry of entries) {
      const relativeName = prefix + entry.name, full = resolve(path, entry.name);
      requireValue(!entry.isSymbolicLink(), 'Proof runtime must not contain linked files.');
      if (entry.isDirectory()) await visit(full, `${relativeName}/`);
      else { requireValue(entry.isFile(), 'Unexpected proof runtime file.'); files[relativeName] = digest(await bytes(full)); }
      requireValue(Object.keys(files).length <= 4096, 'Proof runtime inventory exceeds the producer limit.');
    }
  }
  await visit(root);
  requireValue(files['Transfiguration.Proof.exe'] && files['Transfiguration.Proof.dll'] && files['Transfiguration.Proof.runtimeconfig.json'], 'Published self-contained proof runtime is incomplete.');
  const runtime = readStrictJson(await bytes(resolve(root, 'Transfiguration.Proof.runtimeconfig.json'), 1_048_576), 'proof runtime configuration');
  requireValue(runtime.runtimeOptions?.includedFrameworks?.some((framework) => framework.name === 'Microsoft.NETCore.App' && framework.version === '10.0.12'), 'Proof must use the pinned self-contained .NET 10.0.12 runtime.');
  requireValue(!Object.keys(files).some((name) => ['osu.Game.dll', 'osu.Framework.dll', 'osu!.dll', 'Realm.dll'].some((native) => name.toLowerCase() === native.toLowerCase())), 'Stock game DLLs must not be copied into the proof runtime.');
  return files;
}

function commandPaths(options) {
  return {
    sourceRoot: options.sourceRoot, nativeRoot: options.nativeRoot,
    proofRuntime: resolve(options.outputRoot, 'proof-runtime'), proofExe: resolve(options.outputRoot, 'proof-runtime/Transfiguration.Proof.exe'),
    bridgeDll: resolve(options.sourceRoot, 'artifacts', BRIDGE), frozenNative: resolve(options.outputRoot, 'provenance/frozen-native.json'),
  };
}

export async function runProducer(mode, supplied, environment = process.env) {
  requireValue(MODES.includes(mode) && process.platform === 'win32' && /^24\./.test(process.versions.node), 'The producer requires the dedicated Windows runner and Node 24.');
  const options = { ...supplied, sourceRoot: localAbsolute(supplied.sourceRoot), nativeRoot: localAbsolute(supplied.nativeRoot), outputRoot: localAbsolute(supplied.outputRoot) };
  requireValue(!contained(options.sourceRoot, options.outputRoot) && !contained(options.outputRoot, options.sourceRoot) && !contained(options.nativeRoot, options.outputRoot) && !contained(options.outputRoot, options.nativeRoot) && !contained(options.sourceRoot, options.nativeRoot) && !contained(options.nativeRoot, options.sourceRoot), 'Source, native references and owned output must be separate.');
  const provenance = resolve(options.outputRoot, 'provenance'), source = await snapshotSource(options, environment);
  const compatibility = readStrictJson(await bytes(resolve(options.sourceRoot, 'compatibility.json'), 1_048_576), 'source compatibility');
  requireValue(compatibility.runtime === '10.0.12' && compatibility.ruleset_api === '2026.818.0' && compatibility.harmony === '2.4.2', 'Producer runtime and dependency pins must be reviewed before changing.');
  const native = await snapshotNative(options.nativeRoot, compatibility);
  if (mode === 'freeze') {
    await assertPlainPath(options.outputRoot, { absent: true });
    await mkdir(options.outputRoot); await mkdir(provenance);
    await exclusiveJson(resolve(provenance, 'source-before.json'), { protocol: 1, repository: options.repository, source_commit: options.sourceCommit, proof_endpoint: environment.ILLYAVERSE_LAZER_SERVER_ENDPOINT, source });
    await exclusiveJson(resolve(provenance, 'frozen-native.json'), native);
    return { mode, source_commit: options.sourceCommit, native_dlls: native.frozen_inputs.length };
  }
  await assertPlainPath(options.outputRoot);
  const initial = await json(resolve(provenance, 'source-before.json'));
  requireValue(initial.protocol === 1 && initial.repository === options.repository && initial.source_commit === options.sourceCommit, 'Frozen dispatch source differs from this operation.');
  requireValue(initial.proof_endpoint === environment.ILLYAVERSE_LAZER_SERVER_ENDPOINT, 'Frozen task endpoint differs from this operation.');
  same(source, initial.source, 'source'); same(native, await json(resolve(provenance, 'frozen-native.json')), 'native');
  const component = await collectComponent(resolve(options.sourceRoot, 'artifacts'));
  const artifact = readStrictJson(component.contents.get('artifact.json'), 'compiled artifact');
  validateProductionMetadata(artifact, compatibility, component.hashes[BRIDGE]);
  for (const name of SOURCE_PAYLOAD) requireValue(component.hashes[name] === source.files[sourceName(name)].sha256, 'Production bundle text differs from frozen source bytes.');
  const proofRuntime = await snapshotRuntime(commandPaths(options).proofRuntime);
  const snapshot = { source, native, component: component.hashes, proof_runtime: proofRuntime };
  if (mode === 'bind') {
    await exclusiveJson(resolve(provenance, 'production-before.json'), snapshot);
    return { mode, bridge_sha256: component.hashes[BRIDGE], proof_runtime_files: Object.keys(proofRuntime).length };
  }
  const before = await json(resolve(provenance, 'production-before.json'));
  const commands = await json(resolve(provenance, 'commands.json'));
  const expected = { ...options, bridgeSha256: before.component[BRIDGE], sourceCompatibilitySha256: source.files['compatibility.json'].sha256 };
  const evidence = createProductionEvidence({ source: { environment, checkout: source.checkout }, expected, compatibility, artifact, commands, paths: commandPaths(options), before, after: snapshot });
  const logs = {};
  for (const command of commands.commands) logs[`${command.kind}.log`] = digest(await bytes(resolve(provenance, `${command.kind}.log`), TEXT_LIMIT));
  // Re-read every input immediately before staging. Failed validation leaves no
  // production-verification.json and therefore no releasable success artifact.
  same(await snapshotSource(options, environment), source, 'source');
  same(await snapshotNative(options.nativeRoot, compatibility), native, 'native');
  same((await collectComponent(resolve(options.sourceRoot, 'artifacts'))).hashes, component.hashes, 'component');
  same(await snapshotRuntime(commandPaths(options).proofRuntime), proofRuntime, 'proof runtime');
  const proofChecks = detachedProofChecks(commands, commandPaths(options));
  // Mandatory welcome and RX results are recorded before artifact staging.
  // The existing publisher evidence schema intentionally retains two checks.
  await exclusiveJson(resolve(provenance, 'detached-proof-results.json'), {
    protocol: 1, source_commit: options.sourceCommit, bridge_sha256: expected.bridgeSha256,
    proof_endpoint: environment.ILLYAVERSE_LAZER_SERVER_ENDPOINT, checks: proofChecks, logs, native_gui_and_login: 'pending_human_test',
  });
  const verified = resolve(options.outputRoot, 'verified'), production = resolve(verified, 'production');
  await mkdir(verified); await mkdir(production); await mkdir(resolve(production, 'licenses'));
  for (const name of COMPONENT_FILES) await writeFile(resolve(production, name), component.contents.get(name), { flag: 'wx' });
  same((await collectComponent(production)).hashes, component.hashes, 'staged component');
  const evidenceBytes = Buffer.from(`${JSON.stringify(evidence, null, 2)}\n`, 'utf8');
  await exclusiveJson(resolve(provenance, 'production-after.json'), snapshot);
  await exclusiveJson(resolve(provenance, 'producer-result.json'), {
    protocol: 1, repository: options.repository, source_commit: options.sourceCommit,
    proof_endpoint: environment.ILLYAVERSE_LAZER_SERVER_ENDPOINT,
    run_id: environment.GITHUB_RUN_ID, run_attempt: environment.GITHUB_RUN_ATTEMPT,
    bridge_sha256: expected.bridgeSha256, source_compatibility_sha256: expected.sourceCompatibilitySha256,
    verification_sha256: digest(evidenceBytes), logs, files: component.hashes,
    additional_proofs: {
      production_welcome_contracts: proofChecks.production_welcome_contracts,
      production_relax_contracts: proofChecks.production_relax_contracts,
    },
    native_gui_and_login: 'pending_human_test',
  });
  await writeFile(resolve(verified, 'production-verification.json'), evidenceBytes, { flag: 'wx' });
  return { mode, source_commit: options.sourceCommit, bridge_sha256: expected.bridgeSha256, verification_sha256: digest(evidenceBytes), native_gui_and_login: 'pending_human_test' };
}

function parseArguments(args) {
  const mode = args.shift(), names = { '--source-root': 'sourceRoot', '--source-commit': 'sourceCommit', '--repository': 'repository', '--native-root': 'nativeRoot', '--output-root': 'outputRoot' }, options = {};
  requireValue(MODES.includes(mode) && args.length === 10, 'Usage: production-evidence.mjs freeze|bind|finalize --source-root PATH --source-commit SHA --repository OWNER/REPO --native-root PATH --output-root PATH');
  while (args.length) { const key = args.shift(), value = args.shift(); requireValue(names[key] && value && !Object.hasOwn(options, names[key]), 'Unknown, duplicate or missing producer argument.'); options[names[key]] = value; }
  return { mode, options };
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { const { mode, options } = parseArguments(process.argv.slice(2)); console.log(JSON.stringify(await runProducer(mode, options))); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
