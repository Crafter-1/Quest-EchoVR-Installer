# EchoVR Quest Installer

## What is it?
This is a fully 3D Quest installer for EchoVR built in Unity with C# as the main programming language.
Compared to the other installers, which have just been a plain app. This installer uses the main part of VR for style!

The installer is fairly simple to use, with instructions for parts that need user input and the UI follows the style of the EchoVR menu.

Of course, main part of this installer is the ability to install the game Echo VR which is built on community servers now after Meta shut it down.
For full instructions on how to install this on your quest, please read https://quest.echovr.de 

## Notes
For this you need to be in the Echo VR lounge discord. (https://discord.gg/X5Bx4fDdBG)
This is built for the Meta Quest headsets, this will not work on a regular android device.

## Signed release manifest (verifier core)

The installer is moving to install the signed Quest APK named by the nevr-runtime release
manifest (`https://evr.echo.taxi/nevr-runtime/manifest.json` plus `manifest.json.sig`). This
part holds the checks that decide whether a manifest may be used at all. It is in
`Assets/Scripts/NevrManifest/` and has no Unity dependency, so `Tests~/NevrManifest.Tests`
runs it with plain `dotnet test`.

- The minisign signature (Ed25519 over BLAKE2b-512, hashed mode only) is checked against
  three pinned keys (K1, K2, KR) before the JSON is parsed.
- A key listed in `revoked_keys` is refused from then on, even if pinned.
- A manifest whose `manifest_version` is lower than the highest seen, or than the compiled-in
  `NevrPinnedKeys.MinManifestVersion`, is refused. **Raise `MinManifestVersion` for every
  installer release** to the manifest version that is current at build time.
- An expired manifest, a missing clock, or a clock that disagrees with the server by more
  than a day is refused.
- The verified manifest, its signature and the highest version are stored in one file by
  writing a temporary file and renaming it over the old one.

Dependencies, pinned: `BouncyCastle.Cryptography` 2.7.0 (netstandard2.0 DLL in
`Assets/Plugins/BouncyCastle/`, from the NuGet package with SHA-256
`f091ffccab4d03993e660bace277659a79dee0972f54d7f1f4bd46d680966241`) and
`com.unity.nuget.newtonsoft-json` 3.2.1. `Assets/link.xml` keeps the types they use from being
stripped. IL2CPP on a headset is untested.

Run the tests: `dotnet test Tests~/NevrManifest.Tests`.

## Installing and updating Echo VR (signed APK)

Both install buttons install the Quest APK that the signed release manifest names for the
`beta` channel (`NevrReleaseManager.channel`). The installer checks the manifest (see above),
downloads the APK from `https://evr.echo.taxi/nevr-runtime/<path>` stopping at the manifest's
`size`, checks the SHA-256 and size, then asks Android for the file's package name,
versionCode and signer certificate and compares them with the manifest before it hands the
file to Android's installer. The manifest must name `signer_cert_sha256`; an APK entry without
it is refused.

Before the install intent the APK is hashed and read again (it sits on external storage while
the game data downloads), and after Android's installer returns the installed package's signer
certificate must equal `signer_cert_sha256`; a mismatch stops the setup with a plain message.
Nothing, including the game-data download, starts until the manifest has been accepted. No
request follows a redirect, and the APK download stops after 30 s without data or 60 min in all.

**Game data zip (signed).** The game data comes from the manifest's `game-data` artifact
(`platform` `android-arm64`) for the same release as the APK, not from a file name on a server
list. The installer downloads `path` relative to `https://evr.echo.taxi/nevr-runtime/` (the only
accepted shape is `data/echovr-data-<12 hex>.zip`; anything with `..`, a scheme, a host, a query
or another folder refuses the whole manifest). The download stops at the manifest's `size` and
must end at exactly that size; its SHA-256 must equal `sha256`. The zip is written as `<name>.part`
and only renamed to its final name after both checks pass, so a mismatch deletes the partial file
and extracts nothing. If a `.part` file is left (the app was closed, the network dropped), the next
run asks for the rest with a `Range` header and accepts only a `206` for exactly that range
(a server that answers `200` makes it start over); the whole file is then hashed from disk before
it counts. Only the zip the accepted manifest names is extracted. The unsigned `_data.zip` that
earlier installers saved is removed. If the release has no `game-data` artifact the download does not
start and the screen says so; there is no fallback to another server or file name.

**Game data update list stays unsigned.** The asset sync from the files.echovr.de update list
(run after the zip is extracted) is still not covered by a signature or hashes in the signed
manifest. Until it is, the update list is held to: the pinned address
`https://files.echovr.de/updates/quest/update.manifest` (its own Base URL is ignored for downloads
and must be that host), https only, no redirects, at most 256 KB for the list, 1000 entries,
128 MB per file and 512 MB in all, writes and deletes only under `asset_patches/` and `files/` in
Echo's media folder, no hidden files, no `.so`, `.dex`, `.apk`, `.jar`, `.dll` or other program
files, and the existing path-traversal guard.

Android reads the APK's signer on API 28+. This code has not run on a headset yet.
