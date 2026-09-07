PROCESSING 6 MILLION POINTS IN ~3 SECONDS
This is the MVP of the DroneLiDAR-BIM Automated Construction Monitoring System.
1) We receive CSV + ply data (or, if it's not available, we automatically generate test stubs)
2) We generate an .xyz file (processing phantom errors with classifiers)
3) We render this .xyz file on the web
<img width="744" height="347" alt="image" src="https://github.com/user-attachments/assets/32217b33-4c23-4915-a397-e7f27a720f0e" />

There are also two automatic data generation options:

- A house in a Python file (there is a darkened section)

- Three boxes are generated automatically when the CLI is launched to avoid causing exceptions due to Lack of .ply and .csv files

To understand how the code works
- The lidar.cs library, which can be integrated anywhere (UI, web, etc.)
- CLI (essentially a UI, but in a console for control and logging) - Program.cs
- A file that converts points into a 3D view from a raw .xyz file (viewer.html - renderer, transferring data from Program.cs)

How it works:
We receive the drone's position (.csv) and lidar readings (.ply) from the drone.

IMPORTANT: To be linked by the program, the .csv and .ply files must have the same name (e.g., N.csv + N.ply).

All "squares" obtained from the scan are combined into a single .xyz file. This can then be rendered directly in the program in a browser, if desired.
dotnet add package MathNet.Numerics


DroneLiDAR-BIM: High-Performance Automated Construction Monitoring
PROCESSING MILLIONS OF POINTS IN SECONDS.

This is the MVP of the DroneLiDAR-BIM Automated Construction Monitoring System, optimized for industrial drone data (e.g., DJI Matrice 350 RTK + Zenmuse L2). The system processes raw LiDAR point clouds, automatically compensates for drone drift, calculates earthwork volumes, and renders the results instantly in a browser.

🔥 Core Features
Industrial Data Ingestion: Natively reads industrial binary .las files (ASPRS standard) directly from drone software like DJI Terra. Legacy .ply + .csv telemetry is also supported.

Mathematical Core (ICP & Volumetrics):

Collision Resolution: Automatically compensates for drone sway and IMU drift using the Iterative Closest Point (ICP) algorithm powered by KD-Tree spatial indexing.

Earthwork Calculation: Calculates the exact volume of extracted material using the Cut-and-Fill 2.5D DEM method.

High-Speed Export: Generates standard .xyz files for CAD/BIM integration and ultra-fast, zero-allocation binary .bin streams for the web.

4D Web Visualization: Renders the point cloud directly in the browser. Features a "Before/After" interactive toggle and a dashboard displaying the calculated extraction volumes directly on the web.

🚀 Prerequisites
The mathematical core relies on MathNet.Numerics for heavy linear algebra and Singular Value Decomposition (SVD). Before running the project, you must install this dependency:

Bash
dotnet add package MathNet.Numerics
🛠 Architecture & How It Works
The system is decoupled into highly optimized modules that can be integrated anywhere:

Lidar.cs (The Engine): The high-performance core library. Handles zero-allocation binary parsing of .las/.ply files and Voxel grid deduplication.

MathApparatus.cs (The Brain): The mathematical solver. Implements KD-Tree, ICP alignment (SVD-based), and volumetric calculations.

Program.cs (The CLI): Orchestrates the pipeline, acts as the control interface, and hosts the local HTTP server for the web viewer.

viewer.html (The Frontend): A Three.js WebGL renderer. Fetches binary .bin buffers and meta.json to instantly visualize the 3D environment and production metrics without crashing the browser.

⚙️ The Pipeline Workflow
Drop your .las files (Strips/Gals) into the working directory.

The program takes the first scan as a rigid reference.

Subsequent scans are algorithmically aligned to the reference using ICP to eliminate "double walls" caused by wind drift.

The merged cloud is voxel-filtered and projected into a 2.5D grid to calculate the extracted volume.

Results are exported to the Web UI for presentation.

🧪 Autonomous Data Generation (Demo Mode)
You don't need real drone data to test the system.
If the CLI is launched in an empty directory, it will automatically generate synthetic industrial .las files representing two epochs ("Before" and "After" excavation) with simulated drone drift. The system will align them, calculate the volume of the "excavated" pit, and display the results in the web visualizer to prevent runtime exceptions and allow immediate testing.