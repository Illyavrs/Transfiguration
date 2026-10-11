import { createHash, createPrivateKey, createPublicKey, sign } from 'node:crypto';
import { spawn } from 'node:child_process';
import { lstat, mkdir, readFile, readdir, realpath, writeFile } from 'node:fs/promises';
import { dirname, isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { TextDecoder } from 'node:util';

// This component publisher does not build, launch, download, or inspect a client.
// It consumes a reviewed production directory and externally supplied proof bytes.
export const MAX_ARCHIVE_BYTES = 536_870_912;
const MAX_FILE_BYTES = 268_435_456;
const MAX_TEXT_BYTES = 1_048_576;
const BRIDGE = 'osu.Game.Rulesets.Transfiguration.dll';
const SOURCE_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const PUBLIC_DER_PREFIX = Buffer.from('302a300506032b6570032100', 'hex');
export const COMPONENT_FILES = Object.freeze([
  BRIDGE, 'artifact.json', 'compatibility.json', 'THIRD-PARTY-NOTICES.md',
  'Transfiguration-README.md', 'licenses/DotNet.txt', 'licenses/Harmony.txt',
  'licenses/Iced.txt', 'licenses/Mono.Cecil.txt', 'licenses/MonoMod.txt',
]);
export const NATIVE_ASSEMBLIES = Object.freeze([
  'osu.Game.dll', 'osu.Framework.dll', 'osu!.dll',
  'System.Net.Http.dll', 'System.Net.WebSockets.Client.dll',
]);
export const REQUIRED_CONTRACTS = Object.freeze([
  'OsuGameBase.CreateEndpoints', 'Host.GetSuitableDesktopHost', 'DesktopGameHost.GetDefaultGameStorage',
  'IniConfigManager<OsuSetting>.PerformLoad:auth-only stream overlay',
  'IniConfigManager<OsuSetting>.PerformSave:shared preferences and private auth partition',
  'NativeStorage.GetStream:scoped game.ini auth filtering', 'Storage.CreateFileSafely:scoped native preference serialization',
  'OsuConfigManager.ctor(Storage):shared preferences and private auth before API',
  'APIAccess.ctor(OsuGameBase,OsuConfigManager,EndpointConfiguration,string)',
  'RulesetStore.addRuleset', 'SocketsHttpHandler.SendAsync', 'ClientWebSocket.ConnectAsync(Uri,CancellationToken)',
]);
const SOURCE_FILES = COMPONENT_FILES.filter((name) => name !== BRIDGE && name !== 'artifact.json');
// Each platform pins its own stock client build. Windows keeps its original exact-"passed" rules.
// Linux is experimental: a bare "passed" is never accepted there, every passed state must be
// qualified by the evidence that produced it, and every proof that needs a real client stays pending.
export const PLATFORM_POLICIES = Object.freeze({
  'windows-x64': Object.freeze({
    exactPassed: ['native_metadata_and_endpoints', 'http_websocket_and_redirects', 'mandatory_bootstrap_before_headless_main', 'production_destination_policy'],
    qualifiedPassed: [],
    pendingHuman: ['real_api_worker_refresh_and_login', 'native_gui_and_login'],
  }),
  'linux-x64': Object.freeze({
    exactPassed: [],
    qualifiedPassed: ['native_metadata_and_endpoints'],
    pendingHuman: ['real_api_worker_refresh_and_login', 'native_gui_and_login', 'explicit_ui_current_session_revocation'],
  }),
});
const MANIFEST_FIELDS = ['protocol', 'version', 'platform', 'repository', 'tag', 'asset', 'bytes', 'sha256', 'bridge_sha256', 'transfiguration_commit', 'launcher_requirement', 'files'];

class ReleaseError extends Error {}
function requireValue(condition, message) { if (!condition) throw new ReleaseError(message); }
function object(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }
function fields(value, names, label) {
  requireValue(object(value) && Object.keys(value).length === names.length && names.every((name) => Object.hasOwn(value, name)), `Invalid ${label} fields.`);
}
export function digest(bytes) { return createHash('sha256').update(bytes).digest('hex'); }
export function validateHash(value) {
  requireValue(typeof value === 'string' && /^[0-9a-f]{64}$/.test(value) && value.length === 64, 'Invalid lowercase SHA-256.');
  return value;
}
function commit(value) {
  requireValue(typeof value === 'string' && /^[0-9a-f]{40}$/.test(value) && value.length === 40, 'Invalid source commit.');
  return value;
}
export function validateVersion(value) {
  requireValue(typeof value === 'string' && value.length <= 64 && !/\s/.test(value) && /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(value), 'Release version must be stable canonical X.Y.Z.');
  requireValue(value.split('.').every((part) => BigInt(part) <= 18_446_744_073_709_551_615n), 'Release version exceeds the SemVer integer limit.');
  return value;
}
export function validateRepository(value) {
  requireValue(typeof value === 'string' && value.length <= 140 && /^[A-Za-z0-9][A-Za-z0-9-]{0,38}\/[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/.test(value) && !/[\s]/.test(value) && !value.split('/')[0].endsWith('-') && !value.endsWith('.git'), 'Repository must be a GitHub owner/repository name.');
  return value;
}
export function validateLauncherRequirement(value) {
  // A deliberate publishable subset of Rust semver::VersionReq: stable, full
  // comparators joined by commas. It avoids ambiguous partial/wildcard syntax.
  // https://docs.rs/semver/latest/semver/struct.VersionReq.html#syntax
  requireValue(typeof value === 'string' && value.length > 0 && value.length <= 128 && !/[\r\n\t]/.test(value), 'Invalid launcher requirement.');
  const clauses = value.split(',');
  requireValue(clauses.length <= 8, 'Invalid launcher requirement.');
  for (const clause of clauses) {
    const match = /^ *(?:>=|<=|>|<|=|\^|~)? *(\d+\.\d+\.\d+) *$/.exec(clause);
    requireValue(match !== null, 'Launcher requirement must use comma-separated stable full SemVer comparators.');
    validateVersion(match[1]);
  }
  return value;
}

export function validatePlatform(value) {
  requireValue(typeof value === 'string' && Object.hasOwn(PLATFORM_POLICIES, value), 'Invalid component compatibility platform.');
  return value;
}

export function buildManifest(input) {
  const version = validateVersion(input.version);
  const platform = validatePlatform(input.platform ?? 'windows-x64');
  fields(input.files, COMPONENT_FILES, 'component file inventory');
  const files = Object.fromEntries(COMPONENT_FILES.map((name) => [name, validateHash(input.files[name])]));
  requireValue(Number.isSafeInteger(input.bytes) && input.bytes >= 1 && input.bytes <= MAX_ARCHIVE_BYTES, 'Archive exceeds the component update size limit.');
  return {
    protocol: 1, version, platform, repository: validateRepository(input.repository),
    tag: `v${version}`, asset: `Transfiguration-${version}-${platform}.zip`,
    bytes: input.bytes, sha256: validateHash(input.sha256), bridge_sha256: files[BRIDGE],
    transfiguration_commit: commit(input.transfigurationCommit),
    launcher_requirement: validateLauncherRequirement(input.launcherRequirement), files,
  };
}

export function createSignedEnvelope(manifest, privatePem, publicHex) {
  fields(manifest, MANIFEST_FIELDS, 'signed manifest');
  const expected = buildManifest({ platform: manifest.platform, version: manifest.version, repository: manifest.repository, bytes: manifest.bytes, sha256: manifest.sha256, transfigurationCommit: manifest.transfiguration_commit, launcherRequirement: manifest.launcher_requirement, files: manifest.files });
  requireValue(MANIFEST_FIELDS.every((key) => key === 'files' || manifest[key] === expected[key]), 'Signed manifest bindings do not match.');
  validateHash(publicHex);
  let key;
  try { key = createPrivateKey(privatePem); } catch { throw new ReleaseError('Invalid component signing key.'); }
  requireValue(key.asymmetricKeyType === 'ed25519', 'Component signing key must be Ed25519.');
  const publicDer = createPublicKey(key).export({ format: 'der', type: 'spki' });
  requireValue(publicDer.length === 44 && publicDer.subarray(0, 12).equals(PUBLIC_DER_PREFIX) && publicDer.subarray(12).toString('hex') === publicHex, 'Signing identity does not match the configured component public key.');
  const bytes = Buffer.from(JSON.stringify(expected), 'utf8');
  requireValue(bytes.length <= MAX_TEXT_BYTES, 'Signed manifest exceeds the size limit.');
  const signature = sign(null, bytes, key);
  requireValue(signature.length === 64, 'Invalid Ed25519 signature length.');
  return { manifest: bytes.toString('base64'), signature: signature.toString('base64') };
}

export function readStrictJson(bytes, label = 'metadata') {
  requireValue(Buffer.isBuffer(bytes) && bytes.length > 0 && bytes.length <= MAX_TEXT_BYTES, `Invalid ${label} size.`);
  let text;
  try { text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes); } catch { throw new ReleaseError(`Invalid ${label} UTF-8.`); }
  requireValue(!text.startsWith('\ufeff'), `Invalid ${label} encoding.`);
  let offset = 0;
  const whitespace = () => { while (/[ \r\n\t]/.test(text[offset] ?? '\0')) offset++; };
  function string() {
    const start = offset++;
    while (offset < text.length) {
      const character = text[offset++];
      if (character === '\\') { offset++; continue; }
      if (character === '"') return JSON.parse(text.slice(start, offset));
    }
    throw new Error();
  }
  function value(depth) {
    requireValue(depth <= 16, `Invalid ${label} nesting.`);
    whitespace();
    if (text[offset] === '{') {
      offset++; whitespace(); const names = new Set();
      if (text[offset] === '}') { offset++; return; }
      while (true) {
        requireValue(text[offset] === '"', `Invalid ${label} JSON.`);
        const name = string().toLowerCase();
        requireValue(!names.has(name), `Duplicate ${label} field.`); names.add(name);
        whitespace(); requireValue(text[offset++] === ':', `Invalid ${label} JSON.`); value(depth + 1); whitespace();
        const delimiter = text[offset++];
        if (delimiter === '}') return;
        requireValue(delimiter === ',', `Invalid ${label} JSON.`); whitespace();
      }
    }
    if (text[offset] === '[') {
      offset++; whitespace(); if (text[offset] === ']') { offset++; return; }
      while (true) {
        value(depth + 1); whitespace(); const delimiter = text[offset++];
        if (delimiter === ']') return;
        requireValue(delimiter === ',', `Invalid ${label} JSON.`);
      }
    }
    if (text[offset] === '"') { string(); return; }
    const token = /^(?:true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)/.exec(text.slice(offset));
    requireValue(token !== null, `Invalid ${label} JSON.`);
    requireValue(!/^-?[0-9]/.test(token[0]) || /^-?(?:0|[1-9][0-9]*)$/.test(token[0]), `${label} numeric values must be integers.`);
    offset += token[0].length;
  }
  try {
    value(0); whitespace(); requireValue(offset === text.length, `Invalid ${label} JSON.`);
    const parsed = JSON.parse(text);
    requireValue(object(parsed), `${label} must be an object.`); return parsed;
  } catch (error) { if (error instanceof ReleaseError) throw error; throw new ReleaseError(`Invalid ${label} JSON.`); }
}

const SECURITY_FIELDS = {
  protocol: 1, artifact: BRIDGE, default_endpoint: 'https://illya.test',
  endpoint_mode: 'https_origin_per_session', configurable_endpoint: true,
  private_auth_filename: 'auth.illyaverse.json', shared_game_preferences: true, fixture: false,
};
export function validateProductionMetadata(artifact, compatibility, bridgeHash) {
  fields(artifact, [...Object.keys(SECURITY_FIELDS), 'sha256'], 'artifact metadata');
  fields(compatibility, [...Object.keys(SECURITY_FIELDS), 'platform', 'runtime', 'ruleset_api', 'harmony', 'assemblies', 'contracts', 'verification'], 'compatibility metadata');
  for (const [name, expected] of Object.entries(SECURITY_FIELDS)) requireValue(artifact[name] === expected && compatibility[name] === expected, `Invalid production security policy: ${name}.`);
  requireValue(validateHash(artifact.sha256) === validateHash(bridgeHash), 'Production bridge hash does not match artifact metadata.');
  const policy = PLATFORM_POLICIES[validatePlatform(compatibility.platform)];
  for (const name of ['runtime', 'ruleset_api', 'harmony']) validateVersion(compatibility[name]);
  fields(compatibility.assemblies, NATIVE_ASSEMBLIES, 'native compatibility inventory');
  for (const assembly of NATIVE_ASSEMBLIES) {
    const native = compatibility.assemblies[assembly]; fields(native, ['version', 'sha256'], 'native assembly');
    requireValue(typeof native.version === 'string' && native.version.length <= 43 && native.version.split('.').length === 4 && native.version.split('.').every((part) => /^(0|[1-9][0-9]*)$/.test(part) && !/\s/.test(part) && BigInt(part) <= 4_294_967_295n), 'Invalid canonical native assembly version.');
    requireValue(typeof native.sha256 === 'string' && native.sha256.length === 64 && /^[0-9a-fA-F]{64}$/.test(native.sha256), 'Invalid native compatibility hash.');
  }
  requireValue(Array.isArray(compatibility.contracts) && compatibility.contracts.length > 0 && compatibility.contracts.length <= 256 && compatibility.contracts.every((contract) => typeof contract === 'string' && contract.trim() === contract && contract.length > 0 && Buffer.byteLength(contract, 'utf8') <= 512) && new Set(compatibility.contracts).size === compatibility.contracts.length && REQUIRED_CONTRACTS.every((required) => compatibility.contracts.includes(required)), 'Invalid or incomplete compatibility contracts.');
  requireValue(object(compatibility.verification) && Object.keys(compatibility.verification).length > 0 && Object.keys(compatibility.verification).length <= 128 && Object.entries(compatibility.verification).every(([name, status]) => name.length > 0 && Buffer.byteLength(name, 'utf8') <= 256 && typeof status === 'string' && Buffer.byteLength(status, 'utf8') <= 256 && /^(?:passed(?:_[a-z0-9]+)*|pending(?:_[a-z0-9]+)+)$/.test(status)), 'Invalid compatibility verification states.');
  for (const name of policy.exactPassed) requireValue(compatibility.verification[name] === 'passed', 'Required production compatibility proof is absent.');
  for (const name of policy.qualifiedPassed) requireValue(/^passed(?:_[a-z0-9]+)+$/.test(compatibility.verification[name] ?? ''), 'Required production compatibility proof is absent.');
  for (const name of policy.pendingHuman) requireValue(compatibility.verification[name] === 'pending_human_test', 'Compatibility must preserve pending human client verification.');
  // A platform without exact proofs may not claim any unqualified pass: that wording is reserved for fully proven builds.
  if (policy.exactPassed.length === 0) requireValue(Object.values(compatibility.verification).every((status) => status !== 'passed'), 'Unqualified verification claims are not accepted for this platform.');
  nativeClientVersion(compatibility);
}

export function nativeClientVersion(compatibility) {
  const gameVersion = compatibility.assemblies['osu.Game.dll'].version;
  requireValue(gameVersion === compatibility.assemblies['osu!.dll'].version && gameVersion.endsWith('.0'), 'Game and entry assembly versions do not identify one stable native client.');
  return validateVersion(gameVersion.slice(0, -2));
}

export function componentCompatibilityBytes(compatibility) {
  // Older launcher packages retain the byte-pinned source schema. Only this
  // independently signed component carries a native client version binding.
  return Buffer.from(`${JSON.stringify({ ...compatibility, native_client_version: nativeClientVersion(compatibility) }, null, 2)}\n`, 'utf8');
}

export function validateVerificationEvidence(evidence, expected, compatibility) {
  fields(evidence, ['protocol', 'kind', 'transfiguration_commit', 'bridge_sha256', 'source_compatibility_sha256', 'native_client_version', 'runtime', 'ruleset_api', 'harmony', 'native_assemblies', 'checks', 'native_gui_and_login'], 'production verification evidence');
  requireValue(evidence.protocol === 1 && evidence.kind === 'transfiguration_production_metadata', 'Unsupported production verification evidence.');
  requireValue(commit(evidence.transfiguration_commit) === expected.transfigurationCommit && validateHash(evidence.bridge_sha256) === expected.bridgeSha256 && validateHash(evidence.source_compatibility_sha256) === expected.sourceCompatibilitySha256, 'Production proof does not bind the reviewed source and component bytes.');
  requireValue(evidence.native_client_version === nativeClientVersion(compatibility), 'Production proof native client version does not match.');
  for (const name of ['runtime', 'ruleset_api', 'harmony']) requireValue(evidence[name] === compatibility[name], 'Production proof runtime or dependency metadata does not match.');
  fields(evidence.native_assemblies, NATIVE_ASSEMBLIES, 'proof native compatibility inventory');
  for (const name of NATIVE_ASSEMBLIES) requireValue(validateHash(evidence.native_assemblies[name]) === compatibility.assemblies[name].sha256.toLowerCase(), 'Production proof native compatibility hashes do not match.');
  fields(evidence.checks, ['production_browser_only', 'explicit_logout'], 'production metadata proof checks');
  requireValue(evidence.checks.production_browser_only === 'passed_compiled_policy' && evidence.checks.explicit_logout === 'passed_pinned_ui_metadata', 'Current production browser and explicit logout metadata proofs are required.');
  requireValue(evidence.native_gui_and_login === 'pending_human_test', 'Production metadata evidence must preserve the pending human client test state.');
}

function contained(parent, child) {
  const suffix = relative(parent, child);
  return suffix === '' || (suffix !== '..' && !suffix.startsWith(`..${sep}`) && !isAbsolute(suffix));
}
export async function assertPlainPath(path, { absent = false } = {}) {
  requireValue(typeof path === 'string' && path.length > 0, 'Missing release path.');
  const full = resolve(path);
  for (let cursor = full; ; cursor = dirname(cursor)) {
    try { requireValue(!(await lstat(cursor)).isSymbolicLink(), 'Release path contains a symbolic link or junction.'); }
    catch (error) { if (!(absent && cursor === full && error.code === 'ENOENT')) throw error; }
    if (dirname(cursor) === cursor) break;
  }
  if (!absent) requireValue((await realpath(full)).toLowerCase() === full.toLowerCase(), 'Release path resolves to another location.');
  return full;
}

export async function collectComponent(directory) {
  const root = await assertPlainPath(directory);
  requireValue((await lstat(root)).isDirectory(), 'Production input must be a directory.');
  const contents = new Map(); let total = 0;
  async function visit(directoryPath, prefix = '') {
    for (const entry of await readdir(directoryPath, { withFileTypes: true })) {
      const inputName = prefix + entry.name;
      const path = resolve(directoryPath, entry.name); const item = await lstat(path);
      requireValue(!item.isSymbolicLink(), 'Component payload contains a symbolic link or junction.');
      if (item.isDirectory()) {
        requireValue(inputName === 'licenses', 'Unexpected component directory.'); await visit(path, 'licenses/'); continue;
      }
      const name = inputName === 'README.md' ? 'Transfiguration-README.md' : inputName;
      requireValue(item.isFile() && COMPONENT_FILES.includes(name) && !contents.has(name), 'Unexpected or duplicate component payload.');
      requireValue(item.size >= 1 && item.size <= MAX_FILE_BYTES && (!name.endsWith('.json') || item.size <= MAX_TEXT_BYTES), 'Empty or oversized component payload.');
      total += item.size; requireValue(total <= MAX_ARCHIVE_BYTES, 'Component payload exceeds the size limit.');
      const bytes = await readFile(path);
      requireValue(bytes.length === item.size, 'Component payload changed while reading.'); contents.set(name, bytes);
    }
  }
  await visit(root);
  requireValue(contents.size === COMPONENT_FILES.length && COMPONENT_FILES.every((name) => contents.has(name)), 'Component payload inventory is incomplete.');
  return { contents, hashes: Object.fromEntries(COMPONENT_FILES.map((name) => [name, digest(contents.get(name))])) };
}

function crc32(bytes) {
  let crc = 0xffff_ffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (0xedb8_8320 & -(crc & 1));
  }
  return (crc ^ 0xffff_ffff) >>> 0;
}
export function createComponentZip(contents) {
  requireValue(contents instanceof Map && contents.size === COMPONENT_FILES.length && COMPONENT_FILES.every((name) => contents.has(name)), 'Invalid ZIP component inventory.');
  const local = [], central = []; let offset = 0, centralBytes = 0;
  for (const name of COMPONENT_FILES) {
    const bytes = contents.get(name); const filename = Buffer.from(name, 'ascii');
    requireValue(Buffer.isBuffer(bytes) && bytes.length > 0 && bytes.length <= MAX_FILE_BYTES, 'Invalid ZIP component content.');
    const checksum = crc32(bytes);
    const header = Buffer.alloc(30);
    header.writeUInt32LE(0x0403_4b50, 0); header.writeUInt16LE(20, 4);
    // Stored entries, fixed 1980-01-01 DOS timestamp, no extra fields or directory
    // entries: equal reviewed bytes always produce an equal archive digest.
    header.writeUInt16LE(0x21, 12); header.writeUInt32LE(checksum, 14);
    header.writeUInt32LE(bytes.length, 18); header.writeUInt32LE(bytes.length, 22); header.writeUInt16LE(filename.length, 26);
    const record = Buffer.alloc(46);
    record.writeUInt32LE(0x0201_4b50, 0); record.writeUInt16LE(20, 4); record.writeUInt16LE(20, 6);
    record.writeUInt16LE(0x21, 14); record.writeUInt32LE(checksum, 16);
    record.writeUInt32LE(bytes.length, 20); record.writeUInt32LE(bytes.length, 24); record.writeUInt16LE(filename.length, 28); record.writeUInt32LE(offset, 42);
    local.push(header, filename, bytes); central.push(record, filename);
    offset += header.length + filename.length + bytes.length; centralBytes += record.length + filename.length;
    requireValue(offset + centralBytes + 22 <= MAX_ARCHIVE_BYTES, 'ZIP exceeds the component update size limit.');
  }
  const end = Buffer.alloc(22); end.writeUInt32LE(0x0605_4b50, 0);
  end.writeUInt16LE(COMPONENT_FILES.length, 8); end.writeUInt16LE(COMPONENT_FILES.length, 10);
  end.writeUInt32LE(centralBytes, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...local, ...central, end]);
}

async function gitBytes(root, args, maximum = MAX_TEXT_BYTES) {
  // No child process receives signing or publishing credentials, and git is
  // only used for immutable source reads and checkout cleanliness checks.
  const env = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/TOKEN|SECRET|PASSWORD|SIGNING_KEY/i.test(name)));
  return await new Promise((finish, fail) => {
    const child = spawn('git', ['--no-replace-objects', ...args], { cwd: root, env, shell: false, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    const chunks = []; let size = 0, failure;
    const timeout = setTimeout(() => { failure = new ReleaseError('Source verification timed out.'); child.kill(); }, 30_000);
    child.stdout.on('data', (chunk) => { size += chunk.length; if (size > maximum) { failure = new ReleaseError('Source verification exceeds the size limit.'); child.kill(); } else chunks.push(chunk); });
    child.stderr.on('data', () => {});
    child.once('error', () => { clearTimeout(timeout); fail(new ReleaseError('Could not verify the pinned source.')); });
    child.once('close', (code) => { clearTimeout(timeout); if (failure) fail(failure); else if (code !== 0) fail(new ReleaseError('Could not verify the pinned source.')); else finish(Buffer.concat(chunks)); });
  });
}

async function verifySource(root, sourceCommit, hashes, signed) {
  requireValue((await gitBytes(root, ['rev-parse', '--verify', 'HEAD'])).toString('utf8').trim() === commit(sourceCommit), 'Source checkout does not match the reviewed commit.');
  if (signed) requireValue((await gitBytes(root, ['status', '--porcelain', '--untracked-files=normal'])).length === 0, 'Signed releases require a committed, clean source checkout.');
  const pinned = {};
  for (const name of SOURCE_FILES) {
    const sourceName = name === 'Transfiguration-README.md' ? 'README.md' : name;
    pinned[name] = digest(await gitBytes(root, ['show', `${sourceCommit}:${sourceName}`]));
    requireValue(pinned[name] === hashes[name], 'Component documentation or compatibility bytes differ from the pinned Git blobs; production source text must use the same line endings.');
  }
  return pinned;
}

async function signingPem(environment, excludedRoots) {
  const inline = environment.TRANSFIGURATION_UPDATE_SIGNING_KEY_PEM;
  const filename = environment.TRANSFIGURATION_UPDATE_SIGNING_KEY_FILE;
  requireValue(Boolean(inline) !== Boolean(filename), 'Configure exactly one component signing key source.');
  if (inline) { requireValue(Buffer.byteLength(inline, 'utf8') <= 16_384, 'Component signing key exceeds the size limit.'); return inline; }
  const path = await assertPlainPath(filename);
  requireValue(excludedRoots.every((root) => !contained(root, path)), 'Signing key file must be outside source, payload, and release directories.');
  const item = await lstat(path); requireValue(item.isFile() && item.size >= 1 && item.size <= 16_384, 'Invalid component signing key file.');
  return await readFile(path, 'utf8');
}

export async function prepareComponentRelease(options, environment = process.env) {
  requireValue(options.mode === 'review' || options.mode === 'sign', 'Release mode must be review or sign.');
  const signed = options.mode === 'sign';
  validateVersion(options.version); validateRepository(options.repository); validateLauncherRequirement(options.launcherRequirement);
  commit(options.transfigurationCommit); validateHash(options.bridgeSha256); validateHash(options.sourceCompatibilitySha256);
  requireValue(options.repository === environment.TRANSFIGURATION_UPDATE_REPOSITORY, 'Release repository differs from the configured component update channel.');
  if (signed) validateHash(environment.TRANSFIGURATION_UPDATE_PUBLIC_KEY);
  const sourceRoot = await assertPlainPath(options.sourceRoot ?? SOURCE_ROOT);
  const production = await assertPlainPath(options.productionDirectory);
  const output = await assertPlainPath(options.outputDirectory, { absent: true });
  requireValue(!contained(production, output) && !contained(output, production) && !contained(sourceRoot, output), 'Release output must be separate from production and source directories.');
  try { await lstat(output); throw new ReleaseError('Release output already exists; choose a new directory.'); } catch (error) { if (error.code !== 'ENOENT') throw error; }
  const before = await collectComponent(production);
  requireValue(before.hashes[BRIDGE] === options.bridgeSha256 && before.hashes['compatibility.json'] === options.sourceCompatibilitySha256, 'Reviewed bridge or source compatibility digest does not match production input.');
  const compatibility = readStrictJson(before.contents.get('compatibility.json'), 'compatibility');
  validateProductionMetadata(readStrictJson(before.contents.get('artifact.json'), 'artifact'), compatibility, options.bridgeSha256);
  const sourceHashes = await verifySource(sourceRoot, options.transfigurationCommit, before.hashes, signed);
  let verificationHash = null;
  if (options.verificationFile || options.verificationSha256 || signed) {
    validateHash(options.verificationSha256);
    const evidencePath = await assertPlainPath(options.verificationFile);
    requireValue(!contained(production, evidencePath) && !contained(output, evidencePath), 'Production proof must remain outside the component ZIP and release output.');
    const evidenceItem = await lstat(evidencePath); requireValue(evidenceItem.isFile() && evidenceItem.size > 0 && evidenceItem.size <= MAX_TEXT_BYTES, 'Invalid production proof file.');
    const evidence = await readFile(evidencePath); verificationHash = digest(evidence);
    requireValue(verificationHash === options.verificationSha256, 'Production proof differs from its reviewed immutable digest.');
    validateVerificationEvidence(readStrictJson(evidence, 'production proof'), options, compatibility);
  }
  const payload = new Map(before.contents);
  payload.set('compatibility.json', componentCompatibilityBytes(compatibility));
  const payloadHashes = { ...before.hashes, 'compatibility.json': digest(payload.get('compatibility.json')) };
  const zip = createComponentZip(payload);
  const manifest = buildManifest({ ...options, bytes: zip.length, sha256: digest(zip), files: payloadHashes });
  // Re-read before signing: the archive is made from the same captured bytes.
  const after = await collectComponent(production);
  requireValue(COMPONENT_FILES.every((name) => before.hashes[name] === after.hashes[name]), 'Production component changed during release review.');
  await verifySource(sourceRoot, options.transfigurationCommit, before.hashes, signed);
  const feed = signed ? createSignedEnvelope(manifest, await signingPem(environment, [sourceRoot, production, output]), environment.TRANSFIGURATION_UPDATE_PUBLIC_KEY) : manifest;
  await mkdir(output);
  await writeFile(resolve(output, manifest.asset), zip, { flag: 'wx' });
  const staged = await readFile(resolve(output, manifest.asset));
  requireValue(staged.length === manifest.bytes && digest(staged) === manifest.sha256, 'Release archive changed during output staging.');
  const feedName = signed ? 'updates.json' : 'manifest.json';
  await writeFile(resolve(output, feedName), `${JSON.stringify(feed)}\n`, { encoding: 'utf8', flag: 'wx' });
  const review = {
    protocol: 1, mode: options.mode, version: manifest.version, repository: manifest.repository,
    transfiguration_commit: manifest.transfiguration_commit, asset: manifest.asset, bytes: manifest.bytes,
    sha256: manifest.sha256, bridge_sha256: manifest.bridge_sha256, source_compatibility_sha256: options.sourceCompatibilitySha256,
    compatibility_sha256: payloadHashes['compatibility.json'], native_client_version: nativeClientVersion(compatibility),
    verification_sha256: verificationHash, native_gui_and_login: 'pending_human_test', source_files: sourceHashes, files: manifest.files,
  };
  await writeFile(resolve(output, 'release-review.json'), `${JSON.stringify(review, null, 2)}\n`, { encoding: 'utf8', flag: 'wx' });
  return { directory: output, mode: options.mode, asset: manifest.asset, feed: feedName, review: 'release-review.json', bytes: manifest.bytes, sha256: manifest.sha256 };
}

function argumentsFor(argv) {
  const mappings = {
    mode: 'mode', version: 'version', repository: 'repository', 'launcher-requirement': 'launcherRequirement',
    'transfiguration-commit': 'transfigurationCommit', 'bridge-sha256': 'bridgeSha256', 'source-compatibility-sha256': 'sourceCompatibilitySha256',
    'production-directory': 'productionDirectory', 'output-directory': 'outputDirectory', 'source-root': 'sourceRoot',
    'verification-file': 'verificationFile', 'verification-sha256': 'verificationSha256',
  };
  const result = { mode: 'review' };
  const seen = new Set();
  for (let index = 0; index < argv.length; index += 2) {
    const argument = argv[index]?.startsWith('--') ? argv[index].slice(2) : '';
    requireValue(Object.hasOwn(mappings, argument) && typeof argv[index + 1] === 'string' && argv[index + 1].length > 0 && !seen.has(argument), 'Unknown, missing, or duplicate release arguments.');
    seen.add(argument); result[mappings[argument]] = argv[index + 1];
  }
  return result;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { process.stdout.write(`${JSON.stringify(await prepareComponentRelease(argumentsFor(process.argv.slice(2))))}\n`); }
  catch (error) {
    // External errors, child output, paths, PEM bytes, and credentials never cross
    // this error boundary. Review inputs and controlled diagnostics are sufficient.
    process.stderr.write(`${error instanceof ReleaseError ? error.message : 'Component release preparation failed.'}\n`); process.exitCode = 1;
  }
}
