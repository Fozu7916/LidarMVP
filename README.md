# DroneLiDAR

Processes 4+ million points in 3 seconds (scales linearly).

A practical local tool for comparing point cloud epochs and monitoring product volume changes. The program accepts LAS and LAZ files, verifies source data compatibility, separates machinery from the measured surface, calculates cut/fill volumes, and generates a reproducible results package.

Practical value: reduced manual processing of repeat surveys, a standardized workflow for epoch comparison, visual verification of data cleaning, contractor volume monitoring, and an audit trail for the provenance of every result. Realistic scope of application: without field validation, metrological review, and methodology approval, the result does not automatically hold legally binding status.

## Workflow

1. The operator creates a project or selects its directory.
2. Source LAS/LAZ files are placed in `input/`; the first file (based on natural sort order) is treated as the baseline (the point where excavated volume = 0).
3. The operator specifies the density, swell factor, and the validated AOI (Area of ​​Interest).
4. The program validates the LAS/LAZ files and CRS, performs the calculation, and creates a new, immutable run in `results/<RunId>/`.
5. In the viewer, the operator compares RAW vs. CLEAN data and inspects machinery, buildings, surface cover, and the AOI.
6. `report.html`, CSV files, and `manifest.json` are delivered to the reviewer as a single package.

Each recalculation receives a unique Run ID. Previous results are neither deleted nor mixed with the new run.

## Features

- LAS/LAZ 1.2–1.4, point formats 0–10;
- local origin to preserve `float` precision with large geodetic coordinates;
- WKT/CRS consistency check between epochs (if WKT is present in the LAS file); - classification and exclusion of machinery, vegetation, and noise;
- separate "Pre-filtering" and "Post-filtering" slots for each scan;
- robust DEM generation based on the upper quantile of observations;
- cut, fill, balance, area, depth, volume (with bulking factor), and mass;
- constrained ICP using only stable ground outside the AOI;
- DEM difference RMS in the stable zone and coverage percentage;
- HTML report, cell CSV, 3D viewer, and `manifest.json` with SHA-256;
- synthetic scenes: quarry, mixed stockpile, and excavation pit;
- built-in `--selftest` regression.

## What is calculated

For each valid DEM cell with area `A`:

```text
Δh = Zbase − Zcurrent

Δh > threshold   → Vcut  += Δh · A
Δh < −threshold  → Vfill += |Δh| · A

Vnet    = Vcut − Vfill
Vbulked = Vcut · Kr
Mass    = Vcut · density
```

Change threshold:

```text
threshold = max(0.03 m, min(0.10 m, 0.15 · DEM_cell_size))
```

Density is specified in `t/m³`, and mass is calculated in tonnes. The bulking factor `Kr` applies only to the cut volume. It does not replace laboratory determination of material properties.

## Pipeline

1. Validation of LAS/LAZ header: version, point format, record length, point count, scale/offset, and bounds.
2. Reading WKT from projection VLR/EVLR. Incompatible WKTs from different epochs halt the calculation.
3. Coordinate transformation to a local system relative to the origin of the first scan. 4. For very large LAS/LAZ files: deterministic spatial sampling based on XYZ hashing. This process is independent of the strip order within the file.
5. Classification that preserves reliable input LAS classes.
6. A single voxel-based sorting pass simultaneously generates three independent point clouds: RAW, CLEAN, and a calculated DEM. Ground points are not removed along with machinery/equipment from the same voxel.
7. ICP registration is performed using only ground points outside the AOI, without substituting the area undergoing change. The solution is rejected if there are fewer than 300 stable points, or in cases of insufficient overlap, high residuals, or excessive shift/rotation; the report records the fallback to direct RTK georeferencing.
8. DEM: the 85th percentile of valid points is used for each cell. Small, isolated gaps are filled only if at least five measured neighbors are present.
9. Volume calculation is performed only where valid coverage exists for both epochs.
10. The DEM is constrained to the AOI plus a 10-meter buffer zone of stable terrain. If the selected grid spacing results in more than 25 million cells, the spacing is automatically and explicitly increased based on the point density of both epochs and available memory; the actual spacing is recorded in `meta.json`.
11. Export of raw/clean data, metadata, CSV, report, and manifest.

## Actual input data size

- LAS files are read in 4 MB streaming blocks. LAZ files are decompressed sequentially using a built-in managed decoder, without creating temporary LAS copies or relying on external software.
- While a file may contain tens or hundreds of millions of records, no more than **8 million points** are processed for a single epoch.
- The actual limit is automatically adjusted (within the 1–8 million range) based on available process memory and recorded in `manifest.json`. - If the limit is exceeded, deterministic XYZ spatial sampling is applied. Reading time remains linear relative to the total number of records, as the entire file must be scanned.
- Processing memory is limited by the sample size, a classification grid of up to 8 million cells, and a DEM of up to 25 million cells.
- The viewer has two modes: "Quick View" loads up to 480,000 points from the selected RAW/CLEAN slot, while "All Points" reads the full BIN file and displays 100% of the points in that slot. On the screen...
