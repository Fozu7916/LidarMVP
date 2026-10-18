# DroneLiDAR

Processes 4+ million points in 3 seconds (scales linearly).

A working local tool for comparing point-cloud epochs and monitoring changes in product volumes. The program accepts LAS and LAZ, checks source-data compatibility, separates equipment from the measured surface, calculates cut/fill volumes, and generates a reproducible result package.

**Practical value:** reducing manual processing of repeated surveys, establishing a consistent procedure for comparing epochs, visually verifying filtering, controlling contractor-reported volumes, and auditing the provenance of every result.

> **Applicability boundary:** until field validation, metrological assessment, and approval of the methodology, the result does not automatically have legally significant status.

## Capabilities

- LAS/LAZ 1.2–1.4, point formats 0–10
- Local origin for preserving `float` precision with large geodetic coordinates
- WKT/CRS consistency checks between epochs when WKT is present in LAS
- Classification and exclusion of equipment, vegetation, and noise
- Separate "Before filtering" (RAW) and "After filtering" (CLEAN) slots for each scan
- Robust DEM based on the upper quantile of observations
- Cut, fill, balance, area, depth, volume with swell factor, and mass
- Protective ICP using only stable ground outside the AOI
- DEM difference RMS in the stable zone and coverage percentage
- HTML report, cell CSV, 3D viewer, and `manifest.json` with SHA-256
- Synthetic scenes: quarry, mixed warehouse, and crater
- Built-in regression testing via `--selftest`

## How It Works

1. Put source LAS/LAZ files in `input/` (the first file in natural sort order is the baseline).
2. Specify density, swell factor, and a verified AOI.
3. The program validates LAS/LAZ and CRS, runs the calculation, and creates a new immutable run in `results/<RunId>/`.
4. Compare RAW/CLEAN in the 3D viewer and check equipment, buildings, coverage, and AOI.
5. Deliver `report.html`, CSV, and `manifest.json` to the reviewer as a single package.

Each recalculation receives its own Run ID; previous results are never deleted or mixed with a new run.

## Quick Start

**Requirements:** .NET 10 SDK. For the practical demonstration profile: 64-bit Windows, SSD, at least 16 GB RAM, and at least 5 GB of free space in addition to the size of the input files.

Interactive menu:

```
dotnet run
```

Without the menu:

```
dotnet run -- --run "D:\scans"
```

Verify the installation:

```
dotnet run -- --selftest
```

Item 5 of the menu needs a baseline and at least one subsequent epoch. To try the tool without real data, use item 4 to generate synthetic epochs.

## Documentation

Full documentation (project preparation, AOI, calculation method, pipeline, viewer, results, guarantees, limitations, function map): [DOCUMENTATION.md](DOCUMENTATION.md)

## Limitations

DroneLiDAR is not a certified measuring instrument. The accuracy of a specific volume depends on flight quality, RTK/trajectory, CRS and vertical datum, AOI, point density, classification, and control measurements. See [Known Limitations](DOCUMENTATION.md#14-known-limitations) before using results for commercial accounting.
