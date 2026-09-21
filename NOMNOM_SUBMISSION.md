# NOMNOM submission checklist

The canonical NOMNOM registry stores one manifest per mod and discovers new artifacts from GitHub releases. This checkout is ready for the source/plugin and local-extraction portions of a submission:

- [x] One BepInEx 5 plugin: FS2Hercules.dll
- [x] Un-obfuscated source is present in the repository
- [x] Plugin version is 1.0.0 and uses a parseable release version
- [x] Release layout, installation path, and local asset generation are documented in README.md
- [x] Original source code has an explicit license
- [x] Original FreeSpace 2 VP/POF/PCX files are excluded from the repository and release payload
- [ ] Confirm and document permission/license for the converted FreeSpace 2 artwork, or obtain registry approval for a user-supplied-data build step
- [ ] Publish a GitHub release whose first asset is the complete plugin archive
- [ ] Record that release's SHA-256 hash in the NOMNOM manifest

The final registry manifest must use the repository owner's actual GitHub account and repository name. Those values are intentionally not guessed here because this checkout has no Git remote configured.

Important: if NOMNOM enforces a complete immediately-installable archive, the local extraction workflow alone does not satisfy that requirement. The remaining choices are permission for distributable assets, newly created replacement assets, or an accepted build-from-user-owned-data workflow.
