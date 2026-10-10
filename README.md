<h1 align="center">Transfiguration</h1>

<p align="center">
  <strong>A managed authentication and API bridge for supported stock osu!lazer clients.</strong>
</p>

<p align="center">
  <a href="Transfiguration.csproj"><img src="https://img.shields.io/badge/C%23-.NET_10-512BD4?style=flat-square" alt="C# and .NET 10"></a>
  <a href="compatibility.json"><img src="https://img.shields.io/badge/Platform-Windows_x64-0078D4?style=flat-square" alt="Windows x64"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-lightgrey?style=flat-square" alt="MIT License"></a>
</p>

Transfiguration connects an existing official lazer installation to
[Illyaverse](https://github.com/Illyavrs/Illyaverse) without rebuilding the game.
Its implementation and guards are original.

## Launch through Kaleidoscope

[Kaleidoscope](https://github.com/Illyavrs/Kaleidoscope) loads the bridge through its
native host before game startup, installs the DLL temporarily in `rulesets/`, and
removes its owned plugin after exit. It adds no playable ruleset. Manual DLL
installation does not provide the required startup guarantees.

- **Fail closed:** unknown versions, mismatched hashes or failed required patches
  abort startup. HTTP/WebSocket guards fix one private HTTPS origin per launch,
  refuse official fallback and foreign destinations, and retain TLS verification.
- **Isolate accounts:** private credentials use `auth.illyaverse.json`, partitioned
  by server origin. Shared `game.ini` preserves official account lines and gameplay
  preferences.
- **Reuse the library:** existing `client.realm` and `files/` need no copy or
  reimport. Use one game session per data directory; concurrent external
  configuration replacement is unsupported and in-game data migration is disabled.

Selected reads support Normal/Relax context. Explicit logout requests current-session
revocation; automatic failures and disconnects do not. See
[connection setup](https://github.com/Illyavrs/Illyaverse/blob/main/docs/connection.md)
and the [protocol contract](https://github.com/Illyavrs/Illyaverse/blob/main/docs/protocol-compatibility.md).

## Compatibility and verification

[`compatibility.json`](compatibility.json) pins exact assembly hashes and method contracts.

| Dependency | Pinned version |
| --- | --- |
| Stock lazer (`osu.Game.dll` / `osu!.dll`) | `2026.1005.1.0` |
| .NET transport runtime | `10.0.12` |
| Ruleset API | `2026.818.0` |
| Harmony | `2.4.2` |

A lazer update can require a new bridge. Managed contract/policy proofs do not
establish native GUI, real login/refresh/gameplay/logout or shared Realm-schema
compatibility. See the [current integration record](https://github.com/Illyavrs/Illyaverse/blob/main/docs/current-integration-20261010.md)
for evidence and deployment boundaries.

## Build from the parent checkout

Use the complete Illyaverse checkout, PowerShell, .NET 10 SDK and an existing
Windows x64 lazer installation matching the manifest. From the parent directory:

```powershell
pwsh -File tools/transfiguration/build.ps1 -NativeRoot "<compatible-lazer-directory>"
```

The script checks hashes and writes the production DLL, metadata and notices to
`tools/transfiguration/artifacts/` without modifying the installation. Exclude
stock assemblies and proof fixtures from distribution.

## Independent component updates

The signed DLL channel uses
[Illyavrs/Transfiguration](https://github.com/Illyavrs/Transfiguration), independently
of [Kaleidoscope](https://github.com/Illyavrs/Kaleidoscope) releases. Its manifest
binds the DLL, ZIP contents, source commit, native compatibility and launcher requirement.

[`release.example.env`](release.example.env) documents external channel settings.
The [workflow](.github/workflows/release.yml) defaults to unsigned review; signing
requires verified production input and an external Ed25519 key. GitHub publication,
public releases and signing-key configuration remain pending. The launcher test
package is unsigned.

## License and references

Licensed under [MIT](LICENSE), with [third-party notices](THIRD-PARTY-NOTICES.md).
Research references include pinned [osu! source](https://github.com/ppy/osu/tree/6cbcae7fbb764839a66c1adf564c2e1679242072)
and [osu-framework configuration](https://github.com/ppy/osu-framework/blob/ab102608f363f24e0e33ec629a9bf613e72c9950/osu.Framework/Configuration/IniConfigManager.cs).
