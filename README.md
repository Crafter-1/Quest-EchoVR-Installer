# EchoVR Quest Installer

## What is it?
This is a fully 3D Quest installer for EchoVR built in Unity with C# as the main programming language.
Compared to the other installers, which have just been a plain app. This installer uses the main part of VR for style!

The installer is fairly simple to use, with instructions for parts that need user input and the UI follows the style of the EchoVR menu.

Of course, main part of this installer is the ability to install the game Echo VR which is built on community servers now after Meta shut it down.
For full instructions on how to install this on your quest, please read https://quest.echovr.de 

## Notes
For this you need to be in the Echo VR lounge discord. (https://discord.gg/X5Bx4fDdBG)
For the patched method you need to be in the patcher server. (https://discord.gg/Rs8CQvjDv)
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
