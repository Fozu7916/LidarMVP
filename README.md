# DroneLiDAR

Processes 4+ million points in 3 seconds (scales linearly).

A working local tool for comparing point-cloud epochs and monitoring changes in product volumes. The program accepts LAS and LAZ, checks source-data compatibility, separates equipment from the measured surface, calculates cut/fill volumes, and generates a reproducible result package.

Practical value: reducing manual processing of repeated surveys, establishing a consistent procedure for comparing epochs, visually verifying filtering, controlling contractor-reported volumes, and auditing the provenance of every result. The applicability boundary is explicit: until field validation, metrological assessment, and approval of the methodology, the result does not automatically have legally significant status.

## Workflow

1. The operator creates a project or selects its directory.
2. Source LAS/LAZ files are placed in `input/`; the first file in natural sort order is considered the baseline (the point at which the excavated volume = 0).
3. The operator specifies density, swell factor, and a verified AOI.
4. The program validates LAS/LAZ and CRS, performs the calculation, and creates a new immutable run in `results/<RunId>/`.
5. In the viewer, the operator compares RAW/CLEAN and checks equipment, buildings, coverage, and AOI.
6. `report.html`, CSV, and `manifest.json` are delivered to the reviewer as a single package.

Each recalculation receives its own Run ID. Previous results are not deleted or mixed with a new run.

## Capabilities

* LAS/LAZ 1.2–1.4, point formats 0–10;
* local origin for preserving `float` precision with large geodetic coordinates;
* WKT/CRS consistency checks between epochs when WKT is present in LAS;
* classification and exclusion of equipment, vegetation, and noise;
* separate “Before filtering” and “After filtering” slots for each scan;
* robust DEM based on the upper quantile of observations;
* cut, fill, balance, area, depth, volume with swell factor, and mass;
* protective ICP using only stable ground outside the AOI;
* DEM difference RMS in the stable zone and coverage percentage;
* HTML report, cell CSV, 3D viewer, and `manifest.json` with SHA-256;
* synthetic scenes: quarry, mixed warehouse, and crater;
* built-in regression testing via `--selftest`.

## What Is Actually Calculated

For each valid DEM cell with area `A`:

```
Δh = Zbase − Zcurrent

Δh > threshold   → Vcut  += Δh · A
Δh < −threshold  → Vfill += |Δh| · A

Vnet    = Vcut − Vfill
Vbulked = Vcut · Kr
Mass    = Vcut · density

```

Change threshold:

```
threshold = max(0.03 m, min(0.10 m, 0.15 · DEM_cell_size))

```

Density is specified in `t/m³`, and mass is obtained in tonnes. The swell factor `Kr` is applied only to excavated volume. It does not replace laboratory determination of material properties.

## Pipeline

1. Validation of the LAS/LAZ header, version, point format, record length, point count, scale/offset, and bounds.
2. Reading WKT from projection VLR/EVLR. Incompatible WKT between different epochs blocks the calculation.
3. Conversion of coordinates to a local coordinate system relative to the first scan's origin.
4. For very large LAS/LAZ files — deterministic spatial sampling based on an XYZ hash. It does not depend on flight-strip order in the file.
5. Classification while preserving reliable input LAS classes.
6. A single voxel sort simultaneously creates three independent clouds: RAW, CLEAN, and calculation DEM. Ground is not removed together with equipment from the same voxel.
7. ICP registration using only ground outside the AOI, without using the changing zone for registration. The solution is rejected if there are fewer than 300 stable points, insufficient overlap, excessive residual error, translation, or rotation; the report records the fallback to direct RTK alignment.
8. DEM: the 85th percentile of valid points is selected for each cell. Small isolated gaps are filled only when at least five measured neighboring cells are available.
9. Volume is integrated only where both epochs have valid coverage.
10. DEM is limited to the AOI and a 10-meter ring of the stable zone. If the selected resolution creates more than 25 million cells, it is automatically and explicitly increased based on the density of both epochs and available memory; the actual resolution is recorded in `meta.json`.
11. Export of raw/clean data, metadata, CSV, report, and manifest.

## Real-World Input Data Size

* LAS is read in streaming blocks of 4 MB. LAZ is decompressed sequentially using an embedded managed decoder, without a temporary LAS copy and without external programs.
* A file may contain tens or hundreds of millions of records; however, no more than **8 million points** from a single epoch are loaded into processing.
* The actual limit is automatically reduced to 1–8 million based on available process memory and is recorded in `manifest.json`.
* When the limit is exceeded, deterministic XYZ spatial sampling is applied. Reading time is still linear with respect to the total number of records: the entire file must be scanned.
* Processing memory is limited by the sample size, a classification grid of up to 8 million cells, and a DEM of up to 25 million cells.
* The viewer has two modes: “Quick Preview” loads up to 480,000 points from the selected RAW/CLEAN slot, while “All Points” reads the full BIN and displays 100% of the points in that slot. The actual numerator, denominator, and percentage are displayed on screen.
* Before execution, an approximate disk-space reserve is checked. If memory, disk space, CRS, or grid constraints are violated, the calculation terminates with an error and is not published as the latest Run ID.
* The calculation is not published when DEM coverage is below 50% or stable-zone RMS exceeds 0.25 m; coverage between 50–90% and RMS between 0.08–0.25 m is published only with a warning.

Practical demonstration profile: 64-bit Windows, SSD, at least 16 GB RAM, and at least 5 GB of free space in addition to the size of the input LAS/LAZ files. Actual processing time depends on the number of records in the source files, not only on the number of points retained after sampling.

## Software Guarantee Boundaries

When the built-in tests complete successfully, the program guarantees the following on supported formats:

* source LAS/LAZ files are not modified;
* a run with fewer than two epochs is not published;
* corrupted LAS/LAZ, `project.json`, and AOI files are not silently replaced with default values;
* an incompatible known WKT blocks the calculation, while missing WKT is explicitly included in warnings;
* a change in the size or modification time of an input file during processing blocks publication of the result;
* RAW, CLEAN, and DEM are generated independently; equipment does not remove ground from the same voxel;
* the complete result is created in a new `results/<RunId>/` directory only after successful completion;
* input and output hashes are recorded in the manifest.

This guarantee applies to software behavior, not absolute geodetic accuracy. The accuracy of a specific volume depends on flight quality, RTK/trajectory, CRS and vertical datum, AOI, point density, classification, and control measurements.

## Point Classes

* `1` — unclassified;
* `2` — surface/ground;
* `3`, `4`, `5` — vegetation;
* `6` — building;
* `7` — noise;
* `9` — water;
* `17` — bridge deck;
* `64` — equipment, internal user-defined class;
* `65` — measured stockpiled material, internal user-defined class.

Ground, material, and permitted unclassified surfaces participate in the DEM. Buildings, equipment, vegetation, noise, water, and bridge decks are excluded. In clean-view, buildings remain visible, while equipment/vegetation/noise are removed.

Classes `64` and `65` are internal tool conventions, not universal classifications for third-party LAS files.

## Directory Preparation

Recommended structure:

```
project/
  input/
    01_base.las
    02_epoch.las
    03_epoch.las
  boundary.geojson       # verified AOI, recommended
  project.json           # parameters and origin
  results/
    latest.json          # pointer to the latest completed Run ID
    20260912T...-abcd1234/
      meta.json
      manifest.json
      report.html
      grid_diff_epoch_1.csv
      epoch_0_raw.bin
      epoch_0_clean.bin

```

Files are naturally sorted: `epoch_2` comes before `epoch_10`. The first file is the baseline. To avoid mistakes, use a numeric prefix and visually verify the list before calculation.

Do not mix different objects, CRS, vertical datums, or units in the same directory.

## Field Survey Requirements

The following must match between compared epochs:

* coordinate system, zone, and units;
* vertical datum and geoid treatment;
* trajectory/RTK parameters and processing mode;
* sufficient overlap;
* stable terrain outside the AOI;
* point density sufficient to construct both surfaces.

Ground control points and independent checkpoints are recommended. They are not yet implemented as a complete metrological module and must be verified separately.

Uncompressed `.las` and LASzip-compatible `.laz` versions 1.2–1.4 are supported. LAS and LAZ may be mixed in one project; epoch order is determined by the overall natural filename sort order. The compressed stream is decoded sequentially, the source file is not modified, and the original `.laz` is what is hashed in the manifest.

## AOI — Calculation Boundary

Priority:

1. `boundary.geojson`;
2. `boundary.json`;
3. automatic rectangle with an offset from the cloud edge.

`boundary.json` contains local X/Y coordinates relative to the project origin. GeoJSON may contain absolute project coordinates, but geographic longitude/latitude coordinates are not automatically reprojected.

Automatic AOI is a fallback option for initial inspection, not a confirmed work boundary. A warning is recorded in the report and manifest. For production calculations, the operator must prepare and verify the boundary.

Example:

```
{
  "Vertices": [
    { "X": -10.0, "Y": -8.0 },
    { "X":  10.0, "Y": -8.0 },
    { "X":  10.0, "Y":  8.0 },
    { "X": -10.0, "Y":  8.0 }
  ],
  "IsAutoGenerated": false
}
```

A polygon with fewer than three vertices, zero area, or non-numeric coordinates is rejected.

## Running

.NET 10 SDK is required:

```
dotnet run
```

Menu items:

1. select the working directory;
2. set density, `Kr`, and voxel size;
3. select the object type for the built-in control dataset;
4. generate synthetic epochs for installation verification;
5. run the calculation;
6. open the local 3D viewer;
7. open the report;
8. reset origin/AOI when switching to another object.
9. select a single LAS/LAZ and prepare its RAW/CLEAN data for the viewer without comparing epochs.

Item 5 does not automatically create test data: without a baseline and at least one subsequent epoch, the calculation will be rejected.

Item 0 is intended for initial inspection of unknown or significantly different files. An independent local origin is created for the selected file, so the saved project baseline and AOI are not modified. The result is explicitly marked `SINGLE_SCAN_PREVIEW`: volumes, coverage, RMS, and epoch compatibility are not claimed in this mode. After preparation, open item 6 and switch between the RAW/CLEAN slots.

Without the interactive menu:

```
dotnet run -- --run "D:\scans"
```

## Filtering Verification in the Viewer

Each epoch has two separate slots:

* **Before Filtering / RAW** — registered classified cloud with equipment;
* **After Filtering / CLEAN** — cloud passed to the operator after excluding equipment, vegetation, and noise.

The camera, AOI, and metrics are shared. The operator must switch between both slots and verify:

* equipment has disappeared;
* surfaces, stockpiles, and buildings have not been incorrectly removed;
* there are no large empty areas;
* the AOI boundary corresponds to the actual work area;
* DEM coverage is acceptable;
* stable-zone RMS does not indicate an epoch shift.

The switch in the upper-right corner changes the scene background between light and dark while simultaneously adjusting grid and AOI contrast. The selection is stored in the browser.

A quality switch is located nearby. “Quick Preview” is intended for navigation, while “All Points” is intended for final visual verification. In single-scan preview, full mode is enabled automatically. When switching RAW/CLEAN, the previous full slot is removed from RAM and video memory; only one full cloud is stored at a time.

## Results

After calculation, a separate `project/results/<RunId>/` directory is created:

```
meta.json                   viewer data and two versions of each epoch
manifest.json               provenance, parameters, and SHA-256
report.html                 operational report
grid_diff_epoch_N.csv       cells inside AOI
epoch_N_raw*.bin            state before filtering
epoch_N_clean*.bin          state after filtering

```

`viewer.html` and `web/` are included in the application distribution and are not copied into the result. Menu items 6 and 7 use the latest completed run. `results/latest.json` stores its Run ID. Source LAS/LAZ files are not modified or deleted; previous runs are retained for comparison and auditing.

If no directory is selected, the workspace is created at:

```
Documents/DroneLiDAR/Workspace/

```

Therefore, launching from `bin/`, an IDE, or a published build does not pollute the application directory with project data.

## Manifest and Reproducibility

`manifest.json` contains:

* Run ID and UTC timestamp;
* application version, runtime, and OS;
* order and role of input files;
* size, modification time, and SHA-256 of every LAS/LAZ;
* source format, compression flag, LAS version, point format, record length, declared/loaded count, sampling percentage, and WKT;
* origin, AOI, voxel/DEM resolution, density, and `Kr`;
* SHA-256 of generated results;
* validation warnings.

`meta.json` additionally stores the registration mode for each epoch (`BASE`, `DIRECT_RTK`, or `ICP_STABLE_ZONE`) and ICP residual.

Matching the manifest, input hashes, and parameters is required to reproduce the result. The manifest itself is not a digital signature.

## Stable-Zone RMS

Metric:

```
RMSstable = sqrt(sum((Zbase − Zcurrent)²) / N)

```

It is calculated from valid DEM cells outside the AOI. This is **not absolute sensor accuracy, not the RMS error of control points, and not the metrological error of the volume**. If there are no more than ten stable cells, “not evaluated” is displayed instead of a fabricated value.

## Mixed Warehouse Scene

The synthetic Warehouse includes:

* concrete surface;
* two hangars classified as Building;
* 12 rectangular stockpiles classified as Material;
* loading equipment classified as Machinery.

Only stockpile height changes between epochs. The hangars remain visible but do not participate in the volume calculation; equipment is visible in RAW and absent in CLEAN.

## Self-Test

```
dotnet run -- --selftest
```

The following are checked:

* analytical volume of a rectangular excavation;
* equality between the CSV sum and the reported calculation;
* AOI JSON round-trip;
* voxel aggregation independence from point order;
* small 6-DoF ICP transformation and rejection on an insufficient dataset;
* minimal LAS 1.2–1.4 files for all point formats 0–10, RGB/intensity, and withheld;
* real LAZ round-trip, decompression, RGB/classes/withheld, and joint LAS/LAZ sorting;
* WKT and synthetic LAS labeling;
* Quarry, Warehouse, and Crater;
* presence of equipment in RAW and absence in CLEAN;
* preservation of hangars and stockpiles;
* volume growth across epochs, coverage, and RMS;
* presence of report/meta/manifest;
* placement of artifacts only in `results/<RunId>/` and absence of results in the project root.

Self-test confirms the absence of software regressions on controlled data. It does not replace field validation.

Memory, performance, and analytical-volume stress test:

```
dotnet run -c Release -- --stress 2000000
```

The permitted range is 100,000 to 4,000,000 points per epoch. The test outputs processing time, peak working set, calculated volume, and relative error; there is no hard time limit because it depends on the CPU and storage device.

## Local Server and Closed Environment

The viewer runs on `127.0.0.1`, serves only permitted extensions, and verifies that the path remains inside the latest result directory or application directory. Source LAS files and project configuration are not exposed over HTTP. CORS is disabled; basic security HTTP headers are added.

JS dependencies are stored locally in `web/`; internet access is not required for viewing. When distributing the build, retain the license files of the libraries used and verify the package SHA-256.

## Known Limitations

* LASzip-compatible LAZ is supported; COPC and other containers are not claimed;
* WKT may be absent from the source; in that case CRS compatibility is not proven;
* GeoTIFF CRS keys are not converted to WKT;
* PLY is supported only as simplified ASCII XYZ RGB without a complete header schema and is rejected when exceeding the safe limit; use LAS/LAZ for field data;
* automatic equipment classification is heuristic and depends on geometry/RGB;
* DEM is 2.5D; vertical walls, overhangs, and cavities are not modeled;
* AOI is evaluated by cell center, without partial-area intersection;
* there is no complete uncertainty model for volume and mass;
* there is no GCP/checkpoint import or independent absolute-accuracy assessment;
* there is no digital signature, role model, or immutable server-side audit log;
* full viewer mode requires loading the BIN file in its entirety; for 8 million points, a single slot requires hundreds of megabytes of RAM and video memory, so only one full RAW/CLEAN slot is displayed at a time;
* synthetic data does not prove quality on field scans.

## What Is Required for an Industrial Pilot

1. Real repeated flights with independent control points.
2. Reference volumes calculated using an independent method.
3. A labeled dataset for ground/material/building/equipment/vegetation.
4. A methodology for selecting the AOI and stable zone.
5. An uncertainty model covering survey, registration, DEM, boundary, density, and `Kr`.
6. GCP/checkpoint import and XYZ residuals.
7. GeoTIFF/GeoJSON/LAS export with CRS and units.
8. Signed result packages, operator/reviewer roles, and an action log.
9. Load testing using actual flight-data sizes.
10. Methodology assessment and separate confirmation of regulatory compliance.

## Positioning Before a Review Committee

Correct wording:

> DroneLiDAR automates reproducible comparison of two or more compatible LAS/LAZ epochs: it preserves both original and cleaned representations, calculates relative changes in a 2.5D surface within the AOI, and generates a separate verifiable package for each run containing the original input hashes.

The following must not be claimed without field evidence:

* certified accuracy;
* regulatory compliance;
* legal significance of the report;
* guaranteed removal of all equipment;
* suitability of the result for final commercial accounting.

## Why the 8 Million Point Limit Exists

This is a limitation of the current in-memory architecture, not a limitation of LAS/LAZ and not a selection of the first eight million records.

1. The header and all records of the input LAS/LAZ are read sequentially until the end.
2. If the declared point count does not exceed the calculated limit, all valid points are retained except those marked `withheld`.
3. If there are more points, a deterministic hash of the integer XYZ coordinates is calculated for each record. A spatially distributed sample is loaded into memory with probability `limit / declaredCount`.
4. The filename, full declared count, actual loaded count, and sampling percentage are recorded in `manifest.json`.

A single `Point3D` occupies 16 bytes, but during a full calculation the original/classified array, voxel-sort keys, RAW, CLEAN, DEM representation, ICP sample, and grids coexist. A protective estimate of up to 200 bytes of peak working data per loaded point is therefore used. Thus, 8 million is the upper boundary intended to prevent process termination on a typical workstation. The actual limit is calculated from available memory and constrained to the 1–8 million range.

## Full Data Flow Through the System

The following is an expanded process. It supplements the brief pipeline above.

### 1. Input Discovery and Ordering

`ScanWorkDirectory` searches for `.las` and `.laz` in `input/`; if none are found, it checks the project root, while PLY is used only as a fallback format. `CompareFileNamesNaturally` compares numeric portions of filenames as numbers, so `epoch_2` is placed before `epoch_10`. In comparison mode, the first file becomes the baseline; in single-scan mode, the operator explicitly selects the file.

### 2. Resource Checks and Staging

`RunNDPipeline` creates `.inprogress-<RunId>`. Before processing, `ComputeSafePointLimit` determines the point limit, `EnsureSufficientDiskSpace` checks the approximate disk reserve, and the size and modification time of each input are recorded. An incomplete run never becomes `latest`.

### 3. LAS Reading

`FastLoadLasFile` validates the `LASF` signature, versions 1.2–1.4, point formats 0–10, minimum record length, point-data offset, point count, scale/offset, and bounds. LAS is read in 4 MB blocks. Coordinates are reconstructed using:

```
Xworld = Xinteger · scaleX + offsetX
Yworld = Yinteger · scaleY + offsetY
Zworld = Zinteger · scaleZ + offsetZ

```

`withheld` records are not accepted. Class, return number, number of returns, RGB, or intensity are converted to `Point3D`.

### 4. LAZ Reading

`FastLoadLazFile` performs the same header checks and sequentially decodes the LASzip-compatible stream through `Altemiq.IO.Las.Compression`. No temporary LAS is created. The original LAZ is hashed in the manifest.

### 5. CRS and Local Origin

`ReadCoordinateSystemWkt` extracts WKT from projection VLR/EVLR. `PrepareProject` compares normalized epoch WKT. When a known mismatch exists, the calculation is blocked. For the first scan, the origin is set to the center of the XY bounds and minimum Z:

```
OriginX = (minX + maxX) / 2
OriginY = (minY + maxY) / 2
OriginZ = minZ
Xlocal = Xworld − OriginX
Ylocal = Yworld − OriginY
Zlocal = Zworld − OriginZ

```

This reduces `float` precision loss at large project coordinates. The program does not reproject geographic coordinates and does not reconstruct the trajectory from telemetry.

### 6. Spatial Sampling of Large Inputs

`KeepBySpatialHash` calculates an FNV-like hash of integer XYZ coordinates. The decision depends on coordinates rather than record position, so the beginning of the file or an individual flight strip does not receive systematic preference.

### 7. Classification

`ClassifyGroundAndObjects` builds an XY grid with an initial 0.5 m resolution and a limit of 8 million cells. For permitted input classes, the original classification is preserved. Heuristics are applied primarily to classes 0/1:

* a local reference elevation is formed from the lowest recent returns;
* the elevation grid is smoothed using a 3×3 window;
* components with vertical thickness greater than 0.70 m are identified using 8-neighbor connectivity;
* an equipment candidate must satisfy area constraints of 2.5–55 m², dimensions up to 18 m, and height of 1.2–6.5 m;
* unmarked equipment requires a characteristic RGB feature and a share of such points of at least 5%;
* an intermediate return above the reference surface is classified as vegetation.

If RGB is absent and the input does not contain a reliable equipment class, automatic equipment detection is not guaranteed — this is explicitly included in the warnings.

### 8. Voxelization and Independent Representations

`VoxelizeForPipeline` sorts points once by voxel indices:

```
vx = floor(X / voxelSize)
vy = floor(Y / voxelSize)
vz = floor(Z / voxelSize)

```

For each voxel, the following are accumulated independently:

* `RAW` — all classes;
* `CLEAN` — excluding equipment, vegetation, noise, and overlap;
* `Analysis` — only classes permitted for DEM.

XYZ and RGB are averaged. The class is selected by priority so that a hazardous object does not disappear from RAW when coinciding with ground. In single-scan preview, voxel reduction is not applied to RAW/CLEAN: full viewer mode receives every accepted point.

### 9. Stable-Zone Selection

`ExtractStableVectors` uses only ground-class points outside the AOI. No more than 25,000 points are retained deterministically. The changing work area is not used for registration, so excavation or fill should not “pull” the scans toward each other.

### 10. Protective ICP Registration

`AlignCloudsICP` performs up to eight iterations:

1. At least 300 stable points are required in each epoch.
2. Each cloud is deterministically limited to 25,000 points.
3. A three-dimensional k-d tree is built from the baseline.
4. For every fourth point of the current epoch, the nearest baseline point is found.
5. Pairs farther than 2 m are excluded; at least 30 pairs and at least 15% overlap are required.
6. The median squared distance is calculated.
7. The inlier threshold is `max(2.5 · medianDistanceSquared, 0.005 m²)`. Therefore, the code does not reject a fixed 50%; the proportion depends on the error distribution.
8. Centroids and a 3×3 covariance matrix are calculated from the inlier pairs.
9. `CalculateRigidTransformationSVD` solves the rigid transformation using the SVD/Kabsch method, correcting reflection when the determinant is negative.
10. The resulting rotation and translation are applied to the entire current cloud; the loop stops when the change in mean error is less than 0.0001 m.

The transformation is accepted only when mean residual is no more than 0.25 m, translation no more than 0.35 m, rotation no more than 3°, with sufficient overlap and number of inliers. Otherwise, the original direct coordinate alignment is used, and ICP rejection is recorded in the report. This is a correction of residual inter-epoch misalignment, not a replacement for RTK/PPK and not flight-telemetry processing.

### 11. DEM Construction

`CalculateVolumeBalance` limits calculation to the AOI bounds and a stable-zone ring of at least 10 m. The number of cells is limited to 25 million, with a maximum side length of 100,000 cells. When exceeded, the resolution is increased based on area, memory, and the density of both epochs.

`BuildRobustDEM` sorts valid points by cell number and selects the 85th percentile of Z. `InterpolateSmallHoles` fills only an empty cell that has at least five directly measured neighboring cells in a 3×3 window.

### 12. Volume Integration

A cell participates only when a valid surface exists in both epochs and the cell center lies inside the AOI. The change threshold is constrained to 0.03–0.10 m:

```
threshold = max(0.03, min(0.10, 0.15 · cellSize))
deltaH = Zbase − Zcurrent
deltaH > threshold  → cut  += deltaH · cellSize²
deltaH < −threshold → fill += |deltaH| · cellSize²

```

Net volume, cut/fill area, maximum depth, volume with swell factor, and mass are additionally calculated.

### 13. Quality Control

Coverage is the proportion of AOI cells with a valid surface in both epochs. RMS is calculated outside the AOI:

```
RMSstable = sqrt(sum((Zbase − Zcurrent)²) / N)

```

The run is not published when coverage is below 50% or RMS exceeds 0.25 m. Coverage of 50–90% and RMS of 0.08–0.25 m are allowed only with a warning.

### 14. Results and Reproducibility

`ExportToBinary` writes compact 16-byte points. `BuildAndExportOctreeLOD` creates overview nodes for the viewer. `GenerateHtmlReport` generates the report, `ExportVolumeGridCsv` creates the cell-level statement, and `WriteRunManifest` records parameters and SHA-256 hashes of inputs/outputs. Before publication, the size and modification time of inputs are checked again. Only after successful completion is the staging directory atomically renamed to `results/<RunId>`.

### 15. Local Viewer and Server

`ShowWebVisualizer` starts an HTTP server only on `127.0.0.1`. `ResolveStaticFile` permits a restricted set of extensions and verifies that the path remains inside the application directory or latest result. The viewer lazily loads the selected slot, supports RAW/CLEAN, fast LOD and full BIN, displays the actual number of points shown, supports light/dark backgrounds, and frees the previous full cloud from memory.

## Main Function Map

### `Lidar.cs`

* `ProjectConfig.LoadOrCreate`, `Save`, `Validate` — project parameters, origin, WKT, density, and swell factor.
* `Point3D` — 16-byte XYZ/RGB/class/return-info point and class-acceptance rules.
* `LoadScanAuto` — selection of LAS, LAZ, or PLY reader.
* `FastLoadLasFile`, `FastLoadLazFile`, `FastLoadPlyFile` — input reading and validation.
* `PrepareProject`, `ReadCoordinateSystemWkt`, `NormalizeWkt` — local coordinate system and CRS validation.
* `KeepBySpatialHash` — reproducible sampling of large point clouds.
* `VoxelFilter`, `VoxelizeForPipeline` — aggregation and RAW/CLEAN/Analysis generation.
* `ExportToBinary` — internal viewer format.
* `BuildAndExportOctreeLOD`, `SampleForViewer` — overview visualization.

### `Math.cs`

* `BoundaryPolygon.TryLoad`, `ParseGeoJsonPolygon`, `CreateInsetFromCloud`, `IsPointInside` — AOI.
* `ClassifyGroundAndObjects` — ground/vegetation/equipment classification while preserving input classes.
* `LabelElevatedComponents` — geometric analysis of connected elevated objects.
* `AlignCloudsICP` — correspondence search, robust inlier selection, and registration validation.
* `CalculateRigidTransformationSVD` — optimal rigid 6-DoF transformation.
* `KdTreeFlat` — nearest-neighbor search for ICP.
* `CalculateVolumeBalance`, `BuildRobustDEM`, `InterpolateSmallHoles` — DEM, coverage, RMS, and volumes.

### `Program.cs`

* `Main` — interactive mode, `--run`, `--selftest`, `--stress`.
* `ScanWorkDirectory`, `CompareFileNamesNaturally` — epoch discovery and ordering.
* `RunSingleScanPreviewMenu` — independent visual inspection of a single file.
* `RunNDPipeline` — orchestration of the complete run.
* `ComputeSafePointLimit`, `EnsureSufficientDiskSpace` — resource safeguards.
* `ExtractStableVectors` — stable ground zone outside the AOI.
* `WriteRunManifest`, `ComputeSha256` — result provenance auditing.
* `ShowWebVisualizer`, `ResolveStaticFile` — local viewer serving.
* `GenerateSyntheticLas`, `RunSelfTest`, `RunStressTest` — test scenes and regression testing.

### `Report.cs`

* `GenerateHtmlReport` — operational report or single-scan inspection protocol.
* `ExportVolumeGridCsv` — detailed comparison by DEM cell.

### `viewer.html`

* reading `meta.json`;
* epoch and RAW/CLEAN selection;
* LOD or 100% of points from the selected BIN;
* AOI and metric display;
* camera, background, and WebGL memory management.

## What the System Does Not Do

* does not accept CSV telemetry and synchronize it with laser measurements;
* does not perform quaternion-based coordinate transformation from the sensor body frame;
* does not replace photogrammetric/trajectory software or RTK/PPK processing;
* does not reproject longitude/latitude into the project coordinate system;
* does not guarantee compatibility with every LiDAR system solely based on the manufacturer's name;
* is not a certified measuring instrument;
* does not prove absolute accuracy without GCP/checkpoints and field validation.
