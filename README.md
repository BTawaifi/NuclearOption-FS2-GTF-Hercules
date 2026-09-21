# FS2 Hercules

FS2 Hercules is a BepInEx 5 plugin for Nuclear Option that adds an atmospheric-refit GTF Hercules heavy-assault aircraft. The plugin registers the aircraft through Harmony patches, reuses the stock fixed-wing aircraft systems for networking and damage, and loads generated visual assets from the adjacent assets directory.

The repository and release payload do not contain the original FreeSpace 2 VP, POF, or PCX files. Generate the four runtime assets from a FreeSpace 2 installation that you own before running the mod.

## Compatibility

- Nuclear Option: 0.34.2
- Loader: BepInEx 5 (Mono, Windows x64)
- Plugin assembly: FS2Hercules.dll
- Plugin version: 1.0.0

## Generate the visual assets

Requirements:

- Python 3
- numpy, installed with python -m pip install -r tools/requirements.txt
- Rust/Cargo
- A local checkout of the FreeSpace Open pof-tools repository, or an already-built pof2glb converter
- A user-owned FreeSpace 2 installation containing its VP archives

From the repository root, run:

    python tools/extract_hercules.py --freespace "D:\Games\Freespace 2" --pof-tools "D:\Games\FreespaceOpen\tools\pof-tools-src"

The default output is the repository's assets directory. To use a different tools checkout, pass its path with --pof-tools. To use an existing converter instead of Cargo, pass --converter PATH.

The script reads data/models/fighter06.pof and data/maps/fighter06-01a.pcx from the VP archives, converts only the fighter06a hull, and writes:

    assets/
        HercPBR-glow.png
        HercPBR-normal.png
        HercPBR.png
        hercules.nomesh

The extracted VP contents are temporary inputs and are not copied to the output.

## Build

The project targets net472 and uses the pinned SDK in global.json. Set the GameDir property in FS2Hercules.csproj to the local Nuclear Option installation, then run:

    dotnet build -c Release

The installable payload is bin/Release/net472/FS2Hercules.dll plus the generated assets directory. Game assemblies and BepInEx/Harmony references are compile-time/runtime dependencies and are not redistributed by this project.

## Install

1. Generate assets as described above.
2. Copy FS2Hercules.dll and the generated assets directory into Nuclear Option/BepInEx/plugins/FS2Hercules/.

The plugin does not download files, modify the game installation, edit the registry, start processes, or delete user data. If generated assets are missing, it logs the extraction command and leaves the base game unchanged.

## NOMNOM release requirements

This repository is structured as one mod with an un-obfuscated source tree and a BepInEx 5 plugin. A NOMNOM release should:

1. Use a parseable tag such as 1.0.0, matching the assembly version.
2. Upload one archive containing the complete FS2Hercules plugin folder as the first release asset.
3. Keep original FreeSpace 2 VP/POF/PCX data out of the repository and release archive.
4. Add the release URL and SHA-256 hash to the mod manifest in the NOMNOM registry.
5. Include the asset-permission evidence described in THIRD_PARTY_NOTICES.md before requesting registry approval.

The local-extraction design avoids redistributing the original game data, but an archive that requires the user to run the extractor is not a complete drop-in package. If NOMNOM requires every release to be immediately installable, this project still needs either permission for a distributable asset package or replacement assets with a compatible license.

The source code is intentionally shipped in this repository so a release DLL can be compared with its source, as required by NOMNOM's open-source policy.
