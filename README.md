# FS2 Hercules

FS2 Hercules is a BepInEx 5 plugin for Nuclear Option that adds an atmospheric-refit GTF Hercules heavy-assault aircraft. The plugin registers the aircraft through Harmony patches, reuses the stock fixed-wing aircraft systems for networking and damage, and loads visual assets from the adjacent assets directory.

The repository and release payload do not contain the original FreeSpace 2 VP, POF, or PCX files. The release includes a small self-contained extractor that reads those files from a FreeSpace 2 installation supplied by the player and writes only the four runtime files the mod needs.

## Compatibility

- Nuclear Option: 0.34.2
- Loader: BepInEx 5 (Mono, Windows x64)
- Plugin assembly: FS2Hercules.dll
- Plugin version: 1.0.0

## Install for players

[NOMM](https://github.com/Combat787/NOMM) is the easiest way to install BepInEx and manage Nuclear Option mods. For a manual install, use [BepInEx 5 Mono x64](https://github.com/BepInEx/BepInEx/releases):

1. Install BepInEx 5 Mono x64 into Nuclear Option and launch the game once.
2. Extract the FS2Hercules folder into Nuclear Option/BepInEx/plugins/.
3. Double-click Extract-FS2Hercules.cmd inside the FS2Hercules folder.
4. Select the folder containing the original FreeSpace 2 installation when prompted.
5. Start Nuclear Option.

The release launcher uses Windows PowerShell, which is already included with supported Windows installations. Players do not need the .NET SDK, Python, NumPy, Rust, Cargo, or a FreeSpace Open development checkout.

The launcher auto-detects common Steam and GOG locations. If it cannot find one, it opens a folder picker. It only creates or replaces files in the mod's assets directory; it does not modify the FreeSpace 2 or Nuclear Option installation.

Expected installed layout:

    BepInEx/plugins/FS2Hercules/
        FS2Hercules.dll
        Extract-FS2Hercules.cmd
        Extract-FS2Hercules.ps1
        tools/FS2Hercules.AssetTool.exe
        assets/
            HercPBR-glow.png
            HercPBR-normal.png
            HercPBR.png
            hercules.nomesh

## External assets and hot swapping

The plugin deliberately loads the mesh and textures from the external assets directory beside the DLL. After extraction, a player or creator can replace:

- hercules.nomesh for the mesh
- HercPBR.png for the albedo
- HercPBR-normal.png for the normal map
- HercPBR-glow.png for the emission map

The mesh must contain a material named HercPBR. Restart Nuclear Option after changing files because the visual is loaded when the aircraft encyclopedia is built. The asset tool only regenerates the default assets; it does not overwrite the original FreeSpace 2 files.

## Build from source

The plugin targets net472 and uses the pinned SDK in global.json. Set the GameDir property in FS2Hercules.csproj to the local Nuclear Option installation, then run:

    dotnet build -c Release

To build the self-contained asset tool, install Rust/Cargo and obtain a local FreeSpace Open pof-tools checkout. Set the pof dependency path in tools/assettool/Cargo.toml if it is not at the documented development path, then run:

    cargo build --release --manifest-path tools/assettool/Cargo.toml

Copy the resulting target/release/fs2hercules-assettool.exe to tools/FS2Hercules.AssetTool.exe when preparing a release archive. The older Python extractor remains in tools/extract_hercules.py as a developer fallback, but is not required by the player workflow.

## NOMNOM release requirements

This repository is structured as one mod with an un-obfuscated source tree, a BepInEx 5 plugin, and source for the bundled asset executable. A release archive should contain the complete FS2Hercules plugin folder, including the launcher and asset tool, as its first GitHub release asset.

The local-extraction design avoids redistributing the original game data, but an archive that requires the player to run the extractor is not a complete immediately-installable package under NOMNOM's strict wording. The remaining approval choices are permission for a distributable asset package, newly created replacement assets, or explicit acceptance of a build-from-user-owned-data workflow.

See NOMNOM_SUBMISSION.md and THIRD_PARTY_NOTICES.md for the remaining submission evidence.
