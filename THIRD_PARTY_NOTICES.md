# Third-party notices

## FreeSpace 2 Hercules artwork

The generated assets in the local assets directory are converted/reworked visual data for the FreeSpace 2 GTF Hercules. They are created by the supplied asset tool from data in a FreeSpace 2 installation supplied by the user.

This repository intentionally does not contain the original FreeSpace 2 VP, POF, or PCX files, and a release archive must not contain them. The asset tool reads the required entries in memory and writes only the runtime mesh and texture files required by FS2Hercules.

The FreeSpace 2 retail software and its incorporated assets are owned by their original rights holders. This checkout contains no permission letter or separate redistribution license for those assets. Local extraction reduces redistribution risk; it does not establish ownership or grant permission to publish derivative artwork. Do not submit or publish a release containing generated artwork until the required permission or an applicable asset license has been obtained, unless the registry explicitly accepts a user-supplied-data build step.

The FreeSpace 2 manual and software license is the source for this redistribution caution:

https://cdn.cloudflare.steamstatic.com/steam/apps/273620/manuals/MANUAL.PDF?t=1573779795

The GTF Hercules reference and design information are documented by the FreeSpace Wiki:

https://wiki.hard-light.net/index.php/GTF_Hercules

The original game is developed by Volition and published by Interplay; this project is not affiliated with either company. The aircraft name and fictional setting references are used only to identify the adaptation and do not imply endorsement.

## Asset tool and runtime dependencies

The bundled asset tool is compiled from tools/assettool/src/main.rs and uses the FreeSpace Open pof-tools crate from a local development checkout. It contains no FreeSpace 2 data. The pof-tools source and its dependencies remain subject to their own licenses.

The plugin is built against the user's locally installed Nuclear Option, BepInEx 5, Harmony, Unity, and Mirage assemblies. Those assemblies are not included in this repository's release payload and remain subject to their own terms.

## Original code

Original source code in this repository is licensed under the MIT License in LICENSE. That license does not extend to third-party material listed above.
