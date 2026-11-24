# ShemBuild

ShemBuild is a Windows application for inspecting Minecraft schematics. **Stage 0 is complete. The first implementation of stage 1, the Java `.schem` viewer MVP, builds and launches; user review is ongoing.** The parser and renderer remain experimental; see the [compatibility matrix](COMPATIBILITY.md) before relying on a format or Minecraft version.

## Prototype setup

The prototype uses C# / .NET 10, WinUI 3, and HelixToolkit.WinUI.SharpDX. Build it on Windows with the .NET 10 SDK, the Windows SDK 10.0.26100, and the WinUI application-development workload installed.

```powershell
dotnet build .\src\ShemBuild.App\ShemBuild.App.csproj -p:Platform=x64
```

Run the app from Visual Studio, or launch `src\ShemBuild.App\bin\x64\Debug\net10.0-windows10.0.26100.0\ShemBuild.App.exe`. The framework-dependent executable needs the .NET 10 Desktop Runtime.

For early testing, download the self-contained Windows x64 ZIP from [GitHub Releases](https://github.com/Latte-Night-Studio/ShemBuild/releases). Extract the full archive and run `ShemBuild.App.exe`; the package includes the .NET and Windows App SDK runtimes.

Before rendering, the app asks the user to confirm or choose a stable Minecraft release. `DataVersion` 3700 suggests 1.20.4 because that is the version associated with the current sample files; other values do not have a verified mapping, so the latest stable release is only a suggestion. If no archive is selected, the app downloads the official client JAR for the confirmed release, verifies its SHA-1 and size against Mojang metadata, and caches it under `%LOCALAPPDATA%\ShemBuild\assets`. Offline use can reuse a cached JAR for the chosen release. A manual **Choose resource archive…** override remains available for a client `.jar` or full resource-pack `.zip`; the app cannot confirm that a manually selected archive matches the chosen release.

## Prototype scope and limits

- The picker reads GZip-compressed Sponge Java Edition `.schem` input and reports legacy `.schematic`, `.nbt`, Bedrock `.mcstructure`, malformed data, and size-limit cases as unsupported or invalid. The experimental reader handles structures labelled v2 or v3; this is not a compatibility claim.
- The app shows dimensions, local origin, Sponge paste offset, block-state counts, format version, and the raw `DataVersion`. A version confirmation dialog is shown before using version-dependent resources.
- The side panel lists non-air block states and counts. Use Ctrl+O to open a file and F to fit the full structure in view.
- The renderer reads blockstate variants/multipart rules, model parents/elements, face UVs, rotations, and PNG textures from the selected archive. Missing models or textures appear magenta and are counted in the status line.
- Rendering is still experimental: weighted model alternatives always choose the first entry; `uvlock`, animation frames, biome tinting, block entities, and some special models are not implemented. Transparent PNGs are sent through the renderer's transparent path, but exact Minecraft cutout/blend layers are not yet matched.
- Automatically downloaded assets come from Mojang's official version manifest and stay in the local app cache; the app does not bundle them with the project. The renderer does not validate overall compatibility between a resource version and a schematic's `DataVersion`, or claim compatibility with a Minecraft release.
- Input is read-only. The prototype never saves to or modifies the selected file.
- Prototype limits: compressed file 64 MB, expanded NBT 128 MB, 20,000,000 block positions, 65,536 palette entries, and an emergency ceiling of 2,000,000 exposed faces. Above 750,000 faces the app warns that rendering may be slow or unstable, then continues.
- The viewport displays the live frame rate. The status line records elapsed import/mesh-build time and approximate managed bytes allocated for that operation.

## Stage 0 evaluation and result

The workspace includes 11 example `.schem` files under `schematic/`; their filenames reference Java 1.20.4. All 11 passed a one-off C# reader run. The files contain GZip NBT Sponge v3, `DataVersion` 3700, and volumes from 312,832 to 16,121,388 positions. This Stage 0 reader probe predates the Stage 1 viewer builds and is sample evidence only; see [COMPATIBILITY.md](COMPATIBILITY.md).

In a single C# reader probe, those files took 20–401 ms to parse and allocated approximately 3.5–111.7 MB. `Acropolis` (16,121,388 positions; 3,118,751 non-air blocks) took 401 ms and allocated 111.7 MB. `PantheonTemple` (402,696 positions; 96,514 non-air blocks) opened in the app: reader plus background scene construction took 116 ms, UI mesh preparation took 109 ms, and the background phase allocated about 32.1 MB for 80,366 exposed faces. The viewport rendered the model and reported about 144 FPS during camera movement. These are single-run measurements on this machine.

The voxel preflight found 59,926–1,423,600 exposed faces. The app warns above 750,000 faces and continues rendering, up to an emergency ceiling of 2,000,000 faces. The user confirmed that textures display and, after the camera and far-plane correction, the large `PantheonINSCRIPTION` model is fully visible with the warning. Its status line reported 1,423,600 exposed faces, 5,858 ms scene construction, 889 ms UI geometry preparation, and about 1,704.5 MB allocated. Stage 0 is complete. The emergency ceiling has not been exercised on a sample. Do not add format/version combinations to a compatibility matrix until representative files have been checked.

Stage 1 has since been built with .NET SDK 10.0.404 and launched. The self-contained alpha package opens a repository sample. User review of the viewer is still ongoing, so stage 1 is not marked complete.

The `0.1.0-alpha.2` Release scene builder was compared with alpha.1 using the same two sample files and cached Minecraft 1.20.4 resources. Allocations fell from 236.7 to 129.7 MB for `PantheonTemple` (187,303 exposed faces) and from 1,595.1 to 828.0 MB for `PantheonINSCRIPTION` (1,423,600 exposed faces). The builder took 638 ms and 2,388 ms respectively; these are one-off builder-only measurements that exclude UI geometry and do not establish Minecraft-version compatibility. `PantheonTemple` still reports missing models or textures for 2,421 blocks.
