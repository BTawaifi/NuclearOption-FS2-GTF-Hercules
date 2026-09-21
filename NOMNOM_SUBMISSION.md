# NOMNOM submission checklist

The canonical NOMNOM registry stores one manifest per mod and discovers new artifacts from GitHub releases. This checkout is ready for the source, plugin, and no-extra-install extraction workflow:

- [x] One BepInEx 5 plugin: FS2Hercules.dll
- [x] Un-obfuscated plugin source is present in the repository
- [x] Source for the bundled asset executable is present in the repository
- [x] Plugin version is 1.0.0 and uses a parseable release version
- [x] Release layout, installation path, asset generation, and hot swapping are documented in README.md
- [x] Original FreeSpace 2 VP/POF/PCX files are excluded from the repository and release payload
- [x] Player workflow needs no .NET SDK, Python, NumPy, Rust, Cargo, or pof-tools checkout
- [ ] Confirm and document permission/license for the converted FreeSpace 2 artwork, or obtain registry approval for a user-supplied-data build step
- [ ] Build a release archive containing FS2Hercules.dll, the launcher, and the self-contained asset tool
- [ ] Publish a GitHub release whose first asset is that complete plugin archive
- [ ] Record that release's SHA-256 hash in the NOMNOM manifest

The final registry manifest must use the repository owner's actual GitHub account and repository name. Those values are intentionally not guessed here because this checkout has no Git remote configured.

Important: if NOMNOM enforces a complete immediately-installable archive, the local extraction workflow alone does not satisfy that requirement. The remaining choices are permission for distributable assets, newly created replacement assets, or explicit acceptance of a build-from-user-owned-data workflow.
