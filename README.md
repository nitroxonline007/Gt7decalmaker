# GT7 Decal Maker — PNG to SVG converter (Windows)

Native Windows (WPF, .NET 8) app that converts PNG images into **color-retaining vector SVG decals** sized to fit **Gran Turismo 7's 15 KB decal import limit**, keeping as much quality as possible.

![pipeline](https://img.shields.io/badge/output-%E2%89%A415KB_SVG-blue)

## Gran Turismo 7 decal import rules

| Rule | Value |
|---|---|
| Format | SVG only |
| Max file size | **15 KB (~15,000 bytes)** — hard limit enforced by the uploader |
| Content | Pure vector `<path>` fills; no embedded rasters; text must be outlined |
| Transparency | Supported (transparent background recommended for decals) |
| Import method | Copy SVGs to a USB stick (FAT32) → in GT7: **Livery Editor → My Decals → Import Decal** (up to ~100 files) |

## How it works

1. **Decode + downscale** PNG to the working resolution (default 384 px max side).
2. **Flat background removal** — detects a baked-in white/gray border background and clears it (skipped if the PNG already uses transparency).
3. **Median-cut + k-means color quantization** with perceptual palette merging — centroids converge to true color peaks, so hues land accurately.
4. **Despeckle** removes tiny noise regions; removed specks are absorbed by the surrounding color (no holes).
5. **Stacked contour tracing**: colors are painted largest-first and each layer also covers the area under the layers above it, so lower layers need almost no holes or shared edges — far fewer bytes for the same picture.
6. **Douglas-Peucker simplification** smooths pixel staircases into compact paths; small shapes get proportionally gentler smoothing.
7. **Compact SVG encoding**: relative delta-encoded integer coordinates, `h`/`v` for straight edges, `#rgb` short colors, one `<path>` per color.
8. **Quality-scored auto-fit**: tries hundreds of resolution × color count × despeckle combinations, finds the sharpest edge setting that fits each under the limit, renders every candidate and keeps the one closest to the original. The score weights sharp edges more and penalizes wrong tints, so silver stays silver instead of picking up a neighbouring accent color.
9. **Sharp lettering (detail layer)**: groups of crisp letters on a flat backing (badges, "K20", "i-VTEC") are re-traced at the photo's full resolution and drawn on top of the base trace, so text stays readable inside the 15 KB budget.

## Usage

```
GT7DecalMaker.exe
```

1. Drag & drop a PNG (or *Open PNG...*). Transparent-background PNGs work best.
2. Adjust sliders if needed:
   - **Working resolution** — higher = more detail = bigger file
   - **Max colors** — palette ceiling
   - **Detail level** — edge smoothing vs fidelity
   - **Despeckle** — removes specks smaller than N px²
   - **Keep lettering sharp** — spends part of the budget on readable text/logos (slightly coarser elsewhere); untick for maximum overall detail
3. Press **Convert**. The badge shows final size: green = under limit.
4. **Save SVG...** and copy the file to your USB stick.

### Tips for hitting 15 KB with maximum quality
- Leave **Auto-fit** on; it finds the best settings automatically.
- If results look too soft, raise *Working resolution* and let auto-fit trade colors instead of detail.
- Photos/gradients don't vectorize well — decals with flat/solid color regions convert beautifully.

## Build & publish

Requires .NET 8 SDK (Windows).

```powershell
# run from source
dotnet run --project GT7DecalMaker -c Release

# framework-dependent single exe (needs .NET 8 Desktop Runtime installed)
dotnet publish GT7DecalMaker -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -o dist

# fully self-contained single exe (no runtime needed, ~150 MB)
dotnet publish GT7DecalMaker -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist-sc
```

## Tests

Engine verification suite (43 checks): topology/holes, checkerboard stress, transparency handling,
hue-integrity scoring (independent of the quantizer's own classifier), soft-edge color retention,
hue-guarded palette reduction, background removal, resolution climbing, and auto-fit convergence:

```powershell
dotnet run --project GT7DecalMaker.Tests -c Release
```

Latest run: **43 passed / 0 failed** — interior fidelity 98.1%, hue families preserved under forced palette cuts.

## Project layout

```
GT7DecalMaker/
├── Convert/
│   ├── Quantizer.cs      median-cut + palette merge + assignment
│   ├── Tracer.cs         despeckle + boundary loop tracing
│   ├── Simplifier.cs     Douglas-Peucker
│   ├── SvgWriter.cs      compact path encoding, byte counting
│   └── ConvertEngine.cs  pipeline + auto-fit ladder
├── MainWindow.xaml(.cs)  WPF UI, live vector preview, save
GT7DecalMaker.Tests/      engine verification harness
dist/GT7DecalMaker.exe    ready-to-run build
```
