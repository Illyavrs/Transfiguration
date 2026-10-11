import assert from 'node:assert/strict';
import { generateKeyPairSync, verify } from 'node:crypto';
import { mkdtemp, mkdir, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';
import {
  COMPONENT_FILES, MAX_ARCHIVE_BYTES, NATIVE_ASSEMBLIES, REQUIRED_CONTRACTS, assertPlainPath,
  buildManifest, collectComponent, componentCompatibilityBytes,
  createComponentZip, createSignedEnvelope, digest, nativeClientVersion,
  prepareComponentRelease, readStrictJson, validateLauncherRequirement,
  validateProductionMetadata, validateRepository, validateVerificationEvidence,
  validateVersion,
} from './component-release.mjs';

const BRIDGE = COMPONENT_FILES[0];
const bridgeHash = digest('synthetic component bytes, never a compiled DLL');
const fileHashes = () => Object.fromEntries(COMPONENT_FILES.map((name) => [name, digest(name)]));
const manifestInput = () => ({ version: '0.1.1', repository: 'Illyaverse/Transfiguration', bytes: 4096, sha256: digest('synthetic ZIP'), transfigurationCommit: 'a'.repeat(40), launcherRequirement: '>=0.1.0, <0.2.0', files: fileHashes() });
const security = () => ({ protocol: 1, artifact: BRIDGE, default_endpoint: 'https://illya.test', endpoint_mode: 'https_origin_per_session', configurable_endpoint: true, private_auth_filename: 'auth.illyaverse.json', shared_game_preferences: true, fixture: false });
const compatibility = () => ({
  ...security(), platform: 'windows-x64', runtime: '10.0.12', ruleset_api: '2026.818.0', harmony: '2.4.2',
  assemblies: Object.fromEntries(NATIVE_ASSEMBLIES.map((name) => [name, { version: name === 'osu.Game.dll' || name === 'osu!.dll' ? '2026.1005.1.0' : '10.0.0.0', sha256: digest(name).toUpperCase() }])),
  contracts: [...REQUIRED_CONTRACTS, 'synthetic declared contract'],
  verification: {
    native_metadata_and_endpoints: 'passed', http_websocket_and_redirects: 'passed',
    mandatory_bootstrap_before_headless_main: 'passed', production_destination_policy: 'passed',
    real_api_worker_refresh_and_login: 'pending_human_test', native_gui_and_login: 'pending_human_test',
  },
});
const expectedProof = () => ({ transfigurationCommit: 'a'.repeat(40), bridgeSha256: bridgeHash, sourceCompatibilitySha256: digest('synthetic source compatibility') });
const evidence = () => ({
  protocol: 1, kind: 'transfiguration_production_metadata', transfiguration_commit: expectedProof().transfigurationCommit,
  bridge_sha256: bridgeHash, source_compatibility_sha256: expectedProof().sourceCompatibilitySha256,
  native_client_version: '2026.1005.1', runtime: '10.0.12', ruleset_api: '2026.818.0', harmony: '2.4.2',
  native_assemblies: Object.fromEntries(NATIVE_ASSEMBLIES.map((name) => [name, digest(name)])),
  checks: { production_browser_only: 'passed_compiled_policy', explicit_logout: 'passed_pinned_ui_metadata' },
  native_gui_and_login: 'pending_human_test',
});

function ephemeralIdentity() {
  // Only ephemeral generated keys sign synthetic in-memory manifests in this
  // test suite. No environment key, installed client or production package read.
  const { privateKey, publicKey } = generateKeyPairSync('ed25519');
  return { pem: privateKey.export({ type: 'pkcs8', format: 'pem' }), publicKey, raw: publicKey.export({ type: 'spki', format: 'der' }).subarray(12).toString('hex') };
}

async function syntheticDirectory(run) {
  const parent = await mkdtemp(join(tmpdir(), 'transfiguration-component-synthetic-'));
  const directory = join(parent, 'production');
  try {
    await mkdir(directory); await mkdir(join(directory, 'licenses'));
    for (const name of COMPONENT_FILES) await writeFile(join(directory, name === 'Transfiguration-README.md' ? 'README.md' : name), `synthetic payload: ${name}\n`);
    await run(parent, directory);
  } finally {
    // Native removal is confined to the freshly created and checked test root.
    assert(resolve(parent).startsWith(resolve(tmpdir()) + '\\transfiguration-component-synthetic-') || resolve(parent).startsWith(resolve(tmpdir()) + '/transfiguration-component-synthetic-'));
    await rm(parent, { recursive: true, force: true });
  }
}

test('manifest has the exact independent component contract and ten-file inventory', () => {
  const value = buildManifest(manifestInput());
  assert.deepEqual(Object.keys(value), ['protocol', 'version', 'platform', 'repository', 'tag', 'asset', 'bytes', 'sha256', 'bridge_sha256', 'transfiguration_commit', 'launcher_requirement', 'files']);
  assert.equal(value.asset, 'Transfiguration-0.1.1-windows-x64.zip');
  assert.equal(value.tag, 'v0.1.1'); assert.equal(value.platform, 'windows-x64');
  assert.equal(value.bridge_sha256, value.files[BRIDGE]); assert.deepEqual(Object.keys(value.files), COMPONENT_FILES);
});

test('stable versions, repositories, and documented requirement subset reject ambiguous inputs', () => {
  for (const version of ['0.1.1', '1.2.3', '2026.1005.1']) assert.equal(validateVersion(version), version);
  for (const version of ['1.2', 'v1.2.3', '01.2.3', '1.2.3-alpha', '1.2.3+build', '1.2.3\n', '1.2.3/evil', '18446744073709551616.0.0']) assert.throws(() => validateVersion(version));
  assert.equal(validateRepository('Illyaverse/Transfiguration'), 'Illyaverse/Transfiguration');
  for (const repo of ['owner/../repo', 'https://github.com/owner/repo', 'owner/repo\n', 'owner/repo/extra', 'owner@token/repo', 'owner-/repo', 'owner/repo.git']) assert.throws(() => validateRepository(repo));
  for (const req of ['>=0.1.0, <0.2.0', '^1.2.3', '=1.2.3', '~0.1.0', '1.0.0', ' <= 1.2.3 ']) assert.equal(validateLauncherRequirement(req), req);
  for (const req of ['', '*', '1.*', '^1.2', '>=1.0.0 <2.0.0', '1.0.0 || 2.0.0', '>=1.0.0,', '1.0.0\n', '^01.2.3', ' '.repeat(129)]) assert.throws(() => validateLauncherRequirement(req));
});

test('manifest rejects inventory, hash, pin and size substitutions', () => {
  const missing = fileHashes(); delete missing['artifact.json'];
  for (const changed of [
    { files: missing }, { files: { ...fileHashes(), 'extra.dll': digest('extra') } },
    { files: { ...fileHashes(), [BRIDGE]: 'A'.repeat(64) } }, { files: [] },
    { transfigurationCommit: 'A'.repeat(40) }, { sha256: 'a'.repeat(63) },
    ...[0, -1, 0.5, MAX_ARCHIVE_BYTES + 1, Infinity, '123'].map((bytes) => ({ bytes })),
  ]) assert.throws(() => buildManifest({ ...manifestInput(), ...changed }));
  assert.equal(buildManifest({ ...manifestInput(), bytes: MAX_ARCHIVE_BYTES }).bytes, MAX_ARCHIVE_BYTES);
});

test('synthetic Ed25519 envelope authenticates exact UTF-8 bytes and detects tampering', () => {
  const identity = ephemeralIdentity(), manifest = buildManifest(manifestInput());
  const envelope = createSignedEnvelope(manifest, identity.pem, identity.raw);
  assert.deepEqual(Object.keys(envelope), ['manifest', 'signature']);
  const bytes = Buffer.from(envelope.manifest, 'base64'), signature = Buffer.from(envelope.signature, 'base64');
  assert.equal(signature.length, 64); assert.equal(bytes.toString('utf8'), JSON.stringify(manifest));
  assert(verify(null, bytes, identity.publicKey, signature));
  assert(!verify(null, Buffer.concat([bytes, Buffer.from(' ')]), identity.publicKey, signature));
  assert.deepEqual(createSignedEnvelope(manifest, identity.pem, identity.raw), envelope);
});

test('signer independently rejects mismatched identity and altered manifest bindings', () => {
  const identity = ephemeralIdentity(), other = ephemeralIdentity(), manifest = buildManifest(manifestInput());
  assert.throws(() => createSignedEnvelope(manifest, identity.pem, other.raw), /identity/);
  assert.throws(() => createSignedEnvelope(manifest, 'synthetic invalid key', identity.raw), /signing key/);
  const nonEd = generateKeyPairSync('ec', { namedCurve: 'prime256v1' }).privateKey.export({ type: 'pkcs8', format: 'pem' });
  assert.throws(() => createSignedEnvelope(manifest, nonEd, identity.raw), /Ed25519/);
  for (const changed of [{ extra: true }, { protocol: 2 }, { tag: 'v0.1.0' }, { asset: 'wrong.zip' }, { platform: 'linux-x64' }, { bridge_sha256: digest('another bridge') }]) assert.throws(() => createSignedEnvelope({ ...manifest, ...changed }, identity.pem, identity.raw));
});

test('strict metadata parsing rejects duplicate case aliases, invalid UTF-8 and excessive nesting', () => {
  assert.deepEqual(readStrictJson(Buffer.from('{"valid": [1, false, {"x":"escaped\\\"quote"}]}')), { valid: [1, false, { x: 'escaped"quote' }] });
  for (const text of ['{"fixture":true,"fixture":false}', '{"fixture":true,"FIXTURE":false}', '{"a":{"x":1,"X":2}}', '{"x":1,}', '[]', '{"x":01}', '{"x":NaN}', '{"protocol":1.0}', '{"protocol":1e0}', '{"x":true} trailing', '\ufeff{}']) assert.throws(() => readStrictJson(Buffer.from(text)));
  assert.throws(() => readStrictJson(Buffer.from([0xff, 0xfe])));
  assert.throws(() => readStrictJson(Buffer.from('{"x":'.repeat(18) + '{}' + '}'.repeat(18))));
});

test('production policies require literal flags and the exact bridge digest', () => {
  const original = compatibility(), artifact = { ...security(), sha256: bridgeHash };
  validateProductionMetadata(artifact, original, bridgeHash);
  for (const changed of [{ fixture: true }, { shared_game_preferences: false }, { private_auth_filename: 'game.ini' }, { default_endpoint: 'https://osu.ppy.sh' }, { configurable_endpoint: 'true' }, { sha256: digest('another bridge') }, { sha256: bridgeHash.toUpperCase() }, { extra: true }]) assert.throws(() => validateProductionMetadata({ ...artifact, ...changed }, original, bridgeHash));
  for (const changed of [{ platform: 'linux-x64' }, { fixture: true }, { runtime: '10.0.12-preview' }, { assemblies: { ...original.assemblies, 'Realm.dll': original.assemblies['osu.Game.dll'] } }, { verification: { ...original.verification, native_gui_and_login: 'passed' } }, { contracts: ['missing mandatory contracts'] }, { contracts: [...original.contracts, 'x'.repeat(513)] }, { verification: { ...original.verification, ['x'.repeat(257)]: 'pending_human_test' } }]) assert.throws(() => validateProductionMetadata(artifact, { ...original, ...changed }, bridgeHash));
  for (const version of ['010.0.0.0', '4294967296.0.0.0']) {
    const changed = structuredClone(original); changed.assemblies['osu.Framework.dll'].version = version;
    assert.throws(() => validateProductionMetadata(artifact, changed, bridgeHash));
  }
  validateProductionMetadata(artifact, { ...original, verification: { ...original.verification, explicit_ui_current_session_revocation: 'pending_pinned_metadata_proof_and_human_test' } }, bridgeHash);
});

test('signed compatibility enrichment derives one stable native version without mutating source', () => {
  const source = compatibility(), original = JSON.stringify(source);
  assert.equal(nativeClientVersion(source), '2026.1005.1');
  const enriched = readStrictJson(componentCompatibilityBytes(source));
  assert.equal(enriched.native_client_version, '2026.1005.1');
  assert.deepEqual(enriched, { ...source, native_client_version: '2026.1005.1' });
  assert.equal(JSON.stringify(source), original); assert(!Object.hasOwn(source, 'native_client_version'));
  for (const version of ['2026.1005.1.1', '2026.1005.2.0', '02026.1005.1.0']) {
    const changed = structuredClone(source); changed.assemblies['osu!.dll'].version = version;
    assert.throws(() => nativeClientVersion(changed));
  }
});

test('external proof binds source, bridge, runtime and native hashes while preserving actual proof scope', () => {
  validateVerificationEvidence(evidence(), expectedProof(), compatibility());
  for (const changed of [
    { transfiguration_commit: 'b'.repeat(40) }, { bridge_sha256: digest('different DLL') },
    { source_compatibility_sha256: digest('different source') }, { runtime: '10.0.13' },
    { native_client_version: '2026.1005.2' }, { native_gui_and_login: 'passed' }, { extra: true },
    { checks: { ...evidence().checks, production_browser_only: 'passed_headless' } },
    { checks: { ...evidence().checks, explicit_logout: 'passed_native_ui' } },
    { native_assemblies: { ...evidence().native_assemblies, 'osu.Game.dll': digest('different native') } },
  ]) assert.throws(() => validateVerificationEvidence({ ...evidence(), ...changed }, expectedProof(), compatibility()));
});

test('collector admits exactly ten original inputs, renames README, and hashes every byte', async () => {
  await syntheticDirectory(async (_parent, directory) => {
    const collected = await collectComponent(directory);
    assert.deepEqual(Object.keys(collected.hashes), COMPONENT_FILES);
    for (const name of COMPONENT_FILES) assert.equal(collected.hashes[name], digest(`synthetic payload: ${name}\n`));
    assert(!collected.contents.has('README.md'));
  });
});

test('collector rejects unexpected, duplicate, empty, missing and linked inputs', async () => {
  const mutations = [
    async (directory) => writeFile(join(directory, 'game.ini'), 'synthetic forbidden payload'),
    async (directory) => mkdir(join(directory, 'nested')),
    async (directory) => writeFile(join(directory, 'Transfiguration-README.md'), 'duplicate README'),
    async (directory) => writeFile(join(directory, BRIDGE), ''),
    async (directory) => rm(join(directory, 'artifact.json')),
  ];
  for (const mutate of mutations) await syntheticDirectory(async (_parent, directory) => { await mutate(directory); await assert.rejects(() => collectComponent(directory)); });
  await syntheticDirectory(async (parent, directory) => {
    const link = join(parent, 'linked'); await symlink(directory, link, process.platform === 'win32' ? 'junction' : 'dir');
    await assert.rejects(() => collectComponent(link), /symbolic link or junction/);
    await assert.rejects(() => assertPlainPath(join(link, BRIDGE)), /symbolic link or junction/);
  });
});

test('original ZIP writer emits ten deterministic stored entries and correct central offsets', () => {
  const contents = new Map(COMPONENT_FILES.map((name) => [name, Buffer.from(`synthetic ZIP entry ${name}`)]));
  const zip = createComponentZip(contents); assert.deepEqual(createComponentZip(contents), zip);
  const end = zip.length - 22; assert.equal(zip.readUInt32LE(end), 0x06054b50);
  assert.equal(zip.readUInt16LE(end + 10), 10); const central = zip.readUInt32LE(end + 16);
  let offset = central;
  for (const name of COMPONENT_FILES) {
    assert.equal(zip.readUInt32LE(offset), 0x02014b50); assert.equal(zip.readUInt16LE(offset + 10), 0);
    const length = zip.readUInt16LE(offset + 28), local = zip.readUInt32LE(offset + 42);
    assert.equal(zip.subarray(offset + 46, offset + 46 + length).toString('ascii'), name);
    assert.equal(zip.readUInt32LE(local), 0x04034b50); assert.equal(zip.readUInt16LE(local + 8), 0);
    assert.equal(zip.readUInt32LE(local + 18), contents.get(name).length);
    assert.deepEqual(zip.subarray(local + 30 + length, local + 30 + length + contents.get(name).length), contents.get(name));
    offset += 46 + length;
  }
  assert.equal(offset, end); assert.equal(zip.readUInt32LE(end + 12), end - central);
  const missing = new Map(contents); missing.delete('artifact.json'); assert.throws(() => createComponentZip(missing));
});

test('review mode never consults signing keys and rejects invalid release inputs before I/O', async () => {
  const environment = { TRANSFIGURATION_UPDATE_REPOSITORY: 'Illyaverse/Transfiguration' };
  for (const key of ['TRANSFIGURATION_UPDATE_SIGNING_KEY_PEM', 'TRANSFIGURATION_UPDATE_SIGNING_KEY_FILE', 'TRANSFIGURATION_UPDATE_PUBLIC_KEY']) Object.defineProperty(environment, key, { get() { throw new Error('test key access is forbidden'); } });
  await assert.rejects(() => prepareComponentRelease({ ...manifestInput(), mode: 'review', bridgeSha256: bridgeHash, sourceCompatibilitySha256: digest('source'), sourceRoot: '' }, environment), /Missing release path/);
});

const linuxCompatibility = () => ({
  ...compatibility(), platform: 'linux-x64',
  verification: {
    native_metadata_and_endpoints: 'passed_static_metadata_only', http_websocket_and_redirects: 'pending_human_test',
    explicit_ui_current_session_revocation: 'pending_human_test', real_api_worker_refresh_and_login: 'pending_human_test',
    native_gui_and_login: 'pending_human_test', linux_auth_file_guard: 'passed_unit_wsl2_synthetic_files',
  },
});

test('linux-x64 manifests name their own platform and unknown platforms stay rejected', () => {
  const value = buildManifest({ ...manifestInput(), platform: 'linux-x64' });
  assert.equal(value.platform, 'linux-x64'); assert.equal(value.asset, 'Transfiguration-0.1.1-linux-x64.zip');
  assert.equal(buildManifest(manifestInput()).platform, 'windows-x64');
  for (const platform of ['linux-arm64', 'darwin-arm64', 'Linux-x64', '']) assert.throws(() => buildManifest({ ...manifestInput(), platform }));
  const identity = ephemeralIdentity();
  assert.equal(JSON.parse(Buffer.from(createSignedEnvelope(value, identity.pem, identity.raw).manifest, 'base64')).platform, 'linux-x64');
  assert.throws(() => createSignedEnvelope({ ...value, asset: 'Transfiguration-0.1.1-windows-x64.zip' }, identity.pem, identity.raw));
});

test('linux-x64 compatibility needs qualified passes and keeps real-client proofs pending', () => {
  const artifact = { ...security(), sha256: bridgeHash }, original = linuxCompatibility();
  validateProductionMetadata(artifact, original, bridgeHash);
  const withVerification = (changes) => ({ ...original, verification: { ...original.verification, ...changes } });
  for (const changed of [
    withVerification({ native_metadata_and_endpoints: 'passed' }), withVerification({ http_websocket_and_redirects: 'passed' }),
    withVerification({ native_metadata_and_endpoints: 'pending_human_test' }), withVerification({ native_gui_and_login: 'passed_unit_wsl2' }),
    withVerification({ real_api_worker_refresh_and_login: 'passed_static_metadata_only' }), withVerification({ explicit_ui_current_session_revocation: 'passed' }),
    { ...original, verification: Object.fromEntries(Object.entries(original.verification).filter(([name]) => name !== 'explicit_ui_current_session_revocation')) },
    { ...original, platform: 'windows-x64' }, { ...original, platform: 'linux-arm64' },
  ]) assert.throws(() => validateProductionMetadata(artifact, changed, bridgeHash));
  // The Windows rules are unchanged: a qualified-only verification block does not satisfy them.
  assert.throws(() => validateProductionMetadata(artifact, { ...original, platform: 'windows-x64' }, bridgeHash));
});

test('the checked-in linux-x64 manifest is valid, distinct from Windows and honest about unverified proofs', async () => {
  const root = resolve(import.meta.dirname, '..');
  const { readFile } = await import('node:fs/promises');
  const linux = readStrictJson(await readFile(join(root, 'compatibility.linux-x64.json')), 'linux compatibility');
  const windows = readStrictJson(await readFile(join(root, 'compatibility.json')), 'windows compatibility');
  assert.equal(linux.platform, 'linux-x64');
  // The validator's SECURITY_FIELDS carry a synthetic endpoint; the real product endpoint is substituted
  // so only the platform policy, hashes and verification states of the checked-in file are exercised.
  validateProductionMetadata({ ...security(), sha256: bridgeHash }, { ...linux, default_endpoint: security().default_endpoint }, bridgeHash);
  for (const name of NATIVE_ASSEMBLIES) assert.notEqual(linux.assemblies[name].sha256, windows.assemblies[name].sha256);
  for (const name of ['osu.Game.dll', 'osu.Framework.dll', 'osu!.dll']) assert.equal(linux.assemblies[name].version, windows.assemblies[name].version);
  assert.deepEqual(linux.contracts, windows.contracts);
  assert(Object.values(linux.verification).every((status) => status !== 'passed'));
  assert.equal(nativeClientVersion(linux), '2026.1005.1');
});
