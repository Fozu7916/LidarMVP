using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using LidarProcessorMVP;

namespace LidarRunner
{
    public enum SceneType
    {
        Quarry,
        Warehouse,
        Crater
    }

    class Program
    {
        private static string workDirectory = Directory.GetCurrentDirectory();
        private static float voxelStep = 0.08f;
        private static SceneType selectedScene = SceneType.Crater;

        static void Main(string[] args)
        {
            while (true)
            {
                var (files, isLas) = ScanWorkDirectory(workDirectory);
                var proj = ProjectConfig.LoadOrCreate(workDirectory);

                Console.Clear();
                Console.WriteLine("==========================================================");
                Console.WriteLine("   ЛИДАР-БИМ: DJI MATRICE 350 RTK + ZENMUSE L2 МОНИТОРИНГ ");
                Console.WriteLine("==========================================================");
                Console.WriteLine($" [ Каталог      ]: {workDirectory}");
                Console.WriteLine($" [ Гео-базис СК ]: {(proj.IsInitialized ? $"X: {proj.OriginX:F3}, Y: {proj.OriginY:F3}, Z: {proj.OriginZ:F3}" : "Центрируется по габаритам сцены")}");
                Console.WriteLine($" [ Шаг вокселя  ]: {voxelStep:F3} м");
                Console.WriteLine($" [ Текущая сцена]: {selectedScene}");

                if (files.Count > 0)
                {
                    Console.WriteLine($" [ Формат файлов]: {(isLas ? "LAS 1.4 (DJI Zenmuse L2)" : "PLY")}");
                    Console.WriteLine($" [ Сканов в базе]: {files.Count} шт.");
                }
                else
                {
                    Console.WriteLine($" [ Статус ]: Сканы не обнаружены. Готов к автогенерации.");
                }

                Console.WriteLine("----------------------------------------------------------");
                Console.WriteLine(" 1. Задать путь к рабочей папке");
                Console.WriteLine(" 2. Настроить шаг воксельной сетки");
                Console.WriteLine(" 3. Выбрать сцену (Куча / Склад / Котлован)");
                Console.WriteLine(" 4. Сгенерировать сканы (DJI L2 эмуляция + полное удаление)");
                Console.WriteLine(" 5. [ЗАПУСК] Полная обработка (Очистка техники + Расчет)");
                Console.WriteLine(" 6. Запустить Web-визуализатор (Three.js)");
                Console.WriteLine(" 7. Открыть маркшейдерский протокол (HTML/PDF)");
                Console.WriteLine(" 8. Сбросить геодезический базис project.json");
                Console.WriteLine(" 9. Выход");
                Console.WriteLine("==========================================================");
                Console.Write(" Выберите пункт: ");

                var key = Console.ReadKey(true);
                Console.WriteLine();

                switch (key.Key)
                {
                    case ConsoleKey.D1: ConfigureDirectory(); break;
                    case ConsoleKey.D2: ConfigureVoxel(); break;
                    case ConsoleKey.D3: SelectSceneType(); break;
                    case ConsoleKey.D4: GenerateCustomEpochs(); break;
                    case ConsoleKey.D5:
                        RunNDPipeline(files);
                        Console.WriteLine("\nНажмите любую клавишу для возврата в меню...");
                        Console.ReadKey();
                        break;
                    case ConsoleKey.D6: ShowWebVisualizer(); break;
                    case ConsoleKey.D7:
                        string repPath = Path.Combine(workDirectory, "report.html");
                        if (File.Exists(repPath))
                        {
                            try { Process.Start(new ProcessStartInfo(repPath) { UseShellExecute = true }); } catch { }
                        }
                        else
                        {
                            Console.WriteLine("[ОШИБКА] Протокол еще не сформирован. Сначала выполните пункт 5.");
                            Thread.Sleep(1500);
                        }
                        break;
                    case ConsoleKey.D8:
                        ResetProjectState();
                        Console.WriteLine("[ИНФО] Базис сброшен.");
                        Thread.Sleep(1000);
                        break;
                    case ConsoleKey.D9: return;
                }
            }
        }

        static (List<string> files, bool isLas) ScanWorkDirectory(string dir)
        {
            if (!Directory.Exists(dir)) return (new List<string>(), false);
            var lasFiles = new List<string>(Directory.GetFiles(dir, "*.las"));
            lasFiles.Sort();
            if (lasFiles.Count > 0) return (lasFiles, true);

            var plyFiles = new List<string>(Directory.GetFiles(dir, "*.ply"));
            plyFiles.Sort();
            return (plyFiles, false);
        }

        static void ConfigureDirectory()
        {
            Console.Clear();
            Console.Write("Введите путь к папке [Enter — текущая]: ");
            string? dir = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                workDirectory = Path.GetFullPath(dir);
                LidarPipeline.CurrentProject = ProjectConfig.LoadOrCreate(workDirectory);
            }
        }

        static void ConfigureVoxel()
        {
            Console.Clear();
            Console.Write($"Текущий воксель: {voxelStep:F3} м. Введите новый (м): ");
            if (float.TryParse(Console.ReadLine()?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s > 0)
                voxelStep = s;
        }

        static void SelectSceneType()
        {
            Console.Clear();
            Console.WriteLine("Выберите тип сцены:");
            Console.WriteLine(" 1. Карьерная куча / Насыпь (с техникой)");
            Console.WriteLine(" 2. Склад паллет и штабелей (с погрузчиком)");
            Console.WriteLine(" 3. Котлован / Кратер выемки (с буровым станком)");
            Console.Write("Выбор: ");
            var k = Console.ReadKey(true);
            switch (k.Key)
            {
                case ConsoleKey.D1: selectedScene = SceneType.Quarry; break;
                case ConsoleKey.D2: selectedScene = SceneType.Warehouse; break;
                case ConsoleKey.D3: selectedScene = SceneType.Crater; break;
            }
        }

        static void ResetProjectState()
        {
            string pPath = Path.Combine(workDirectory, "project.json");
            if (File.Exists(pPath)) File.Delete(pPath);
            string bPath = Path.Combine(workDirectory, "boundary.json");
            if (File.Exists(bPath)) File.Delete(bPath);
            LidarPipeline.CurrentProject = new ProjectConfig();
        }

        static void GenerateCustomEpochs()
        {
            Console.Clear();
            Console.WriteLine($"Генерация сканов Zenmuse L2 для сцены: {selectedScene}");
            Console.Write("Сколько этапов динамики создать после базиса? (по умолчанию 3): ");
            string? input = Console.ReadLine();
            int nStages = 3;
            if (int.TryParse(input, out int parsed) && parsed > 0) nStages = parsed;

            foreach (var f in Directory.GetFiles(workDirectory, "epoch_*.las")) File.Delete(f);
            foreach (var f in Directory.GetFiles(workDirectory, "epoch_*.bin")) File.Delete(f);
            ResetProjectState();

            Console.WriteLine("\n[1/3] Создание скана 1: Сырой скан с техникой в зоне...");
            string rawFile = Path.Combine(workDirectory, "epoch_0_raw.las");
            GenerateSyntheticLas(rawFile, selectedScene, 0.0f, true);

            Console.WriteLine("[2/3] Создание скана 2: Базовый скан (нулевой горизонт)...");
            string baseFile = Path.Combine(workDirectory, "epoch_1_base.las");
            GenerateSyntheticLas(baseFile, selectedScene, 0.0f, true);

            for (int i = 1; i <= nStages; i++)
            {
                float frac = (float)i / nStages;
                Console.WriteLine($"[3/3] Создание этапа выработки/отгрузки {i}/{nStages} ({frac * 100:F0}%)...");
                string stageFile = Path.Combine(workDirectory, $"epoch_{i + 1}_stage_{i}.las");
                GenerateSyntheticLas(stageFile, selectedScene, frac, true);
            }

            Console.WriteLine("\n[ГОТОВО] Сканы сформированы со спецификацией DJI Zenmuse L2.");
            Console.ReadKey();
        }

        static void RunNDPipeline(List<string> inputFiles)
        {
            Console.WriteLine("\n=== 1. ЗАГРУЗКА, СЕГМЕНТАЦИЯ ТЕХНИКИ И РАСЧЕТ ===");

            if (inputFiles.Count == 0)
            {
                Console.WriteLine("[ИНФО] Данных нет. Автогенерация базового сценария...");
                GenerateCustomEpochs();
                inputFiles = ScanWorkDirectory(workDirectory).files;
            }

            Stopwatch globalSw = Stopwatch.StartNew();

            var aoi = BoundaryPolygon.LoadOrCreateDefault(workDirectory, defaultRadius: 11.5f);
            Console.WriteLine($"[AOI] Активен контур полигона: {aoi.Vertices.Count} вершин.");

            List<Vector3>? stableBaseVectors = null;
            List<Point3D>? baseCloud = null;

            var metaEpochs = new List<object>();
            var reportEpochs = new List<EpochReportData>();

            float cellSize = Math.Max(0.08f, voxelStep * 1.5f);

            for (int i = 0; i < inputFiles.Count; i++)
            {
                string currentFile = inputFiles[i];
                string binFile = $"epoch_{i}.bin";

                string stageType = i == 0 ? "RAW_SCAN" : (i == 1 ? "CLEANED_BASE" : "EXCAVATION_PROGRESS");
                string stageTitle = i == 0 ? "Этап 1: До очистки техники" : (i == 1 ? "Этап 2: Базис после очистки" : $"Этап {i + 1}: Шаг выработки/отгрузки #{i - 1}");

                Console.WriteLine($"\n=== {stageTitle.ToUpper()} ({Path.GetFileName(currentFile)}) ===");

                Stopwatch readSw = Stopwatch.StartNew();
                var rawPoints = LidarPipeline.LoadScanAuto(currentFile, workDirectory);
                readSw.Stop();
                Console.WriteLine($"  [Чтение]: {readSw.ElapsedMilliseconds} мс ({rawPoints.Count} точек, DJI LAS 1.4)");

                Stopwatch filterSw = Stopwatch.StartNew();
                List<Point3D> classifiedPoints;

                if (selectedScene == SceneType.Warehouse)
                {
                    classifiedPoints = MathApparatus.FilterWarehouseMachinery(rawPoints, floorElevation: 0.08f);
                }
                else
                {
                    classifiedPoints = MathApparatus.ClassifyGroundAndObjects(rawPoints, gridCellSize: 0.45f, heightThreshold: 0.25f);
                }
                filterSw.Stop();
                Console.WriteLine($"  [PMF Сегментация рельефа]: {filterSw.ElapsedMilliseconds} мс");

                var masterCloud = new List<Point3D>();

                if (stableBaseVectors != null)
                {
                    Stopwatch icpSw = Stopwatch.StartNew();
                    var stableCurrentVectors = ExtractStableVectors(classifiedPoints, aoi);
                    var (icpTransform, error) = MathApparatus.AlignCloudsICP(stableCurrentVectors, stableBaseVectors, 8);

                    for (int j = 0; j < classifiedPoints.Count; j++)
                    {
                        var p = classifiedPoints[j];
                        var v = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), icpTransform);
                        masterCloud.Add(new Point3D(v.X, v.Y, v.Z, p.R, p.G, p.B, p.Classification, p.ReturnInfo));
                    }
                    icpSw.Stop();
                    Console.WriteLine($"  [Опорный ICP]: {icpSw.ElapsedMilliseconds} мс. Невязка: {error:F4} м");
                }
                else
                {
                    masterCloud.AddRange(classifiedPoints);
                }

                Stopwatch voxelSw = Stopwatch.StartNew();
                var optimizedCloud = LidarPipeline.VoxelFilter(masterCloud, voxelStep);
                voxelSw.Stop();
                Console.WriteLine($"  [Воксель {voxelStep}м]: {voxelSw.ElapsedMilliseconds} мс. Точек: {optimizedCloud.Count}");

                List<Point3D> exportCloud;
                if (i == 0)
                {
                    exportCloud = optimizedCloud;
                }
                else
                {
                    exportCloud = new List<Point3D>(optimizedCloud.Count);
                    for (int p = 0; p < optimizedCloud.Count; p++)
                    {
                        if (optimizedCloud[p].Classification != 64)
                        {
                            exportCloud.Add(optimizedCloud[p]);
                        }
                    }
                }

                VolumeBalance balance = new VolumeBalance(0, 0, 0, 0, 0);
                Stopwatch volumeSw = Stopwatch.StartNew();

                if (i == 1)
                {
                    baseCloud = optimizedCloud;
                    stableBaseVectors = ExtractStableVectors(optimizedCloud, aoi);
                }
                else if (i > 1 && baseCloud != null)
                {
                    string gridCsvPath = Path.Combine(workDirectory, $"grid_diff_epoch_{i}.csv");
                    balance = MathApparatus.CalculateVolumeBalance(baseCloud, optimizedCloud, cellSize, aoi, filterMachinery: true, exportGridCsv: gridCsvPath);
                }
                volumeSw.Stop();

                Console.WriteLine($"  [Маркшейдерия AOI]: {volumeSw.ElapsedMilliseconds} мс");
                Console.WriteLine($"  -> ВЫЕМКА (Cut): +{balance.CutVolume:N2} м3 | НАСЫПЬ (Fill): -{balance.FillVolume:N2} м3 | ПЛОЩАДЬ: {balance.AreaCut:N1} м2");

                LidarPipeline.ExportToBinary(exportCloud, Path.Combine(workDirectory, binFile));
                Stopwatch octreeSw = Stopwatch.StartNew();
                var octreeMeta = LidarPipeline.BuildAndExportOctreeLOD(exportCloud, workDirectory, $"epoch_{i}", maxPointsPerNode: 60000);
                octreeSw.Stop();
                Console.WriteLine($"  [Octree LOD Builder]: Нарезано {octreeMeta.Nodes.Count} узлов за {octreeSw.ElapsedMilliseconds} мс");

                metaEpochs.Add(new
                {
                    Id = i,
                    StageType = stageType,
                    Title = stageTitle,
                    BinFile = binFile,
                    Octree = octreeMeta,
                    CutVolume = Math.Round(balance.CutVolume, 2),
                    FillVolume = Math.Round(balance.FillVolume, 2),
                    NetVolume = Math.Round(balance.NetVolume, 2),
                    AreaCut = Math.Round(balance.AreaCut, 1),
                    MaxDepth = Math.Round(balance.MaxDepth, 2)
                });

                reportEpochs.Add(new EpochReportData
                {
                    Id = i,
                    Title = stageTitle,
                    FileName = Path.GetFileName(currentFile),
                    CutVolume = Math.Round(balance.CutVolume, 2),
                    FillVolume = Math.Round(balance.FillVolume, 2),
                    NetVolume = Math.Round(balance.NetVolume, 2),
                    AreaCut = Math.Round(balance.AreaCut, 1),
                    AreaFill = Math.Round(balance.AreaFill, 1),
                    MaxDepth = Math.Round(balance.MaxDepth, 2)
                });
            }

            var metaData = new
            {
                Scene = selectedScene.ToString(),
                Boundary = aoi.Vertices,
                Epochs = metaEpochs
            };

            File.WriteAllText(Path.Combine(workDirectory, "meta.json"), JsonSerializer.Serialize(metaData, new JsonSerializerOptions { WriteIndented = true }));

            string reportFile = Path.Combine(workDirectory, "report.html");
            ReportGenerator.GenerateHtmlReport(reportFile, selectedScene.ToString(), LidarPipeline.CurrentProject, reportEpochs);
            Console.WriteLine($"\n[ОТЧЕТНОСТЬ] Маркшейдерский протокол сформирован: report.html");

            globalSw.Stop();
            Console.WriteLine($"[ИТОГ] Расчет завершен за {globalSw.ElapsedMilliseconds} мс.");
        }

        static List<Vector3> ExtractStableVectors(List<Point3D> points, BoundaryPolygon aoi)
        {
            var list = new List<Vector3>(points.Count / 2);
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                if (pt.Classification == 2 && !aoi.IsPointInside(pt.X, pt.Z))
                {
                    list.Add(new Vector3(pt.X, pt.Y, pt.Z));
                }
            }

            if (list.Count < 300)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    if (points[i].Classification == 2) list.Add(new Vector3(points[i].X, points[i].Y, points[i].Z));
                }
            }
            return list;
        }

        public static void GenerateSyntheticLas(string lasPath, SceneType sceneType, float stageFraction, bool includeMachinery)
        {
            var points = new List<Point3D>(600000);

            switch (sceneType)
            {
                case SceneType.Quarry:
                    GenerateQuarryPoints(points, stageFraction);
                    if (includeMachinery) AddQuarryMachinery(points);
                    break;
                case SceneType.Warehouse:
                    GenerateWarehousePoints(points, stageFraction);
                    if (includeMachinery) AddWarehouseMachinery(points);
                    break;
                case SceneType.Crater:
                    GenerateCraterPoints(points, stageFraction);
                    if (includeMachinery) AddCraterMachinery(points);
                    break;
            }

            WriteLas14File(lasPath, points);
        }

        private static void GenerateQuarryPoints(List<Point3D> points, float digFraction)
        {
            float step = 0.06f;
            for (float x = -15.0f; x <= 15.0f; x += step)
            {
                for (float y = -15.0f; y <= 15.0f; y += step)
                {
                    float r = MathF.Sqrt(x * x + y * y);
                    float baseZ = (x * 0.05f) + (y * 0.04f);
                    float z = baseZ;

                    if (r < 10.0f) z += 5.0f * MathF.Cos(r * MathF.PI / 20.0f);

                    if (digFraction > 0.0f)
                    {
                        float pitDist = MathF.Sqrt((x - 1.5f) * (x - 1.5f) + (y + 1.0f) * (y + 1.0f));
                        float pitRadius = 6.0f * digFraction + 1.0f;
                        if (pitDist < pitRadius)
                        {
                            float digDepth = (4.5f * digFraction) * MathF.Cos(pitDist * MathF.PI / (pitRadius * 2.0f));
                            z -= digDepth;
                            if (z < baseZ + 0.2f) z = baseZ + 0.2f;
                        }
                    }

                    z += (MathF.Sin(x * 2.0f) * MathF.Cos(y * 2.0f)) * 0.04f;

                    byte rC = (byte)(r < 10.0f ? 175 : 120);
                    byte gC = (byte)(r < 10.0f ? 145 : 110);
                    byte bC = (byte)(r < 10.0f ? 100 : 95);

                    points.Add(new Point3D(x, z, y, rC, gC, bC, classification: 1, returnInfo: 0x11));
                }
            }
        }

        private static void GenerateWarehousePoints(List<Point3D> points, float pickupFraction)
        {
            float step = 0.06f;
            for (float x = -14.0f; x <= 14.0f; x += step)
            {
                for (float y = -14.0f; y <= 14.0f; y += step)
                {
                    byte rC = 160, gC = 165, bC = 175;
                    if (MathF.Abs(x) < 0.25f || MathF.Abs(y) < 0.25f) { rC = 230; gC = 190; bC = 20; }
                    points.Add(new Point3D(x, 0.0f, y, rC, gC, bC, classification: 1, returnInfo: 0x11));
                }
            }

            for (int r = 0; r < 3; r++)
            {
                float rowY = -7.0f + r * 6.5f;
                for (int b = 0; b < 5; b++)
                {
                    float boxX = -9.0f + b * 4.0f;
                    float currentHeight = 2.8f * (1.0f - pickupFraction * 0.85f);
                    if (currentHeight > 0.3f)
                    {
                        AddSolidBox(points, boxX, rowY, 0.0f, 2.2f, 1.8f, currentHeight, 185, 135, 75, classification: 1);
                    }
                }
            }
        }

        private static void GenerateCraterPoints(List<Point3D> points, float digFraction)
        {
            float step = 0.06f;
            for (float x = -15.0f; x <= 15.0f; x += step)
            {
                for (float y = -15.0f; y <= 15.0f; y += step)
                {
                    float r = MathF.Sqrt(x * x + y * y);
                    float z = 3.5f;
                    float craterOuterR = 11.0f;

                    if (r < craterOuterR)
                    {
                        float targetDepth = 7.0f * (0.25f + 0.75f * digFraction);
                        z -= targetDepth * MathF.Cos(r * MathF.PI / (craterOuterR * 2.0f));
                        z += MathF.Sin(r * 3.0f) * 0.15f;
                    }

                    byte rC = (byte)(r >= craterOuterR ? 110 : (z < 0 ? 150 : 90));
                    byte gC = (byte)(r >= craterOuterR ? 130 : (z < 0 ? 115 : 80));
                    byte bC = (byte)(r >= craterOuterR ? 85 : (z < 0 ? 80 : 70));

                    points.Add(new Point3D(x, z, y, rC, gC, bC, classification: 1, returnInfo: 0x11));
                }
            }
        }

        private static void AddQuarryMachinery(List<Point3D> points)
        {
            AddSolidBox(points, -6.5f, -5.5f, 1.2f, 3.8f, 2.8f, 2.0f, 245, 175, 15, 1);
            AddSolidBox(points, -6.5f, -5.5f, 0.2f, 4.0f, 3.0f, 1.0f, 45, 45, 45, 1);
            AddSolidBox(points, -5.5f, -4.7f, 3.2f, 1.4f, 1.3f, 1.5f, 50, 180, 230, 1);

            for (float t = 0; t <= 1.0f; t += 0.03f)
            {
                float bx = -4.5f + t * 3.5f;
                float by = -5.5f + t * 1.5f;
                float bz = 2.5f + MathF.Sin(t * MathF.PI) * 3.0f;
                AddSolidBox(points, bx, by, bz, 0.45f, 0.45f, 0.45f, 245, 175, 15, 1);
            }

            AddSolidBox(points, 7.5f, 6.0f, 1.2f, 4.2f, 2.6f, 1.8f, 220, 140, 20, 1);
            AddSolidBox(points, -12.0f, 10.0f, 0.0f, 4.5f, 2.4f, 2.4f, 40, 100, 200, 1);
        }

        private static void AddWarehouseMachinery(List<Point3D> points)
        {
            AddSolidBox(points, 0.0f, -1.0f, 0.2f, 1.8f, 1.2f, 0.7f, 255, 190, 0, 1);
            AddSolidBox(points, 0.0f, -1.0f, 0.9f, 1.2f, 1.0f, 1.3f, 40, 40, 40, 1);
            AddSolidBox(points, 1.1f, -1.0f, 0.1f, 0.25f, 0.9f, 2.7f, 50, 50, 55, 1);
            AddSolidBox(points, 1.6f, -1.0f, 0.15f, 0.9f, 0.7f, 0.1f, 20, 20, 20, 1);
        }

        private static void AddCraterMachinery(List<Point3D> points)
        {
            AddSolidBox(points, -8.5f, 4.5f, 3.6f, 3.5f, 2.4f, 1.8f, 220, 90, 20, 1);
            AddSolidBox(points, -8.5f, 4.5f, 5.4f, 1.5f, 1.3f, 1.4f, 70, 70, 75, 1);
            AddSolidBox(points, -6.8f, 4.5f, 3.6f, 0.4f, 0.4f, 5.2f, 40, 40, 40, 1);
        }

        private static void AddSolidBox(List<Point3D> points, float cx, float cy, float cz,
                                        float sizeX, float sizeY, float sizeZ,
                                        byte r, byte g, byte b, byte classification)
        {
            float step = 0.07f;
            float hx = sizeX / 2.0f;
            float hy = sizeY / 2.0f;

            for (float x = cx - hx; x <= cx + hx; x += step)
            {
                for (float y = cy - hy; y <= cy + hy; y += step)
                {
                    for (float z = cz; z <= cz + sizeZ; z += step)
                    {
                        bool isEdge = (MathF.Abs(x - (cx - hx)) < step || MathF.Abs(x - (cx + hx)) < step ||
                                       MathF.Abs(y - (cy - hy)) < step || MathF.Abs(y - (cy + hy)) < step ||
                                       MathF.Abs(z - cz) < step || MathF.Abs(z - (cz + sizeZ)) < step);

                        if (isEdge)
                        {
                            points.Add(new Point3D(x, z, y, r, g, b, classification, 0x11));
                        }
                    }
                }
            }
        }

        private static void WriteLas14File(string lasPath, List<Point3D> points)
        {
            uint totalPoints = (uint)points.Count;

            using var fs = new FileStream(lasPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
            using var bw = new BinaryWriter(fs);

            bw.Write(new byte[] { (byte)'L', (byte)'A', (byte)'S', (byte)'F' });
            bw.Write((ushort)0); bw.Write((ushort)0); bw.Write(new byte[16]);
            bw.Write((byte)1); bw.Write((byte)4);
            bw.Write(new byte[32]);
            bw.Write(Encoding.ASCII.GetBytes("DJI Terra Zenmuse L2".PadRight(32, '\0')));
            bw.Write((ushort)0); bw.Write((ushort)2026);
            bw.Write((ushort)375);
            bw.Write((uint)375);

            bw.Write((uint)0);
            bw.Write((byte)7);
            bw.Write((ushort)36);
            bw.Write((uint)0);

            bw.Write(new byte[20]);

            double scale = 0.001;
            double geoShiftX = 5432000.0;
            double geoShiftY = 3210000.0;
            double geoShiftZ = 150.0;

            bw.Write(scale); bw.Write(scale); bw.Write(scale);
            bw.Write(geoShiftX); bw.Write(geoShiftY); bw.Write(geoShiftZ);
            bw.Write(geoShiftX + 25.0); bw.Write(geoShiftX - 25.0);
            bw.Write(geoShiftY + 25.0); bw.Write(geoShiftY - 25.0);
            bw.Write(geoShiftZ + 25.0); bw.Write(geoShiftZ - 25.0);

            bw.Write((ulong)0);
            bw.Write((ulong)0);
            bw.Write((uint)0);
            bw.Write((ulong)totalPoints);

            for (int k = 0; k < 15; k++) bw.Write(k == 0 ? (ulong)totalPoints : (ulong)0);

            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                double realX = pt.X;
                double realY = pt.Z;
                double realZ = pt.Y;

                bw.Write((int)(realX / scale));
                bw.Write((int)(realY / scale));
                bw.Write((int)(realZ / scale));

                bw.Write((ushort)1200);
                bw.Write(pt.ReturnInfo);
                bw.Write((byte)0);
                bw.Write(pt.Classification);
                bw.Write((byte)0);
                bw.Write((short)0);
                bw.Write((ushort)0);
                bw.Write((double)0.0);

                bw.Write((ushort)(pt.R << 8));
                bw.Write((ushort)(pt.G << 8));
                bw.Write((ushort)(pt.B << 8));
            }
        }

        static void ShowWebVisualizer()
        {
            string url = "http://localhost:8080/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            try
            {
                listener.Start();
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }

                Console.WriteLine($"\n[СЕРВЕР] Визуализатор: {url}");
                Console.WriteLine("Нажмите клавишу для выхода...");

                bool isRunning = true;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    while (isRunning && listener.IsListening)
                    {
                        try
                        {
                            var ctx = listener.GetContext();
                            string localPath = ctx.Request.Url?.LocalPath.TrimStart('/') ?? string.Empty;
                            if (string.IsNullOrEmpty(localPath)) localPath = "viewer.html";

                            string fullPath = Path.Combine(workDirectory, localPath);

                            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                            if (File.Exists(fullPath))
                            {
                                byte[] buf = File.ReadAllBytes(fullPath);
                                if (fullPath.EndsWith(".html")) ctx.Response.ContentType = "text/html; charset=utf-8";
                                else if (fullPath.EndsWith(".json")) ctx.Response.ContentType = "application/json; charset=utf-8";
                                else if (fullPath.EndsWith(".csv")) ctx.Response.ContentType = "text/csv; charset=utf-8";
                                else ctx.Response.ContentType = "application/octet-stream";

                                ctx.Response.ContentLength64 = buf.Length;
                                ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                            }
                            else
                            {
                                ctx.Response.StatusCode = 404;
                            }
                            ctx.Response.OutputStream.Close();
                        }
                        catch { }
                    }
                });

                Console.ReadKey();
                isRunning = false;
                listener.Stop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ОШИБКА] {ex.Message}");
            }
        }
    }
}