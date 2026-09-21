# Nuclear Option: FS2 GTF Hercules

BepInEx 5 plugin for Nuclear Option that adds the FreeSpace 2 GTF Hercules as an atmospheric-refit heavy-assault aircraft.

The release does not contain FreeSpace 2 VP, POF, or PCX data. It includes a self-contained extractor that reads those files from a FreeSpace 2 installation supplied by the player and writes the four runtime assets locally.

## Install

1. Install BepInEx 5 Mono x64 and launch Nuclear Option once. [NOMM](https://github.com/Combat787/NOMM) is the easiest route.
2. Extract the `NuclearOption-FS2-GTF-Hercules` folder into `Nuclear Option/BepInEx/plugins/`.
3. Double-click Extract-FS2Hercules.cmd, or double-click tools/FS2Hercules.AssetTool.exe.
4. Select the original FreeSpace 2 folder.
5. Start Nuclear Option.

The launcher uses Windows PowerShell, already included with supported Windows. Players do not need the .NET SDK, Python, NumPy, Rust, Cargo, or pof-tools.

The release folder and archive use the full `NuclearOption-FS2-GTF-Hercules` name. The plugin DLL keeps its stable `FS2Hercules.dll` filename for compatibility.

## Assets

Generated files live beside the DLL in assets:

    assets/
        HercPBR-glow.png
        HercPBR-normal.png
        HercPBR.png
        hercules.nomesh

The mesh must contain a material named HercPBR. Replace these files to hot-swap the model or textures, then restart Nuclear Option. The extractor only writes the mod's assets folder and never modifies either game installation.

## Build and package

Set GameDir to the Nuclear Option install folder:

    dotnet build -c Release -p:GameDir="C:\path\to\Nuclear Option"

The asset tool is built from tools/assettool/src/main.rs and requires Rust/Cargo plus a local FreeSpace Open pof-tools checkout. Set the pof dependency path in tools/assettool/Cargo.toml if needed.

Create the deployment archive with:

    powershell -ExecutionPolicy Bypass -File tools/package-release.ps1 -GameDir "C:\path\to\Nuclear Option"

The archive is written to `artifacts/NuclearOption-FS2-GTF-Hercules-<version>.zip` and contains the DLL, launcher, asset tool, license, notices, and README. It intentionally contains no FreeSpace 2-derived assets.

## Submission status

The source, BepInEx 5 plugin, external-asset workflow, and deployment archive are ready. The only unresolved approval issue is whether NOMNOM accepts a user-owned-data extraction step instead of a complete archive containing the generated visual assets; otherwise the project needs permission for distributable assets or replacement assets. Keep the permission evidence in THIRD_PARTY_NOTICES.md.
