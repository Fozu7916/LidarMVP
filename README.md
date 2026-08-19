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
