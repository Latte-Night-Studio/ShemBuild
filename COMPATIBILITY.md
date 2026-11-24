# Compatibility matrix

This matrix separates observed sample data from verified support. A parser accepting a file or the renderer producing an image does not by itself establish compatibility with a Minecraft release.

| Edition / format | Schematic format | DataVersion / game release | Evidence | Status |
|---|---|---|---|---|
| Java / Sponge `.schem` | v3 | DataVersion 3700; samples are associated with 1.20.4 | The reader processed all 11 repository samples. The user confirmed textured rendering and full camera framing for a large sample during prototype work. Alpha.2 reported 2,421 missing blocks in `PantheonTemple`; the alpha.3 scene builder treats 2,397 vanilla light blocks as invisible and leaves 24 special-model blocks unresolved. Alpha.4 opened the same sample and correctly classified 87 opaque indexed textures and 17 transparent textures from the official 1.20.4 client archive. Alpha.5 adds deterministic weighted model choices; this and the synthetic weight test do not verify compatibility. | Sample evidence only; compatibility not verified. |
| Java / Sponge `.schem` | v2 | Not identified | A v2 parsing path exists, but no representative v2 file has been recorded in the current sample set. | Unverified. |
| Java / Sponge `.schem` | v2 or v3 | Java Edition 26.3 (current MVP target as of 2026-11-23) | No representative schematic for this release has been checked in the app. | Unverified; do not claim support yet. |
| Other Java `DataVersion` values | v2 or v3 | User-selected release | The UI asks the user to choose a release; the current app has no verified mapping for these values. | Unverified; manual choice does not establish compatibility. |
| Bedrock `.mcstructure`, legacy `.schematic`, Java structure `.nbt` | — | — | Not implemented in this MVP. | Unsupported. |

The current release target is based on Mojang's [Java Edition 26.3 release page](https://www.minecraft.net/en-us/article/minecraft-java-edition-26-3). Add a verified entry only after opening representative files with the matching resource version and documenting known visual or data limitations.
