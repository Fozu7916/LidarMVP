using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Altemiq.IO.Las;
using DroneLiDAR;

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
        private static string workDirectory = Path.Combine(
            GetWorkspaceRoot(),
            "DroneLiDAR",
            "Workspace");
        private static string? activeResultsDirectory;
        private static float voxelStep = 0.08f;
        private static SceneType selectedScene = SceneType.Quarry;
        private static bool quietMode = false;

        static int Main(string[] args)
        {
            EnsureWorkspaceLayout();
            if (args.Length > 0)
            {
                if (args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
                    return RunSelfTest();
                if (args[0].Equals("--stress", StringComparison.OrdinalIgnoreCase))
                {
                    int pointCount = 2_000_000;
                    if (args.Length > 1 && int.TryParse(args[1], out int requested))
                        pointCount = Math.Clamp(requested, 100_000, 4_000_000);
                    return RunStressTest(pointCount);
                }

                if (args[0].Equals("--run", StringComparison.OrdinalIgnoreCase))
                {
                    quietMode = true;
                    if (args.Length <= 1 || !Directory.Exists(args[1]))
                    {
                        Console.Error.WriteLine("Укажите существующий каталог проекта: --run <путь>");
                        return 2;
                    }
                    workDirectory = Path.GetFullPath(args[1]);
                    activeResultsDirectory = null;
                    EnsureWorkspaceLayout();
                    var files = ScanWorkDirectory(workDirectory).files;
                    try
                    {
                        RunNDPipeline(files);
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[ОШИБКА] " + ex.Message);
                        return 1;
                    }
                }
            }

            while (true)
            {
                var (files, isLas) = ScanWorkDirectory(workDirectory);
                var proj = ProjectConfig.LoadOrCreate(workDirectory);

                Console.Clear();
                Console.WriteLine("==========================================================");
                Console.WriteLine("   DroneLiDAR  |  БПЛА RTK + LiDAR                       ");
                Console.WriteLine("   Экспресс-аудит земляных масс по данным БПЛА-LiDAR      ");
                Console.WriteLine("==========================================================");
                Console.WriteLine($" [ Проект       ]: {workDirectory}");
                Console.WriteLine($" [ Входные LAS/LAZ]: {GetInputDirectory(workDirectory)}");
                string? latestRun = FindLatestResultsDirectory();
                Console.WriteLine($" [ Результаты   ]: {(latestRun ?? "расчётов пока нет")}");
                Console.WriteLine($" [ Базис СК     ]: {(proj.IsInitialized ? $"X: {proj.OriginX:F2}, Y: {proj.OriginY:F2}, Z: {proj.OriginZ:F2}" : "Фиксируется по первому скану")}");
                Console.WriteLine($" [ Воксель / DEM]: {voxelStep:F3} м / {Math.Max(0.10f, voxelStep * 1.5f):F2} м");
                Console.WriteLine($" [ Тип объекта  ]: {SceneLabel(selectedScene)}");
                Console.WriteLine($" [ Плотность /Кр]: {proj.MaterialDensity:F2} т/м³ | Кр = {proj.BulkingFactor:F2}");

                if (files.Count > 0)
                {
                    Console.WriteLine($" [ Сканов       ]: {files.Count} шт. ({(isLas ? "LAS/LAZ 1.2–1.4" : "PLY")})  первый файл = базис");
                    int shown = Math.Min(files.Count, 6);
                    for (int i = 0; i < shown; i++)
                    {
                        string mark = i == 0 ? "БАЗИС" : $"#{i}";
                        Console.WriteLine($"     {mark,-6} {Path.GetFileName(files[i])}");
                    }
                    if (files.Count > shown)
                        Console.WriteLine($"     ... ещё {files.Count - shown}");
                }
                else
                {
                    Console.WriteLine(" [ Статус       ]: Файлов нет. Можно сгенерировать тестовые эпохи.");
                }

                Console.WriteLine("----------------------------------------------------------");
                Console.WriteLine(" 1. Задать путь к рабочему каталогу");
                Console.WriteLine(" 2. Настроить параметры грунта (плотность, Кр, воксель)");
                Console.WriteLine(" 3. Выбрать тип объекта");
                Console.WriteLine(" 4. Сгенерировать эпохи (эмуляция полётов БПЛА с LiDAR)");
                Console.WriteLine(" 5. [РАСЧЁТ] Полный цикл: классификация + объёмы + отчёт");
                Console.WriteLine(" 6. Локальный 3D-визуализатор (http://127.0.0.1:8080)");
                Console.WriteLine(" 7. Открыть маркшейдерский протокол (HTML / PDF)");
                Console.WriteLine(" 8. Сбросить геодезический базис проекта");
                Console.WriteLine(" 9. Выход");
                Console.WriteLine(" 0. [ПРОСМОТР] Один LAS/LAZ: RAW и CLEAN без сравнения");
                Console.WriteLine("==========================================================");
                Console.Write(" Выберите пункт: ");

                var key = Console.ReadKey(true);
                Console.WriteLine();

                switch (key.Key)
                {
                    case ConsoleKey.D1: ConfigureDirectory(); break;
                    case ConsoleKey.D2: ConfigureParameters(); break;
                    case ConsoleKey.D3: SelectSceneType(); break;
                    case ConsoleKey.D4: GenerateCustomEpochs(); break;
                    case ConsoleKey.D5:
                        try
                        {
                            RunNDPipeline(files);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[ОШИБКА] Расчёт не опубликован: " + ex.Message);
                        }
                        if (!quietMode)
                        {
                            Console.WriteLine("\nНажмите любую клавишу для продолжения...");
                            Console.ReadKey();
                        }
                        break;
                    case ConsoleKey.D6: ShowWebVisualizer(); break;
                    case ConsoleKey.D7:
                        string? resultDirectory = FindLatestResultsDirectory();
                        string repPath = resultDirectory == null
                            ? string.Empty
                            : Path.Combine(resultDirectory, "report.html");
                        if (File.Exists(repPath))
                        {
                            try { Process.Start(new ProcessStartInfo(repPath) { UseShellExecute = true }); } catch { }
                        }
                        else
                        {
                            Console.WriteLine("[ОШИБКА] Протокол не найден. Сначала выполните расчёт (пункт 5).");
                            PauseBrief();
                        }
                        break;
                    case ConsoleKey.D8:
                        ResetProjectState();
                        Console.WriteLine("[ИНФО] Базис проекта сброшен.");
                        PauseBrief();
                        break;
                    case ConsoleKey.D9: return 0;
                    case ConsoleKey.D0: RunSingleScanPreviewMenu(files); break;
                }
            }
        }

        static void RunSingleScanPreviewMenu(List<string> files)
        {
            if (files.Count == 0)
            {
                Console.WriteLine("[ОШИБКА] В каталоге input нет LAS/LAZ.");
                PauseBrief();
                return;
            }

            Console.Clear();
            Console.WriteLine("=== ОДИНОЧНЫЙ ПРОСМОТР LAS/LAZ ===");
            for (int i = 0; i < files.Count; i++)
                Console.WriteLine($" {i + 1,3}. {Path.GetFileName(files[i])}");
            Console.Write("\nНомер файла [Enter — отмена]: ");
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) return;
            if (!int.TryParse(input, out int selected) || selected < 1 || selected > files.Count)
            {
                Console.WriteLine("[ОШИБКА] Неверный номер файла.");
                PauseBrief();
                return;
            }

            try
            {
                RunNDPipeline(new List<string> { files[selected - 1] }, singleScanPreview: true);
                Console.WriteLine("\nФайл подготовлен. Откройте пункт 6 и переключайте RAW/CLEAN.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ОШИБКА] Предпросмотр не опубликован: " + ex.Message);
            }
            Console.WriteLine("\nНажмите любую клавишу для продолжения...");
            Console.ReadKey();
        }

        static string SceneLabel(SceneType scene) => scene switch
        {
            SceneType.Warehouse => "Открытый склад / штабеля",
            SceneType.Crater => "Строительный котлован",
            _ => "Карьер / выемка"
        };

        static string GetWorkspaceRoot()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(root))
                root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = Path.GetTempPath();
            return root;
        }

        static (List<string> files, bool isLas) ScanWorkDirectory(string dir)
        {
            if (!Directory.Exists(dir)) return (new List<string>(), false);
            string inputDir = GetInputDirectory(dir);
            var lasFiles = new List<string>(Directory.GetFiles(inputDir, "*.las"));
            lasFiles.AddRange(Directory.GetFiles(inputDir, "*.laz"));
            if (lasFiles.Count == 0 && !inputDir.Equals(dir, StringComparison.OrdinalIgnoreCase))
            {
                lasFiles.AddRange(Directory.GetFiles(dir, "*.las"));
                lasFiles.AddRange(Directory.GetFiles(dir, "*.laz"));
            }
            lasFiles.Sort(CompareFileNamesNaturally);
            if (lasFiles.Count > 0) return (lasFiles, true);

            var plyFiles = new List<string>(Directory.GetFiles(inputDir, "*.ply"));
            if (plyFiles.Count == 0 && !inputDir.Equals(dir, StringComparison.OrdinalIgnoreCase))
                plyFiles.AddRange(Directory.GetFiles(dir, "*.ply"));
            plyFiles.Sort(CompareFileNamesNaturally);
            return (plyFiles, false);
        }

        static string GetInputDirectory(string projectDirectory)
        {
            string input = Path.Combine(projectDirectory, "input");
            Directory.CreateDirectory(input);
            return input;
        }

        static string GetResultsRoot(string projectDirectory)
        {
            string results = Path.Combine(projectDirectory, "results");
            Directory.CreateDirectory(results);
            return results;
        }

        static void EnsureWorkspaceLayout()
        {
            Directory.CreateDirectory(workDirectory);
            GetInputDirectory(workDirectory);
            GetResultsRoot(workDirectory);
        }

        static string? FindLatestResultsDirectory()
        {
            if (!string.IsNullOrWhiteSpace(activeResultsDirectory)
                && Directory.Exists(activeResultsDirectory)
                && File.Exists(Path.Combine(activeResultsDirectory, "meta.json")))
            {
                return activeResultsDirectory;
            }

            string root = GetResultsRoot(workDirectory);
            activeResultsDirectory = Directory.GetDirectories(root)
                .Where(dir => !Path.GetFileName(dir).StartsWith(".inprogress-", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(dir, "meta.json")))
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return activeResultsDirectory;
        }

        static int CompareFileNamesNaturally(string left, string right)
        {
            string a = Path.GetFileName(left);
            string b = Path.GetFileName(right);
            int ia = 0, ib = 0;
            while (ia < a.Length && ib < b.Length)
            {
                if (char.IsDigit(a[ia]) && char.IsDigit(b[ib]))
                {
                    long na = 0, nb = 0;
                    while (ia < a.Length && char.IsDigit(a[ia]))
                    {
                        na = Math.Min(long.MaxValue / 10, na) * 10 + (a[ia++] - '0');
                    }
                    while (ib < b.Length && char.IsDigit(b[ib]))
                    {
                        nb = Math.Min(long.MaxValue / 10, nb) * 10 + (b[ib++] - '0');
                    }
                    int numberComparison = na.CompareTo(nb);
                    if (numberComparison != 0) return numberComparison;
                    continue;
                }

                int charComparison = char.ToUpperInvariant(a[ia]).CompareTo(char.ToUpperInvariant(b[ib]));
                if (charComparison != 0) return charComparison;
                ia++;
                ib++;
            }
            return a.Length.CompareTo(b.Length);
        }

        static void ConfigureDirectory()
        {
            Console.Clear();
            Console.Write("Введите путь к каталогу проекта [Enter — текущий]: ");
            string? dir = Console.ReadLine()?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(dir))
            {
                try
                {
                    workDirectory = Path.GetFullPath(dir);
                    activeResultsDirectory = null;
                    EnsureWorkspaceLayout();
                    LidarPipeline.CurrentProject = ProjectConfig.LoadOrCreate(workDirectory);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ОШИБКА] Не удалось открыть каталог проекта: " + ex.Message);
                    PauseBrief();
                }
            }
        }

        static void ConfigureParameters()
        {
            Console.Clear();
            var proj = ProjectConfig.LoadOrCreate(workDirectory);
            Console.WriteLine("Настройка физико-механических параметров:");
            Console.Write($"Плотность породы в целике (т/м3) [{proj.MaterialDensity:F2}]: ");
            if (TryParseFloat(Console.ReadLine(), out float d) && d > 0)
                proj.MaterialDensity = d;

            Console.Write($"Коэффициент разрыхления Кр [{proj.BulkingFactor:F2}]: ");
            if (TryParseFloat(Console.ReadLine(), out float b) && b >= 1.0f)
                proj.BulkingFactor = b;

            Console.Write($"Шаг вокселя сетки (м) [{voxelStep:F3}]: ");
            if (TryParseFloat(Console.ReadLine(), out float v) && v > 0)
                voxelStep = v;

            proj.Save(workDirectory);
        }

        static void SelectSceneType()
        {
            Console.Clear();
            Console.WriteLine("Выберите тип инспектируемого объекта:");
            Console.WriteLine(" 1. Карьерная выемка");
            Console.WriteLine(" 2. Открытый склад / штабеля");
            Console.WriteLine(" 3. Строительный котлован");
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
            TryDelete(Path.Combine(workDirectory, "project.json"));
            TryDelete(Path.Combine(workDirectory, "boundary.json"));
            LidarPipeline.CurrentProject = new ProjectConfig();
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        static void GenerateCustomEpochs(int? stages = null)
        {
            string inputDirectory = GetInputDirectory(workDirectory);
            if (!quietMode)
            {
                Console.Clear();
                Console.WriteLine($"Эмуляция полётов БПЛА для объекта: {SceneLabel(selectedScene)}");
                Console.Write("Количество циклов мониторинга (по умолчанию 3): ");
            }

            int nStages = stages ?? 3;
            if (!quietMode)
            {
                string? input = Console.ReadLine();
                if (int.TryParse(input, out int parsed) && parsed > 0) nStages = parsed;
            }

            foreach (var f in Directory.GetFiles(inputDirectory, "epoch_*.las")) TryDelete(f);
            ResetProjectState();

            Console.WriteLine("\n[1] Полёт 0: базисный скан (нулевой горизонт, техника в кадре)...");
            GenerateSyntheticLas(Path.Combine(inputDirectory, "epoch_0_base.las"), selectedScene, 0.0f, true);

            for (int i = 1; i <= nStages; i++)
            {
                float frac = (float)i / nStages;
                Console.WriteLine($"[{i + 1}] Полёт {i}: замер динамики {i}/{nStages} ({frac * 100:F0}%)...");
                GenerateSyntheticLas(Path.Combine(inputDirectory, $"epoch_{i}_stage_{i}.las"), selectedScene, frac, true);
            }

            Console.WriteLine("\n[УСПЕХ] Облака записаны как LAS 1.4 (формат точки 7 с RGB).");
            Console.WriteLine("Порядок обработки: по имени файла, первый = базис.");
            if (!quietMode) Console.ReadKey();
        }

        static void RunNDPipeline(List<string> inputFiles, bool singleScanPreview = false)
        {
            Console.WriteLine(singleScanPreview
                ? "\n=== ОДИНОЧНЫЙ ПРОСМОТР: LAS/LAZ → классификация → RAW/CLEAN ==="
                : "\n=== КОНВЕЙЕР: чтение LAS/LAZ → классификация → объёмы ===");

            if ((!singleScanPreview && inputFiles.Count < 2)
                || (singleScanPreview && inputFiles.Count != 1))
            {
                throw new InvalidDataException(
                    singleScanPreview
                        ? $"Для одиночного просмотра нужен ровно один файл, найдено: {inputFiles.Count}."
                        : $"Для расчёта нужны минимум две эпохи (базис и повторная съёмка), найдено: {inputFiles.Count}.");
            }

            var proj = ProjectConfig.LoadOrCreate(workDirectory);
            if (singleScanPreview)
            {
                proj = new ProjectConfig
                {
                    MaterialDensity = proj.MaterialDensity,
                    BulkingFactor = proj.BulkingFactor,
                    SensorInfo = proj.SensorInfo,
                    CoordinateSystemInfo = proj.CoordinateSystemInfo
                };
            }
            LidarPipeline.CurrentProject = proj;
            Stopwatch globalSw = Stopwatch.StartNew();
            string runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N")[..8];
            string resultsRoot = GetResultsRoot(workDirectory);
            string runDirectory = Path.Combine(resultsRoot, ".inprogress-" + runId);
            string finalRunDirectory = Path.Combine(resultsRoot, runId);
            Directory.CreateDirectory(runDirectory);
            int maxLoadPoints = ComputeSafePointLimit();
            EnsureSufficientDiskSpace(runDirectory, inputFiles.Count, maxLoadPoints);
            Console.WriteLine($"  [Ресурсы] лимит загрузки одной эпохи: {maxLoadPoints:N0} точек");
            var inputSnapshots = inputFiles.ToDictionary(
                path => Path.GetFullPath(path),
                path =>
                {
                    var info = new FileInfo(path);
                    return (info.Length, info.LastWriteTimeUtc);
                },
                StringComparer.OrdinalIgnoreCase);

            BoundaryPolygon? aoi = singleScanPreview
                ? null
                : BoundaryPolygon.TryLoad(workDirectory, proj);
            var metaEpochs = new List<object>();
            var reportEpochs = new List<EpochReportData>();
            var lasMetadata = new List<LasFileMetadata>();
            var runWarnings = new List<string>();
            if (singleScanPreview)
            {
                runWarnings.Add(
                    "Одиночный просмотр: объёмы, покрытие, RMS и совместимость эпох не рассчитывались.");
            }

            float cellSize = Math.Max(0.10f, voxelStep * 1.5f);
            List<Vector3>? stableBaseVectors = null;
            List<Point3D>? baseCloud = null;

            for (int i = 0; i < inputFiles.Count; i++)
            {
                string currentFile = inputFiles[i];
                bool isBase = i == 0;
                string stageType = singleScanPreview ? "PREVIEW" : isBase ? "BASE" : "PROGRESS";
                string stageTitle = singleScanPreview
                    ? "Одиночный скан"
                    : isBase
                    ? "Скан 1: Базисный горизонт"
                    : $"Скан {i + 1}: Мониторинг #{i}";

                Console.WriteLine($"\n--- {stageTitle.ToUpper()} ({Path.GetFileName(currentFile)}) ---");

                List<Point3D> rawPoints;
                try
                {
                    Stopwatch readSw = Stopwatch.StartNew();
                    rawPoints = LidarPipeline.LoadScanAuto(
                        currentFile,
                        singleScanPreview ? runDirectory : workDirectory,
                        maxLoadPoints);
                    if (singleScanPreview)
                        proj = LidarPipeline.CurrentProject;
                    lasMetadata.Add(LidarPipeline.LastMetadata);
                    readSw.Stop();
                    string inputFormat = Path.GetExtension(currentFile).TrimStart('.').ToUpperInvariant();
                    Console.WriteLine($"  [Чтение {inputFormat}]: {readSw.ElapsedMilliseconds} мс | Точек: {rawPoints.Count:N0}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [ОШИБКА чтения] {ex.Message}");
                    continue;
                }

                if (rawPoints.Count == 0)
                {
                    Console.WriteLine("  [ПРОПУСК] Пустое облако.");
                    continue;
                }
                int sourcePointCount = rawPoints.Count;

                if (aoi == null)
                {
                    aoi = BoundaryPolygon.CreateInsetFromCloud(rawPoints);
                    aoi.Save(singleScanPreview ? runDirectory : workDirectory);
                    Console.WriteLine($"  [AOI] Контур рассчитан по первому скану ({aoi.Vertices.Count} вершин).");
                }
                else if (i == 0)
                {
                    Console.WriteLine($"  [AOI] Загружен контур расчётной зоны ({aoi.Vertices.Count} вершин).");
                }

                Stopwatch filterSw = Stopwatch.StartNew();
                var classifiedPoints = MathApparatus.ClassifyGroundAndObjects(rawPoints, gridCellSize: 0.5f, heightThreshold: 0.25f);
                rawPoints = null!;
                filterSw.Stop();
                Console.WriteLine($"  [Классификация рельефа / техники]: {filterSw.ElapsedMilliseconds} мс");

                List<Point3D> masterCloud;
                string registrationMode = isBase ? "BASE" : "DIRECT_RTK";
                float? icpErrorMetres = null;
                if (stableBaseVectors != null)
                {
                    Stopwatch icpSw = Stopwatch.StartNew();
                    var stableCurrentVectors = ExtractStableVectors(classifiedPoints, aoi);
                    var (icpTransform, error, isReliable) = MathApparatus.AlignCloudsICP(stableCurrentVectors, stableBaseVectors, 8, maxAllowedShift: 0.35f);

                    if (isReliable)
                    {
                        registrationMode = "ICP_STABLE_ZONE";
                        icpErrorMetres = error;
                        masterCloud = new List<Point3D>(classifiedPoints.Count);
                        for (int j = 0; j < classifiedPoints.Count; j++)
                        {
                            var p = classifiedPoints[j];
                            var v = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), icpTransform);
                            masterCloud.Add(new Point3D(v.X, v.Y, v.Z, p.R, p.G, p.B, p.Classification, p.ReturnInfo));
                        }
                        Console.WriteLine($"  [Опорный ICP]: сшито ({icpSw.ElapsedMilliseconds} мс), невязка: {error:F4} м");
                    }
                    else
                    {
                        string detail = float.IsFinite(error) ? $"невязка {error:F3} м" : "недостаточно надёжных соответствий";
                        Console.WriteLine($"  [RTK-контроль]: ICP отклонён ({detail}). Сохранена прямая RTK-привязка.");
                        runWarnings.Add(
                            $"Эпоха {i}: ICP по стабильной зоне отклонён ({detail}); использована прямая RTK-привязка.");
                        masterCloud = classifiedPoints;
                    }
                    icpSw.Stop();
                }
                else
                {
                    masterCloud = classifiedPoints;
                }

                List<Point3D> optimizedCloud;
                List<Point3D> exportCloud;
                List<Point3D> analysisCloud;
                int machinery;
                int veg;
                if (singleScanPreview)
                {
                    optimizedCloud = masterCloud;
                    exportCloud = new List<Point3D>(masterCloud.Count);
                    machinery = 0;
                    veg = 0;
                    for (int j = 0; j < masterCloud.Count; j++)
                    {
                        Point3D point = masterCloud[j];
                        if (point.Classification == Point3D.ClassMachinery)
                            machinery++;
                        if (point.Classification == Point3D.ClassLowVeg
                            || point.Classification == Point3D.ClassMediumVeg
                            || point.Classification == Point3D.ClassHighVeg)
                        {
                            veg++;
                        }
                        if (!Point3D.IsRemovedFromCleanView(point.Classification))
                            exportCloud.Add(point);
                    }
                    analysisCloud = new List<Point3D>();
                }
                else
                {
                    var voxelized = LidarPipeline.VoxelizeForPipeline(masterCloud, voxelStep);
                    optimizedCloud = voxelized.Raw;
                    exportCloud = voxelized.Clean;
                    analysisCloud = voxelized.Analysis;
                    machinery = voxelized.MachineryRemoved;
                    veg = voxelized.VegetationRemoved;
                }
                Console.WriteLine($"  [Фильтр]: удалено техники {machinery:N0} | растительности {veg:N0} | экспорт {exportCloud.Count:N0}");

                VolumeBalance balance = new VolumeBalance(
                    0, 0, 0, 0, 0,
                    proj.MaterialDensity,
                    proj.BulkingFactor,
                    double.NaN);

                if (isBase)
                {
                    if (!singleScanPreview)
                    {
                        baseCloud = analysisCloud;
                        stableBaseVectors = ExtractStableVectors(analysisCloud, aoi);
                        if (stableBaseVectors.Count < 300)
                        {
                            runWarnings.Add(
                                "Вне AOI найдено менее 300 стабильных точек ground; защитная ICP-регистрация недоступна.");
                        }
                        Console.WriteLine("  [Базис] Зафиксирован. Последующие сканы сравниваются с ним.");
                    }
                    else
                    {
                        Console.WriteLine("  [Просмотр] Подготовлены независимые версии RAW и CLEAN.");
                    }
                }
                else if (baseCloud != null)
                {
                    string gridCsvPath = Path.Combine(runDirectory, $"grid_diff_epoch_{i}.csv");
                    balance = MathApparatus.CalculateVolumeBalance(baseCloud, analysisCloud, cellSize, aoi,
                        proj.MaterialDensity, proj.BulkingFactor, filterMachinery: false, exportGridCsv: gridCsvPath);
                }

                string rmsText = double.IsFinite(balance.RmseElevation)
                    ? $"{balance.RmseElevation:F3} м"
                    : "не оценено";
                if (!singleScanPreview)
                    Console.WriteLine($"  [Объёмы]: выемка +{balance.CutVolume:N2} м³ ({balance.MaterialWeightTon:N1} т) | насыпь {balance.FillVolume:N2} м³ | RMS стабильной зоны {rmsText}");
                if (!isBase && balance.CoveragePercent < 50.0)
                {
                    throw new InvalidDataException(
                        $"Эпоха {i}: покрытие DEM {balance.CoveragePercent:F1}% ниже аварийного порога 50%. "
                        + "Проверьте перекрытие, AOI и систему координат.");
                }
                if (!isBase && double.IsFinite(balance.RmseElevation) && balance.RmseElevation > 0.25)
                {
                    throw new InvalidDataException(
                        $"Эпоха {i}: RMS стабильной зоны {balance.RmseElevation:F3} м превышает аварийный порог 0,25 м.");
                }
                if (!isBase && double.IsFinite(balance.RmseElevation) && balance.RmseElevation > 0.08)
                {
                    string warning = $"Эпоха {i}: RMS стабильной зоны выше 0,08 м.";
                    runWarnings.Add(warning);
                    Console.WriteLine($"  [ВНИМАНИЕ] {warning}");
                }
                if (!isBase && !double.IsFinite(balance.RmseElevation))
                    runWarnings.Add($"Эпоха {i}: RMS стабильной зоны не оценён — недостаточно валидных ячеек вне AOI.");
                if (!isBase && balance.CoveragePercent < 90.0)
                    runWarnings.Add($"Эпоха {i}: покрытие валидными DEM-ячейками {balance.CoveragePercent:F1}%.");

                string rawBinFile = $"epoch_{i}_raw.bin";
                string cleanBinFile = $"epoch_{i}_clean.bin";
                LidarPipeline.ExportToBinary(optimizedCloud, Path.Combine(runDirectory, rawBinFile));
                LidarPipeline.ExportToBinary(exportCloud, Path.Combine(runDirectory, cleanBinFile));
                var rawOctree = LidarPipeline.BuildAndExportOctreeLOD(
                    optimizedCloud, runDirectory, $"epoch_{i}_raw", maxPointsPerNode: 60000);
                var cleanOctree = LidarPipeline.BuildAndExportOctreeLOD(
                    exportCloud, runDirectory, $"epoch_{i}_clean", maxPointsPerNode: 60000);

                metaEpochs.Add(new
                {
                    Id = i,
                    StageType = stageType,
                    Title = stageTitle,
                    Views = new object[]
                    {
                        new
                        {
                            Kind = "RAW",
                            Title = $"{stageTitle}: До фильтрации",
                            BinFile = rawBinFile,
                            Octree = rawOctree,
                            PointCount = optimizedCloud.Count
                        },
                        new
                        {
                            Kind = "CLEAN",
                            Title = $"{stageTitle}: После фильтрации",
                            BinFile = cleanBinFile,
                            Octree = cleanOctree,
                            PointCount = exportCloud.Count
                        }
                    },
                    CutVolume = Math.Round(balance.CutVolume, 2),
                    BulkedVolume = Math.Round(balance.BulkedVolume, 2),
                    WeightTon = Math.Round(balance.MaterialWeightTon, 1),
                    FillVolume = Math.Round(balance.FillVolume, 2),
                    NetVolume = Math.Round(balance.NetVolume, 2),
                    AreaCut = Math.Round(balance.AreaCut, 1),
                    RmsStableZone = double.IsFinite(balance.RmseElevation)
                        ? Math.Round(balance.RmseElevation, 3)
                        : (double?)null,
                    StableCellCount = balance.StableCellCount,
                    CoveragePercent = Math.Round(balance.CoveragePercent, 1),
                    DemCellSizeMetres = float.IsFinite(balance.DemCellSize)
                        ? Math.Round(balance.DemCellSize, 4)
                        : (double?)null,
                    MaxDepth = Math.Round(balance.MaxDepth, 2),
                    SourcePointCount = sourcePointCount,
                    ExportPointCount = exportCloud.Count,
                    RegistrationMode = registrationMode,
                    IcpErrorMetres = icpErrorMetres.HasValue
                        ? Math.Round(icpErrorMetres.Value, 4)
                        : (double?)null,
                    MachineryRemoved = machinery,
                    VegetationRemoved = veg
                });

                reportEpochs.Add(new EpochReportData
                {
                    Id = i,
                    Title = stageTitle,
                    FileName = Path.GetFileName(currentFile),
                    CutVolume = Math.Round(balance.CutVolume, 2),
                    BulkedVolume = Math.Round(balance.BulkedVolume, 2),
                    MaterialWeightTon = Math.Round(balance.MaterialWeightTon, 1),
                    FillVolume = Math.Round(balance.FillVolume, 2),
                    NetVolume = Math.Round(balance.NetVolume, 2),
                    AreaCut = Math.Round(balance.AreaCut, 1),
                    AreaFill = Math.Round(balance.AreaFill, 1),
                    MaxDepth = Math.Round(balance.MaxDepth, 2),
                    RmseElevation = double.IsFinite(balance.RmseElevation)
                        ? Math.Round(balance.RmseElevation, 3)
                        : null,
                    StableCellCount = balance.StableCellCount,
                    CoveragePercent = Math.Round(balance.CoveragePercent, 1),
                    SourcePointCount = sourcePointCount,
                    ExportPointCount = exportCloud.Count,
                    MachineryRemoved = machinery,
                    VegetationRemoved = veg
                });
            }

            if (aoi == null) aoi = BoundaryPolygon.CreateCircle(12.0f);
            int minimumProcessedFiles = singleScanPreview ? 1 : 2;
            if (reportEpochs.Count != inputFiles.Count || reportEpochs.Count < minimumProcessedFiles)
            {
                throw new InvalidDataException(
                    $"Результат не опубликован: обработано файлов {reportEpochs.Count} из {inputFiles.Count}; "
                    + $"требуется минимум {minimumProcessedFiles}.");
            }

            var metaData = new
            {
                SchemaVersion = 2,
                RunId = runId,
                RunMode = singleScanPreview ? "SINGLE_SCAN_PREVIEW" : "EPOCH_COMPARISON",
                Scene = selectedScene.ToString(),
                Sensor = proj.SensorInfo,
                Boundary = aoi.Vertices,
                BoundaryIsAutoGenerated = aoi.IsAutoGenerated,
                BaseFile = inputFiles.Count > 0 ? Path.GetFileName(inputFiles[0]) : string.Empty,
                Epochs = metaEpochs
            };

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
            File.WriteAllText(Path.Combine(runDirectory, "meta.json"), JsonSerializer.Serialize(metaData, jsonOptions));
            if (aoi.IsAutoGenerated)
                runWarnings.Add("AOI создан автоматически и должен быть проверен оператором.");
            int filesWithoutWkt = lasMetadata.Count(m => string.IsNullOrWhiteSpace(m.CoordinateSystemWkt));
            if (filesWithoutWkt > 0)
            {
                runWarnings.Add(
                    $"CRS/WKT отсутствует в {filesWithoutWkt} из {lasMetadata.Count} входных LAS/LAZ; совместимость систем координат не доказана.");
            }
            int filesWithoutRgb = lasMetadata.Count(m => !m.HasRgb);
            if (filesWithoutRgb > 0)
            {
                runWarnings.Add(
                    $"RGB отсутствует в {filesWithoutRgb} из {lasMetadata.Count} входных LAS/LAZ; "
                    + "распознавание немаркированной техники не подтверждено. Требуется входной класс 64 или ручная проверка RAW/CLEAN.");
            }

            ReportGenerator.GenerateHtmlReport(
                Path.Combine(runDirectory, "report.html"),
                SceneLabel(selectedScene),
                proj,
                reportEpochs,
                runId,
                aoi.IsAutoGenerated,
                runWarnings,
                singleScanPreview);
            if (singleScanPreview)
            {
                TryDelete(Path.Combine(runDirectory, "project.json"));
                TryDelete(Path.Combine(runDirectory, "boundary.json"));
            }
            Console.WriteLine("  [Контроль] Вычисление SHA-256 входных и выходных файлов...");
            WriteRunManifest(
                runDirectory,
                runId,
                inputFiles,
                inputSnapshots,
                proj,
                aoi,
                lasMetadata,
                runWarnings,
                maxLoadPoints,
                singleScanPreview);
            Directory.Move(runDirectory, finalRunDirectory);
            activeResultsDirectory = finalRunDirectory;
            File.WriteAllText(
                Path.Combine(resultsRoot, "latest.json"),
                JsonSerializer.Serialize(new
                {
                    RunId = runId,
                    RelativePath = runId,
                    CompletedUtc = DateTime.UtcNow
                }, jsonOptions),
                Encoding.UTF8);

            globalSw.Stop();
            Console.WriteLine($"\n[ИТОГ] {(singleScanPreview ? "Предпросмотр" : "Расчёт")} завершён за {globalSw.ElapsedMilliseconds} мс.");
            Console.WriteLine($"       Папка результата: {finalRunDirectory}");
            Console.WriteLine($"       Отчёт: {Path.Combine(finalRunDirectory, "report.html")}");
            Console.WriteLine(singleScanPreview
                ? "       Визуализация: пункт 6, слоты «До фильтрации» и «После фильтрации»."
                : "       Визуализация: пункт 6. Сканы обрабатываются в алфавитном порядке, первый = базис.");
        }

        static void WriteRunManifest(
            string runDirectory,
            string runId,
            List<string> inputFiles,
            Dictionary<string, (long Length, DateTime LastWriteUtc)> inputSnapshots,
            ProjectConfig project,
            BoundaryPolygon aoi,
            List<LasFileMetadata> lasMetadata,
            List<string> warnings,
            int maxLoadPoints,
            bool singleScanPreview = false)
        {
            var inputs = new List<object>();
            for (int i = 0; i < inputFiles.Count; i++)
            {
                string path = inputFiles[i];
                var info = new FileInfo(path);
                var initial = inputSnapshots[Path.GetFullPath(path)];
                if (info.Length != initial.Length || info.LastWriteTimeUtc != initial.LastWriteUtc)
                {
                    throw new IOException(
                        $"Входной файл изменился во время расчёта: {info.Name}. Запуск не будет опубликован.");
                }
                LasFileMetadata? las = i < lasMetadata.Count ? lasMetadata[i] : null;
                inputs.Add(new
                {
                    Order = i,
                    Role = singleScanPreview ? "PREVIEW" : i == 0 ? "BASE" : "EPOCH",
                    FileName = info.Name,
                    SizeBytes = info.Length,
                    LastWriteUtc = info.LastWriteTimeUtc,
                    Sha256 = ComputeSha256(path),
                    SamplingPercent = las != null && las.DeclaredPointCount > 0
                        ? Math.Round(las.LoadedPointCount * 100.0 / las.DeclaredPointCount, 3)
                        : (double?)null,
                    Las = las
                });
            }

            var outputs = new List<object>();
            string[] outputFiles = Directory.GetFiles(runDirectory)
                .Where(path =>
                {
                    string name = Path.GetFileName(path);
                    return name.Equals("meta.json", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("report.html", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("grid_diff_epoch_", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string path in outputFiles)
            {
                var info = new FileInfo(path);
                outputs.Add(new
                {
                    FileName = info.Name,
                    SizeBytes = info.Length,
                    Sha256 = ComputeSha256(path)
                });
            }

            var manifest = new
            {
                SchemaVersion = 1,
                RunId = runId,
                RunMode = singleScanPreview ? "SINGLE_SCAN_PREVIEW" : "EPOCH_COMPARISON",
                CreatedUtc = DateTime.UtcNow,
                Application = new
                {
                    Name = "DroneLiDAR",
                    Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription
                },
                Parameters = new
                {
                    VoxelSizeMetres = voxelStep,
                    DemCellSizeMetres = singleScanPreview
                        ? (double?)null
                        : Math.Max(0.10f, voxelStep * 1.5f),
                    MaxLoadedPointsPerEpoch = maxLoadPoints,
                    MaterialDensityTonPerM3 = project.MaterialDensity,
                    BulkingFactor = project.BulkingFactor,
                    Origin = new { project.OriginX, project.OriginY, project.OriginZ },
                    CoordinateSystemWkt = project.CoordinateSystemWkt,
                    BoundaryIsAutoGenerated = aoi.IsAutoGenerated,
                    Boundary = aoi.Vertices
                },
                Inputs = inputs,
                Outputs = outputs,
                Warnings = warnings
            };

            var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
            File.WriteAllText(
                Path.Combine(runDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, options),
                Encoding.UTF8);
        }

        static int ComputeSafePointLimit()
        {
            const int absoluteLimit = LidarPipeline.DefaultMaxLoadPoints;
            const int minimumLimit = 1_000_000;
            const long estimatedPeakBytesPerPoint = 200;
            long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (available <= 0) return absoluteLimit;

            long memoryBudget = Math.Max(512L * 1024 * 1024, available / 4);
            long calculated = memoryBudget / estimatedPeakBytesPerPoint;
            return (int)Math.Clamp(calculated, minimumLimit, absoluteLimit);
        }

        static void EnsureSufficientDiskSpace(string outputDirectory, int epochCount, int maxLoadPoints)
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(outputDirectory));
            if (string.IsNullOrWhiteSpace(root)) return;

            var drive = new DriveInfo(root);
            long estimatedOutput = checked(
                512L * 1024 * 1024
                + (long)Math.Max(2, epochCount) * maxLoadPoints * 36L);
            if (drive.AvailableFreeSpace < estimatedOutput)
            {
                throw new IOException(
                    $"Недостаточно места для безопасного расчёта: свободно {drive.AvailableFreeSpace / (1024 * 1024):N0} МБ, "
                    + $"требуется ориентировочно {estimatedOutput / (1024 * 1024):N0} МБ.");
            }
        }

        static string ComputeSha256(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4 * 1024 * 1024,
                FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        static List<Vector3> ExtractStableVectors(List<Point3D> points, BoundaryPolygon aoi)
        {
            const int MaxStablePoints = 25_000;
            var queue = new PriorityQueue<(Vector3 Point, uint Hash), long>(MaxStablePoints);
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                if (pt.Classification == Point3D.ClassGround && !aoi.IsPointInside(pt.X, pt.Y))
                {
                    OfferStablePoint(queue, new Vector3(pt.X, pt.Y, pt.Z), MaxStablePoints);
                }
            }

            var selected = new List<(Vector3 Point, uint Hash)>(queue.Count);
            while (queue.TryDequeue(out var item, out _)) selected.Add(item);
            selected.Sort((a, b) => a.Hash.CompareTo(b.Hash));

            var result = new List<Vector3>(selected.Count);
            for (int i = 0; i < selected.Count; i++) result.Add(selected[i].Point);
            return result;
        }

        static void OfferStablePoint(
            PriorityQueue<(Vector3 Point, uint Hash), long> queue,
            Vector3 point,
            int maxCount)
        {
            uint hash = SpatialPointHash(point);
            long priority = -(long)hash;
            if (queue.Count < maxCount)
            {
                queue.Enqueue((point, hash), priority);
                return;
            }

            queue.TryPeek(out _, out long worstPriority);
            if (priority > worstPriority)
            {
                queue.Dequeue();
                queue.Enqueue((point, hash), priority);
            }
        }

        static uint SpatialPointHash(Vector3 point)
        {
            unchecked
            {
                int x = (int)MathF.Round(point.X * 1000);
                int y = (int)MathF.Round(point.Y * 1000);
                int z = (int)MathF.Round(point.Z * 1000);
                uint hash = 2166136261;
                hash = (hash ^ (uint)x) * 16777619;
                hash = (hash ^ (uint)y) * 16777619;
                return (hash ^ (uint)z) * 16777619;
            }
        }

        public static void GenerateSyntheticLas(string lasPath, SceneType sceneType, float stageFraction, bool includeMachinery)
        {
            var points = new List<Point3D>(600000);

            switch (sceneType)
            {
                case SceneType.Quarry:
                    GenerateQuarryPoints(points, stageFraction);
                    AddSparseVegetation(points);
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
            var rng = new Random(17);
            for (float x = -15.0f; x <= 15.0f; x += step)
            {
                for (float y = -15.0f; y <= 15.0f; y += step)
                {
                    float px = x + ((float)rng.NextDouble() - 0.5f) * step * 0.70f;
                    float py = y + ((float)rng.NextDouble() - 0.5f) * step * 0.70f;
                    float r = MathF.Sqrt(px * px + py * py);
                    float baseZ = (px * 0.04f) + (py * 0.03f);
                    float z = baseZ;

                    if (r < 10.0f) z += 4.5f * MathF.Cos(r * MathF.PI / 20.0f);

                    if (digFraction > 0.0f)
                    {
                        float pitDist = MathF.Sqrt((px - 1.5f) * (px - 1.5f) + (py + 1.0f) * (py + 1.0f));
                        float pitRadius = 6.0f * digFraction + 1.0f;
                        if (pitDist < pitRadius)
                        {
                            float digDepth = (4.0f * digFraction) * MathF.Cos(pitDist * MathF.PI / (pitRadius * 2.0f));
                            z -= digDepth;
                            if (z < baseZ + 0.15f) z = baseZ + 0.15f;
                        }
                    }

                    z += (MathF.Sin(px * 2.0f) * MathF.Cos(py * 2.0f)) * 0.03f;
                    z += ((float)rng.NextDouble() - 0.5f) * 0.04f;

                    byte rC = (byte)(r < 10.0f ? 180 : 120);
                    byte gC = (byte)(r < 10.0f ? 150 : 110);
                    byte bC = (byte)(r < 10.0f ? 100 : 90);
                    points.Add(new Point3D(px, py, z, rC, gC, bC, classification: Point3D.ClassGround, returnInfo: 0x11));
                }
            }
        }

        private static void GenerateWarehousePoints(List<Point3D> points, float pickupFraction)
        {
            float step = 0.06f;
            var rng = new Random(21);
            for (float x = -14.0f; x <= 14.0f; x += step)
            {
                for (float y = -14.0f; y <= 14.0f; y += step)
                {
                    float px = x + ((float)rng.NextDouble() - 0.5f) * step * 0.70f;
                    float py = y + ((float)rng.NextDouble() - 0.5f) * step * 0.70f;
                    points.Add(new Point3D(px, py, ((float)rng.NextDouble() - 0.5f) * 0.02f, 160, 165, 175, Point3D.ClassGround, 0x11));
                }
            }

            AddSolidBox(points, -8.5f, 10.0f, 0.02f, 8.0f, 4.0f, 3.8f,
                115, 135, 155, Point3D.ClassBuilding);
            AddSolidBox(points, 6.5f, 10.0f, 0.02f, 8.0f, 4.0f, 4.5f,
                125, 145, 165, Point3D.ClassBuilding);

            // Три складские линии, по четыре прямоугольных штабеля материалов.
            // Высота уменьшается по эпохам, имитируя отгрузку со склада.
            for (int row = 0; row < 3; row++)
            {
                float rowY = -6.0f + row * 6.0f;
                for (int stack = 0; stack < 4; stack++)
                {
                    float stackX = -7.5f + stack * 5.0f;
                    float reduction = 0.55f + 0.08f * ((row + stack) % 3);
                    float height = 2.8f * (1.0f - pickupFraction * reduction);
                    if (height < 0.20f) continue;

                    byte red = (byte)(145 + row * 12);
                    byte green = (byte)(112 + stack * 7);
                    byte blue = (byte)(90 + row * 6);
                    AddSolidBox(points, stackX, rowY, 0.02f, 3.2f, 2.6f, height,
                        red, green, blue, Point3D.ClassMaterial);
                }
            }
        }

        private static void GenerateCraterPoints(List<Point3D> points, float digFraction)
        {
            float step = 0.06f;
            var rng = new Random(29);
            for (float x = -15.0f; x <= 15.0f; x += step)
            {
                for (float y = -15.0f; y <= 15.0f; y += step)
                {
                    float px = x + ((float)rng.NextDouble() - 0.5f) * step * 0.70f;
                    float py = y + ((float)rng.NextDouble() - 0.5f) * step * 0.70f;
                    float r = MathF.Sqrt(px * px + py * py);
                    float z = 3.5f;
                    float craterOuterR = 11.0f;

                    if (r < craterOuterR)
                    {
                        float targetDepth = 6.5f * (0.25f + 0.75f * digFraction);
                        z -= targetDepth * MathF.Cos(r * MathF.PI / (craterOuterR * 2.0f));
                        z += MathF.Sin(r * 3.0f) * 0.12f;
                    }
                    z += ((float)rng.NextDouble() - 0.5f) * 0.03f;

                    byte rC = (byte)(r >= craterOuterR ? 110 : (z < 0 ? 150 : 90));
                    byte gC = (byte)(r >= craterOuterR ? 130 : (z < 0 ? 115 : 80));
                    byte bC = (byte)(r >= craterOuterR ? 85 : (z < 0 ? 80 : 70));
                    points.Add(new Point3D(px, py, z, rC, gC, bC, Point3D.ClassGround, 0x11));
                }
            }
        }

        private static void AddSparseVegetation(List<Point3D> points)
        {
            var rng = new Random(42);
            float[][] trees =
            {
                new[] { 12.2f, 12.0f },
                new[] { -12.4f, 11.6f },
                new[] { 12.0f, -12.2f },
                new[] { -13.0f, -11.4f }
            };

            foreach (var t in trees)
            {
                float cx = t[0], cy = t[1];
                for (int i = 0; i < 90; i++)
                {
                    float a = (float)(rng.NextDouble() * Math.PI * 2);
                    float rad = (float)rng.NextDouble() * 1.15f;
                    float x = cx + MathF.Cos(a) * rad;
                    float y = cy + MathF.Sin(a) * rad;
                    float zTop = 4.8f + (float)rng.NextDouble() * 2.2f;
                    points.Add(new Point3D(x, y, zTop, 45, 130, 55, 1, 0x21));
                    points.Add(new Point3D(x, y, 0.15f + (float)rng.NextDouble() * 0.1f, 90, 95, 70, 1, 0x22));
                }
            }
        }

        private static void AddQuarryMachinery(List<Point3D> points)
        {
            AddSolidBox(points, -6.5f, -5.5f, 1.2f, 3.8f, 2.8f, 2.0f, 245, 175, 15, Point3D.ClassMachinery);
            AddSolidBox(points, -6.5f, -5.5f, 0.2f, 4.0f, 3.0f, 1.0f, 45, 45, 45, Point3D.ClassMachinery);
            AddSolidBox(points, -5.5f, -4.7f, 3.2f, 1.4f, 1.3f, 1.5f, 50, 180, 230, Point3D.ClassMachinery);

            for (float t = 0; t <= 1.0f; t += 0.03f)
            {
                float bx = -4.5f + t * 3.5f;
                float by = -5.5f + t * 1.5f;
                float bz = 2.5f + MathF.Sin(t * MathF.PI) * 3.0f;
                AddSolidBox(points, bx, by, bz, 0.45f, 0.45f, 0.45f, 245, 175, 15, Point3D.ClassMachinery);
            }
        }

        private static void AddWarehouseMachinery(List<Point3D> points)
        {
            AddSolidBox(points, 10.5f, -10.5f, 0.2f, 3.6f, 2.4f, 2.2f, 255, 190, 0, Point3D.ClassMachinery);
        }

        private static void AddCraterMachinery(List<Point3D> points)
        {
            AddSolidBox(points, -8.5f, 4.5f, 3.6f, 3.5f, 2.4f, 1.8f, 220, 90, 20, Point3D.ClassMachinery);
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
                            points.Add(new Point3D(x, y, z, r, g, b, classification, 0x11));
                    }
                }
            }
        }

        private static void WriteLas14File(string lasPath, List<Point3D> points)
        {
            uint totalPoints = (uint)points.Count;
            double scale = 0.001;
            double geoShiftX = 5432000.0;
            double geoShiftY = 3210000.0;
            double geoShiftZ = 150.0;
            byte[] wktBytes = Encoding.UTF8.GetBytes(
                "LOCAL_CS[\"SYNTHETIC_LOCAL_METRES\",LOCAL_DATUM[\"SYNTHETIC\",0],UNIT[\"metre\",1.0]]\0");
            uint pointDataOffset = checked((uint)(375 + 54 + wktBytes.Length));
            ulong[] returnCounts = new ulong[15];
            for (int i = 0; i < points.Count; i++)
            {
                int returnIndex = Math.Clamp(points[i].ReturnNumber, (byte)1, (byte)15) - 1;
                returnCounts[returnIndex]++;
            }

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i < points.Count; i++)
            {
                double x = points[i].X + geoShiftX;
                double y = points[i].Y + geoShiftY;
                double z = points[i].Z + geoShiftZ;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }

            using var fs = new FileStream(lasPath, FileMode.Create, FileAccess.Write, FileShare.None, 2 * 1024 * 1024);
            using var bw = new BinaryWriter(fs);

            bw.Write(new byte[] { (byte)'L', (byte)'A', (byte)'S', (byte)'F' });
            bw.Write((ushort)0); bw.Write((ushort)17); bw.Write(new byte[16]);
            bw.Write((byte)1); bw.Write((byte)4);
            bw.Write(PadAscii("SYNTHETIC TEST DATA", 32));
            bw.Write(PadAscii("DroneLiDAR", 32));
            bw.Write((ushort)(DateTime.UtcNow.DayOfYear));
            bw.Write((ushort)DateTime.UtcNow.Year);
            bw.Write((ushort)375);
            bw.Write(pointDataOffset);
            bw.Write((uint)1);
            bw.Write((byte)7);
            bw.Write((ushort)36);
            bw.Write((uint)0);
            bw.Write(new byte[20]);
            bw.Write(scale); bw.Write(scale); bw.Write(scale);
            bw.Write(geoShiftX); bw.Write(geoShiftY); bw.Write(geoShiftZ);
            bw.Write(maxX); bw.Write(minX);
            bw.Write(maxY); bw.Write(minY);
            bw.Write(maxZ); bw.Write(minZ);
            bw.Write((ulong)0);
            bw.Write((ulong)0);
            bw.Write((uint)0);
            bw.Write((ulong)totalPoints);
            for (int k = 0; k < 15; k++) bw.Write(returnCounts[k]);

            bw.Write((ushort)0);
            bw.Write(PadAscii("LASF_Projection", 16));
            bw.Write((ushort)2112);
            bw.Write((ushort)wktBytes.Length);
            bw.Write(PadAscii("Synthetic local CRS WKT", 32));
            bw.Write(wktBytes);

            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                bw.Write((int)Math.Round(pt.X / scale));
                bw.Write((int)Math.Round(pt.Y / scale));
                bw.Write((int)Math.Round(pt.Z / scale));
                bw.Write((ushort)1400);
                bw.Write(pt.ReturnInfo);
                bw.Write((byte)0);
                bw.Write(pt.Classification);
                bw.Write((byte)0);
                bw.Write((short)0);
                bw.Write((ushort)0);
                bw.Write(1_000_000.0 + i * 0.0002);
                bw.Write((ushort)(pt.R << 8));
                bw.Write((ushort)(pt.G << 8));
                bw.Write((ushort)(pt.B << 8));
            }
        }

        private static byte[] PadAscii(string text, int length)
        {
            var buf = new byte[length];
            byte[] src = Encoding.ASCII.GetBytes(text);
            Array.Copy(src, buf, Math.Min(src.Length, length));
            return buf;
        }

        static void ShowWebVisualizer()
        {
            activeResultsDirectory = FindLatestResultsDirectory();
            if (activeResultsDirectory == null)
            {
                Console.WriteLine("[ОШИБКА] Нет завершённого расчёта. Сначала выполните пункт 5.");
                PauseBrief();
                return;
            }

            int port = 8080;
            HttpListener? listener = null;
            string url = "";

            for (; port <= 8085; port++)
            {
                url = $"http://127.0.0.1:{port}/";
                listener = new HttpListener();
                listener.Prefixes.Add(url);
                try
                {
                    listener.Start();
                    break;
                }
                catch
                {
                    listener.Close();
                    listener = null;
                }
            }

            if (listener == null)
            {
                Console.WriteLine("[ОШИБКА] Не удалось занять порт 8080–8085. Закройте другой визуализатор.");
                PauseBrief();
                return;
            }

            try
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }

                Console.WriteLine($"\n[СЕРВЕР] 3D-визуализатор: {url}");
                Console.WriteLine($"[ДАННЫЕ] {activeResultsDirectory}");
                Console.WriteLine("Нажмите клавишу для остановки сервера...");

                bool isRunning = true;
                var activeListener = listener;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    while (isRunning && activeListener.IsListening)
                    {
                        HttpListenerContext? ctx = null;
                        try
                        {
                            ctx = activeListener.GetContext();
                            string localPath = ctx.Request.Url?.LocalPath.TrimStart('/') ?? string.Empty;
                            if (string.IsNullOrEmpty(localPath)) localPath = "viewer.html";

                            string? fullPath = ResolveStaticFile(localPath);
                            ctx.Response.AddHeader("X-Content-Type-Options", "nosniff");
                            ctx.Response.AddHeader("Referrer-Policy", "no-referrer");
                            ctx.Response.AddHeader(
                                "Content-Security-Policy",
                                "default-src 'self'; script-src 'self' 'nonce-dronelidar-viewer'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:");

                            if (fullPath != null)
                            {
                                var fileInfo = new FileInfo(fullPath);
                                ctx.Response.ContentType = ContentTypeFor(fullPath);
                                ctx.Response.ContentLength64 = fileInfo.Length;
                                using var input = new FileStream(
                                    fullPath,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.Read,
                                    1024 * 1024,
                                    FileOptions.SequentialScan);
                                input.CopyTo(ctx.Response.OutputStream, 1024 * 1024);
                            }
                            else
                            {
                                ctx.Response.StatusCode = 404;
                            }
                        }
                        catch (HttpListenerException) when (!isRunning || !activeListener.IsListening) { }
                        catch
                        {
                            if (ctx != null)
                            {
                                try { ctx.Response.StatusCode = 500; } catch { }
                            }
                        }
                        finally
                        {
                            if (ctx != null)
                            {
                                try { ctx.Response.OutputStream.Close(); } catch { }
                            }
                        }
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
            finally
            {
                listener.Close();
            }
        }

        static string? ResolveStaticFile(string localPath)
        {
            localPath = Uri.UnescapeDataString(localPath)
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(localPath)
                || localPath.Contains("..", StringComparison.Ordinal)
                || Path.IsPathRooted(localPath))
            {
                return null;
            }

            string extension = Path.GetExtension(localPath).ToLowerInvariant();
            string[] allowedExtensions = { ".html", ".js", ".css", ".json", ".csv", ".bin" };
            if (!allowedExtensions.Contains(extension, StringComparer.Ordinal)) return null;

            string? latestResults = FindLatestResultsDirectory();
            string[] roots = latestResults == null
                ? new[] { AppContext.BaseDirectory }
                : new[] { latestResults, AppContext.BaseDirectory };

            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string canonicalRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(Path.Combine(canonicalRoot, localPath));
                if (full.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(full))
                {
                    return full;
                }
            }
            return null;
        }

        static string ContentTypeFor(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "application/javascript; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".csv" => "text/csv; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                _ => "application/octet-stream"
            };
        }

        static bool TryParseFloat(string? raw, out float value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            raw = raw.Trim().Replace(',', '.');
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        static void PauseBrief()
        {
            if (!quietMode) Thread.Sleep(1400);
        }

        static void RunMathematicalRegressionTests(string rootDir)
        {
            string testDir = Path.Combine(rootDir, "math");
            Directory.CreateDirectory(testDir);

            var baseCloud = new List<Point3D>();
            var currentCloud = new List<Point3D>();
            for (int x = 0; x < 12; x++)
            {
                for (int y = 0; y < 12; y++)
                {
                    baseCloud.Add(new Point3D(x, y, 10, 120, 120, 120, Point3D.ClassGround));
                    float z = x >= 3 && x < 7 && y >= 3 && y < 7 ? 9 : 10;
                    currentCloud.Add(new Point3D(x, y, z, 120, 120, 120, Point3D.ClassGround));
                }
            }

            string csvPath = Path.Combine(testDir, "analytic.csv");
            VolumeBalance analytic = MathApparatus.CalculateVolumeBalance(
                baseCloud, currentCloud, 1.0f, null, 1.0f, 1.0f, true, csvPath);
            if (Math.Abs(analytic.CutVolume - 16.0) > 0.001 || Math.Abs(analytic.FillVolume) > 0.001)
                throw new InvalidOperationException(
                    $"Аналитический объём неверен: cut={analytic.CutVolume:F3}, fill={analytic.FillVolume:F3}");

            double csvVolume = 0;
            foreach (string line in File.ReadLines(csvPath).Skip(1))
            {
                string[] columns = line.Split(',');
                if (columns.Length >= 6)
                    csvVolume += double.Parse(columns[5], CultureInfo.InvariantCulture);
            }
            if (Math.Abs(csvVolume - analytic.CutVolume) > 0.001)
                throw new InvalidOperationException("Сумма CSV не совпадает с рассчитанным объёмом.");

            var boundary = new BoundaryPolygon();
            boundary.Vertices.Add(new Vector2(-2, -1));
            boundary.Vertices.Add(new Vector2(3, -1));
            boundary.Vertices.Add(new Vector2(3, 4));
            boundary.Vertices.Add(new Vector2(-2, 4));
            boundary.Save(testDir);
            BoundaryPolygon? loadedBoundary = BoundaryPolygon.TryLoad(testDir);
            if (loadedBoundary == null
                || loadedBoundary.Vertices.Count != 4
                || Vector2.Distance(loadedBoundary.Vertices[2], new Vector2(3, 4)) > 0.0001f)
            {
                throw new InvalidOperationException("AOI не проходит JSON round-trip.");
            }

            var longAoi = new BoundaryPolygon
            {
                Vertices = new List<Vector2>
                {
                    new Vector2(0, 0),
                    new Vector2(1_000_000, 0),
                    new Vector2(1_000_000, 1),
                    new Vector2(0, 1)
                }
            };
            var sparseGround = new List<Point3D>
            {
                new Point3D(0.5f, 0.5f, 1, 100, 100, 100, Point3D.ClassGround)
            };
            VolumeBalance adaptedDem = MathApparatus.CalculateVolumeBalance(
                sparseGround, sparseGround, 0.1f, longAoi, 1.0f, 1.0f);
            if (!float.IsFinite(adaptedDem.DemCellSize) || adaptedDem.DemCellSize < 9.9f)
                throw new InvalidOperationException("DEM не адаптировал шаг к большой расчётной зоне.");

            var voxelInput = new List<Point3D>();
            var random = new Random(1234);
            for (int i = 0; i < 4000; i++)
            {
                voxelInput.Add(new Point3D(
                    (float)random.NextDouble() * 5,
                    (float)random.NextDouble() * 5,
                    (float)random.NextDouble() * 2,
                    (byte)random.Next(40, 220),
                    (byte)random.Next(40, 220),
                    (byte)random.Next(40, 220),
                    Point3D.ClassGround));
            }
            var forward = LidarPipeline.VoxelFilter(voxelInput, 0.25f);
            voxelInput.Reverse();
            var reverse = LidarPipeline.VoxelFilter(voxelInput, 0.25f);
            if (forward.Count != reverse.Count
                || Vector3.Distance(SumCoordinates(forward), SumCoordinates(reverse)) > 0.001f)
            {
                throw new InvalidOperationException("Voxel filter зависит от порядка входных точек.");
            }

            var mixedVoxel = new List<Point3D>
            {
                new Point3D(1.001f, 2.001f, 0.001f, 120, 110, 90, Point3D.ClassGround),
                new Point3D(1.002f, 2.002f, 0.002f, 240, 170, 10, Point3D.ClassMachinery)
            };
            var separated = LidarPipeline.VoxelizeForPipeline(mixedVoxel, 0.08f);
            if (separated.Raw.Count != 1
                || separated.Raw[0].Classification != Point3D.ClassMachinery
                || separated.Clean.Count != 1
                || separated.Clean[0].Classification != Point3D.ClassGround
                || separated.Analysis.Count != 1
                || separated.Analysis[0].Classification != Point3D.ClassGround)
            {
                throw new InvalidOperationException("Разделение RAW/CLEAN/DEM удаляет землю вместе с техникой.");
            }

            var target = new List<Vector3>();
            random = new Random(77);
            for (int i = 0; i < 1500; i++)
            {
                target.Add(new Vector3(
                    (float)random.NextDouble() * 20 - 10,
                    (float)random.NextDouble() * 13 - 6,
                    (float)random.NextDouble() * 4
                        + MathF.Sin((float)random.NextDouble() * 3)));
            }
            Matrix4x4 applied = Matrix4x4.CreateFromYawPitchRoll(
                0.006f, -0.004f, 0.003f);
            applied.Translation = new Vector3(0.08f, -0.05f, 0.03f);
            var source = target.Select(point => Vector3.Transform(point, applied)).ToList();
            var (transform, _, reliable) = MathApparatus.AlignCloudsICP(
                source, target, 12, maxAllowedShift: 0.35f);
            if (!reliable)
                throw new InvalidOperationException("ICP отклонил корректное малое 6-DoF преобразование.");

            double alignmentError = 0;
            for (int i = 0; i < source.Count; i++)
                alignmentError += Vector3.Distance(Vector3.Transform(source[i], transform), target[i]);
            alignmentError /= source.Count;
            if (alignmentError > 0.03)
                throw new InvalidOperationException($"ICP дал среднюю ошибку {alignmentError:F4} м.");

            var tooSmall = target.Take(20).ToList();
            if (MathApparatus.AlignCloudsICP(tooSmall, tooSmall).IsReliable)
                throw new InvalidOperationException("ICP принял набор с недостаточным числом соответствий.");

            var names = new List<string> { "epoch_10.las", "epoch_2.las", "epoch_1.las" };
            names.Sort(CompareFileNamesNaturally);
            if (!names.SequenceEqual(new[] { "epoch_1.las", "epoch_2.las", "epoch_10.las" }))
                throw new InvalidOperationException("Естественная сортировка эпох работает неверно.");
        }

        static Vector3 SumCoordinates(List<Point3D> points)
        {
            Vector3 sum = Vector3.Zero;
            foreach (var point in points) sum += new Vector3(point.X, point.Y, point.Z);
            return sum;
        }

        static void RunLasReaderFixtures(string rootDir)
        {
            var formats = new (byte Minor, byte Format, ushort RecordLength, int RgbOffset)[]
            {
                (2, 0, 20, -1), (2, 1, 28, -1), (2, 2, 26, 20), (2, 3, 34, 28),
                (3, 4, 57, -1), (3, 5, 63, 28),
                (4, 6, 30, -1), (4, 7, 36, 30), (4, 8, 38, 30),
                (4, 9, 59, -1), (4, 10, 67, 30)
            };
            foreach (var fixture in formats)
            {
                string dir = Path.Combine(rootDir, $"las_1_{fixture.Minor}_fmt_{fixture.Format}");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "fixture.las");
                WriteMinimalLasFixture(
                    path,
                    fixture.Minor,
                    fixture.Format,
                    fixture.RecordLength,
                    fixture.RgbOffset);
                LidarPipeline.CurrentProject = new ProjectConfig();
                List<Point3D> loaded = LidarPipeline.FastLoadLasFile(path, dir, 100);
                byte expectedR = fixture.RgbOffset >= 0 ? (byte)0xAB : (byte)(1000 >> 8);
                byte expectedG = fixture.RgbOffset >= 0 ? (byte)0x6C : expectedR;
                byte expectedB = fixture.RgbOffset >= 0 ? (byte)0x2D : expectedR;
                if (loaded.Count != 1
                    || loaded[0].Classification != Point3D.ClassGround
                    || loaded[0].R != expectedR
                    || loaded[0].G != expectedG
                    || loaded[0].B != expectedB)
                {
                    throw new InvalidOperationException(
                        $"LAS 1.{fixture.Minor} format {fixture.Format}: fixture прочитан неверно.");
                }
            }
        }

        static void RunLazReaderFixture(string rootDir)
        {
            string dir = Path.Combine(rootDir, "laz_roundtrip");
            Directory.CreateDirectory(dir);
            string lasPath = Path.Combine(dir, "epoch_2.las");
            string lazPath = Path.Combine(dir, "epoch_1.laz");
            WriteMinimalLasFixture(lasPath, 4, 7, 36, 30);

            using (var source = new LasReader(lasPath))
            using (var destination = new LazWriter(lazPath))
            {
                var header = new HeaderBlockBuilder(source.Header);
                header.SetCompressed();
                destination.Write(header.HeaderBlock, source.VariableLengthRecords);
                for (ulong i = 0; i < source.Header.NumberOfPointRecords; i++)
                {
                    LasPointSpan point = source.ReadPointDataRecord();
                    if (point.PointDataRecord == null)
                        throw new InvalidDataException("LAS fixture неожиданно оборван при создании LAZ.");
                    destination.Write(point.PointDataRecord, point.ExtraBytes);
                }
                foreach (ExtendedVariableLengthRecord record in source.ExtendedVariableLengthRecords)
                    destination.Write(record);
            }

            LidarPipeline.CurrentProject = new ProjectConfig();
            List<Point3D> loaded = LidarPipeline.FastLoadLazFile(lazPath, dir, 100);
            if (loaded.Count != 1
                || loaded[0].Classification != Point3D.ClassGround
                || loaded[0].R != 0xAB
                || loaded[0].G != 0x6C
                || loaded[0].B != 0x2D
                || LidarPipeline.LastMetadata.SourceFormat != "LAZ"
                || !LidarPipeline.LastMetadata.IsCompressed)
            {
                throw new InvalidOperationException("LAZ roundtrip: координаты, классы или RGB прочитаны неверно.");
            }

            var scanned = ScanWorkDirectory(dir).files;
            if (scanned.Count != 2
                || Path.GetFileName(scanned[0]) != "epoch_1.laz"
                || Path.GetFileName(scanned[1]) != "epoch_2.las")
            {
                throw new InvalidOperationException("Совместное сканирование и сортировка LAS/LAZ работают неверно.");
            }
        }

        static void WriteMinimalLasFixture(
            string path,
            byte versionMinor,
            byte pointFormat,
            ushort recordLength,
            int rgbOffset)
        {
            bool modern = pointFormat >= 6;
            ushort headerSize = modern ? (ushort)375 : versionMinor >= 3 ? (ushort)235 : (ushort)227;
            const uint pointCount = 2;
            const double scale = 0.01;
            const double offsetX = 100;
            const double offsetY = 200;
            const double offsetZ = 10;

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("LASF"));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(new byte[16]);
            writer.Write((byte)1);
            writer.Write(versionMinor);
            writer.Write(PadAscii("SELFTEST", 32));
            writer.Write(PadAscii("DroneLiDAR", 32));
            writer.Write((ushort)1);
            writer.Write((ushort)2026);
            writer.Write(headerSize);
            writer.Write((uint)headerSize);
            writer.Write((uint)0);
            writer.Write(pointFormat);
            writer.Write(recordLength);
            writer.Write(modern ? 0u : pointCount);
            for (int i = 0; i < 5; i++) writer.Write(i == 0 && !modern ? pointCount : 0u);
            writer.Write(scale); writer.Write(scale); writer.Write(scale);
            writer.Write(offsetX); writer.Write(offsetY); writer.Write(offsetZ);
            writer.Write(111.0); writer.Write(110.0);
            writer.Write(221.0); writer.Write(220.0);
            writer.Write(16.0); writer.Write(15.0);

            if (modern)
            {
                writer.Write((ulong)0);
                writer.Write((ulong)0);
                writer.Write((uint)0);
                writer.Write((ulong)pointCount);
                for (int i = 0; i < 15; i++) writer.Write(i == 0 ? (ulong)pointCount : 0UL);
            }
            else if (versionMinor >= 3)
            {
                writer.Write((ulong)0);
            }

            WriteFixturePoint(writer, pointFormat, recordLength, rgbOffset, 1000, 2000, 500, withheld: false);
            WriteFixturePoint(writer, pointFormat, recordLength, rgbOffset, 1100, 2100, 600, withheld: true);
        }

        static void WriteFixturePoint(
            BinaryWriter writer,
            byte pointFormat,
            ushort recordLength,
            int rgbOffset,
            int x,
            int y,
            int z,
            bool withheld)
        {
            bool modern = pointFormat >= 6;
            byte[] record = new byte[recordLength];
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0, 4), x);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4, 4), y);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8, 4), z);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12, 2), 1000);
            record[14] = modern ? (byte)0x11 : (byte)0x09;
            if (modern)
            {
                record[15] = withheld ? (byte)0x04 : (byte)0;
                record[16] = Point3D.ClassGround;
            }
            else
            {
                record[15] = withheld
                    ? (byte)(Point3D.ClassGround | 0x80)
                    : Point3D.ClassGround;
            }
            if (rgbOffset >= 0)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(rgbOffset, 2), 0xAB00);
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(rgbOffset + 2, 2), 0x6C00);
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(rgbOffset + 4, 2), 0x2D00);
            }
            writer.Write(record);
        }

        static int RunSelfTest()
        {
            quietMode = true;
            string rootDir = Path.Combine(Path.GetTempPath(), "dronelidar_selftest_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(rootDir);
            string previous = workDirectory;
            string? previousActiveResults = activeResultsDirectory;
            voxelStep = 0.08f;

            try
            {
                Console.WriteLine("=== SELFTEST DroneLiDAR: все сцены ===");
                RunMathematicalRegressionTests(rootDir);
                RunLasReaderFixtures(rootDir);
                RunLazReaderFixture(rootDir);

                foreach (SceneType scene in Enum.GetValues<SceneType>())
                {
                    string dir = Path.Combine(rootDir, scene.ToString());
                    Directory.CreateDirectory(dir);
                    workDirectory = dir;
                    activeResultsDirectory = null;
                    EnsureWorkspaceLayout();
                    selectedScene = scene;
                    LidarPipeline.CurrentProject = new ProjectConfig();

                    Console.WriteLine($"\n=== СЦЕНА: {SceneLabel(scene)} ===");
                    GenerateCustomEpochs(3);
                    var files = ScanWorkDirectory(dir).files;
                    if (files.Count != 4)
                        throw new InvalidOperationException($"{scene}: ожидалось 4 LAS, получено {files.Count}");

                    var reload = LidarPipeline.LoadScanAuto(files[0], dir);
                    if (reload.Count < 10000)
                        throw new InvalidOperationException($"{scene}: LAS roundtrip содержит только {reload.Count} точек");
                    if (!LidarPipeline.LastMetadata.IsSynthetic
                        || string.IsNullOrWhiteSpace(LidarPipeline.LastMetadata.CoordinateSystemWkt))
                    {
                        throw new InvalidOperationException($"{scene}: synthetic/CRS metadata LAS не прочитаны.");
                    }

                    int sourceMachinery = 0;
                    int elevatedStoragePoints = 0;
                    for (int i = 0; i < reload.Count; i++)
                    {
                        if (reload[i].Classification == Point3D.ClassMachinery) sourceMachinery++;
                        if (scene == SceneType.Warehouse
                            && reload[i].Classification == Point3D.ClassMaterial
                            && reload[i].Z > 0.50f)
                        {
                            elevatedStoragePoints++;
                        }
                    }
                    if (sourceMachinery == 0)
                        throw new InvalidOperationException($"{scene}: генератор не создал маркированную технику");
                    if (scene == SceneType.Warehouse && elevatedStoragePoints < 10000)
                        throw new InvalidOperationException("Warehouse: прямоугольные штабеля не сформированы");

                    // Отдельно проверяем геометрическую/RGB-эвристику на полностью
                    // неклассифицированном облаке, как при сыром экспорте LAS.
                    var unclassified = new List<Point3D>(reload.Count);
                    for (int i = 0; i < reload.Count; i++)
                    {
                        var pt = reload[i];
                        unclassified.Add(new Point3D(
                            pt.X, pt.Y, pt.Z, pt.R, pt.G, pt.B,
                            Point3D.ClassUnclassified, pt.ReturnInfo));
                    }
                    var inferred = MathApparatus.ClassifyGroundAndObjects(
                        unclassified, gridCellSize: 0.5f, heightThreshold: 0.25f);
                    int machineryDetected = 0;
                    int machineryFalsePositive = 0;
                    for (int i = 0; i < inferred.Count; i++)
                    {
                        if (inferred[i].Classification != Point3D.ClassMachinery) continue;
                        if (reload[i].Classification == Point3D.ClassMachinery)
                            machineryDetected++;
                        else
                            machineryFalsePositive++;
                    }
                    double machineryRecall = machineryDetected / (double)sourceMachinery;
                    double machineryFalsePositiveRate = machineryFalsePositive
                        / (double)Math.Max(1, reload.Count - sourceMachinery);
                    if (machineryRecall < 0.25)
                        throw new InvalidOperationException(
                            $"{scene}: распознано только {machineryRecall:P1} немаркированной техники");
                    if (machineryFalsePositiveRate > 0.01)
                        throw new InvalidOperationException(
                            $"{scene}: ложная классификация техники {machineryFalsePositiveRate:P2}");

                    if (scene == SceneType.Quarry)
                    {
                        RunNDPipeline(new List<string> { files[0] }, singleScanPreview: true);
                        string previewDir = activeResultsDirectory
                            ?? throw new InvalidOperationException("Одиночный предпросмотр не зарегистрировал результат.");
                        using var previewDoc = JsonDocument.Parse(
                            File.ReadAllText(Path.Combine(previewDir, "meta.json")));
                        JsonElement previewEpoch = previewDoc.RootElement.GetProperty("Epochs")[0];
                        if (previewDoc.RootElement.GetProperty("RunMode").GetString() != "SINGLE_SCAN_PREVIEW"
                            || previewDoc.RootElement.GetProperty("Epochs").GetArrayLength() != 1
                            || previewEpoch.GetProperty("SourcePointCount").GetInt32()
                                != previewEpoch.GetProperty("Views")[0].GetProperty("PointCount").GetInt32()
                            || File.Exists(Path.Combine(previewDir, "project.json"))
                            || File.Exists(Path.Combine(previewDir, "boundary.json"))
                            || !File.ReadAllText(Path.Combine(previewDir, "report.html")).Contains(
                                "ВИЗУАЛЬНАЯ ПРОВЕРКА ОДИНОЧНОГО", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("Режим одиночного RAW/CLEAN-предпросмотра сформирован неверно.");
                        }
                        activeResultsDirectory = null;
                    }

                    RunNDPipeline(files);

                    string resultDir = activeResultsDirectory
                        ?? throw new InvalidOperationException($"{scene}: каталог результата не зарегистрирован");
                    if (!Path.GetDirectoryName(resultDir)!.Equals(
                        Path.Combine(dir, "results"), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"{scene}: результат записан вне results/<RunId>");
                    }
                    string metaPath = Path.Combine(resultDir, "meta.json");
                    string reportPath = Path.Combine(resultDir, "report.html");
                    string manifestPath = Path.Combine(resultDir, "manifest.json");
                    if (!File.Exists(metaPath) || !File.Exists(reportPath) || !File.Exists(manifestPath))
                        throw new InvalidOperationException($"{scene}: нет meta.json, report.html или manifest.json");
                    if (!File.Exists(Path.Combine(dir, "results", "latest.json")))
                        throw new InvalidOperationException($"{scene}: нет указателя results/latest.json");
                    if (Directory.GetFiles(dir, "*.bin").Length != 0
                        || File.Exists(Path.Combine(dir, "meta.json"))
                        || File.Exists(Path.Combine(dir, "report.html")))
                    {
                        throw new InvalidOperationException($"{scene}: результаты загрязнили корень проекта");
                    }

                    using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                    using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    string metaRunId = doc.RootElement.GetProperty("RunId").GetString()!;
                    string manifestRunId = manifestDoc.RootElement.GetProperty("RunId").GetString()!;
                    if (!metaRunId.Equals(manifestRunId, StringComparison.Ordinal)
                        || manifestDoc.RootElement.GetProperty("Inputs").GetArrayLength() != files.Count)
                    {
                        throw new InvalidOperationException($"{scene}: manifest не соответствует meta/input.");
                    }
                    foreach (var input in manifestDoc.RootElement.GetProperty("Inputs").EnumerateArray())
                    {
                        string hash = input.GetProperty("Sha256").GetString() ?? string.Empty;
                        if (hash.Length != 64)
                            throw new InvalidOperationException($"{scene}: некорректный SHA-256 в manifest.");
                        double samplingPercent = input.GetProperty("SamplingPercent").GetDouble();
                        if (samplingPercent <= 0 || samplingPercent > 100)
                            throw new InvalidOperationException($"{scene}: некорректный процент выборки в manifest.");
                    }
                    if (File.ReadAllText(reportPath).Contains(
                        "Составлен в соответствии", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"{scene}: отчёт содержит неподтверждённое нормативное заявление.");
                    }
                    var boundary = doc.RootElement.GetProperty("Boundary");
                    if (boundary.GetArrayLength() < 3
                        || !boundary[0].TryGetProperty("X", out _)
                        || !boundary[0].TryGetProperty("Y", out _))
                    {
                        throw new InvalidOperationException($"{scene}: AOI не сериализован координатами X/Y");
                    }
                    var epochs = doc.RootElement.GetProperty("Epochs");
                    if (epochs.GetArrayLength() != files.Count)
                        throw new InvalidOperationException($"{scene}: число эпох в meta.json не совпадает с LAS");

                    double previousCut = -1.0;
                    for (int i = 0; i < epochs.GetArrayLength(); i++)
                    {
                        var epoch = epochs[i];
                        double cut = epoch.GetProperty("CutVolume").GetDouble();
                        double? rmse = epoch.GetProperty("RmsStableZone").ValueKind == JsonValueKind.Number
                            ? epoch.GetProperty("RmsStableZone").GetDouble()
                            : null;
                        double coverage = epoch.GetProperty("CoveragePercent").GetDouble();
                        int removed = epoch.GetProperty("MachineryRemoved").GetInt32();
                        string registrationMode = epoch.GetProperty("RegistrationMode").GetString() ?? string.Empty;
                        if (i == 0 && registrationMode != "BASE")
                            throw new InvalidOperationException($"{scene}: базовая эпоха имеет неверный режим регистрации");
                        if (i > 0 && registrationMode != "ICP_STABLE_ZONE")
                            throw new InvalidOperationException($"{scene}, эпоха {i}: ICP не применён на контрольной сцене");
                        var views = epoch.GetProperty("Views");
                        if (views.GetArrayLength() != 2)
                            throw new InvalidOperationException($"{scene}, эпоха {i}: ожидались слоты RAW и CLEAN");
                        foreach (JsonElement view in views.EnumerateArray())
                        {
                            foreach (JsonElement node in view.GetProperty("Octree").GetProperty("Nodes").EnumerateArray())
                            {
                                if (node.GetProperty("Level").GetInt32() == 1
                                    && node.GetProperty("PointCount").GetInt32() > 60_000)
                                {
                                    throw new InvalidOperationException(
                                        $"{scene}, эпоха {i}: viewer-узел превышает лимит 60 000 точек");
                                }
                            }
                        }
                        string rawPath = Path.Combine(resultDir, views[0].GetProperty("BinFile").GetString()!);
                        string cleanPath = Path.Combine(resultDir, views[1].GetProperty("BinFile").GetString()!);

                        if (removed == 0)
                            throw new InvalidOperationException($"{scene}, эпоха {i}: техника не распознана");
                        if (CountBinaryClass(rawPath, Point3D.ClassMachinery) == 0)
                            throw new InvalidOperationException($"{scene}, эпоха {i}: в RAW нет техники");
                        if (CountBinaryClass(cleanPath, Point3D.ClassMachinery) != 0)
                            throw new InvalidOperationException($"{scene}, эпоха {i}: техника попала в viewer BIN");
                        if (scene == SceneType.Warehouse
                            && CountBinaryClass(cleanPath, Point3D.ClassBuilding) == 0)
                        {
                            throw new InvalidOperationException("Warehouse: ангары ошибочно удалены из CLEAN");
                        }
                        if (i == 0 && Math.Abs(cut) > 0.01)
                            throw new InvalidOperationException($"{scene}: у базиса CutVolume={cut}, должно быть 0");
                        if (i > 0 && cut + 0.5 < previousCut)
                            throw new InvalidOperationException($"{scene}: выемка не монотонна на эпохе {i}");
                        if (i > 0 && rmse.HasValue && rmse.Value > 0.08)
                            throw new InvalidOperationException($"{scene}, эпоха {i}: RMS {rmse:F3} м превышает 0.08 м");
                        if (i > 0 && coverage < 90.0)
                            throw new InvalidOperationException($"{scene}, эпоха {i}: покрытие DEM {coverage:F1}% ниже 90%");

                        previousCut = cut;
                    }

                    double lastCut = epochs[epochs.GetArrayLength() - 1].GetProperty("CutVolume").GetDouble();
                    if (lastCut < 10.0)
                        throw new InvalidOperationException($"{scene}: выемка последней эпохи {lastCut} м³ слишком мала");

                    Console.WriteLine(
                        $"SCENE OK: {scene} | техника удалена | "
                        + $"эвристика recall {machineryRecall:P0}, FP {machineryFalsePositiveRate:P2} | "
                        + $"эпох {files.Count} | итог {lastCut:F2} м³");
                }

                Console.WriteLine("\nSELFTEST OK: математика + LAS 1.2–1.4 formats 0–10 + LAZ roundtrip + одиночный RAW/CLEAN + Quarry + Warehouse + Crater");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL: " + ex);
                return 1;
            }
            finally
            {
                workDirectory = previous;
                activeResultsDirectory = previousActiveResults;
                try { Directory.Delete(rootDir, true); } catch { }
            }
        }

        static int RunStressTest(int pointCount)
        {
            quietMode = true;
            const float spacing = 0.25f;
            const float excavationRadius = 40.0f;
            int side = (int)Math.Ceiling(Math.Sqrt(pointCount));
            var baseline = new List<Point3D>(pointCount);
            var current = new List<Point3D>(pointCount);
            var stopwatch = Stopwatch.StartNew();
            string stressDirectory = Path.Combine(
                Path.GetTempPath(),
                "dronelidar_stress_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(stressDirectory);

            try
            {
                for (int i = 0; i < pointCount; i++)
                {
                    int xIndex = i % side;
                    int yIndex = i / side;
                    float x = (xIndex - side * 0.5f) * spacing;
                    float y = (yIndex - side * 0.5f) * spacing;
                    float terrain = 0.02f * MathF.Sin(x * 0.03f) * MathF.Cos(y * 0.03f);
                    bool excavated = x * x + y * y <= excavationRadius * excavationRadius;
                    baseline.Add(new Point3D(x, y, terrain, 150, 135, 105, Point3D.ClassGround));
                    current.Add(new Point3D(x, y, terrain - (excavated ? 1.0f : 0.0f), 150, 135, 105, Point3D.ClassGround));
                }

                string lasPath = Path.Combine(stressDirectory, "stress.las");
                WriteLas14File(lasPath, baseline);
                LidarPipeline.CurrentProject = new ProjectConfig();
                List<Point3D> sampledLas = LidarPipeline.FastLoadLasFile(
                    lasPath,
                    stressDirectory,
                    Math.Min(1_000_000, pointCount));
                if (sampledLas.Count == 0 || sampledLas.Count > Math.Min(1_000_000, pointCount))
                    throw new InvalidOperationException("Потоковое чтение LAS нарушило лимит выборки.");
                sampledLas = null!;

                baseline = MathApparatus.ClassifyGroundAndObjects(baseline);
                current = MathApparatus.ClassifyGroundAndObjects(current);
                var baselineSets = LidarPipeline.VoxelizeForPipeline(baseline, 0.08f);
                var currentSets = LidarPipeline.VoxelizeForPipeline(current, 0.08f);
                var aoi = BoundaryPolygon.CreateCircle(excavationRadius + 1.0f, 64);
                VolumeBalance result = MathApparatus.CalculateVolumeBalance(
                    baselineSets.Analysis,
                    currentSets.Analysis,
                    spacing,
                    aoi,
                    1.65f,
                    1.20f,
                    filterMachinery: false);
                stopwatch.Stop();

                double expected = Math.PI * excavationRadius * excavationRadius;
                double relativeError = Math.Abs(result.CutVolume - expected) / expected;
                long workingSet = Process.GetCurrentProcess().PeakWorkingSet64;
                Console.WriteLine(
                    $"STRESS: {pointCount:N0} точек/эпоха | {stopwatch.Elapsed.TotalSeconds:F2} с | "
                    + $"peak working set {workingSet / (1024.0 * 1024.0):F0} МБ | "
                    + $"объём {result.CutVolume:F2} м³ | ошибка {relativeError:P2}");

                if (relativeError > 0.02 || result.CoveragePercent < 99.0)
                {
                    Console.WriteLine("STRESS FAIL: объём или покрытие вышли за допустимую границу.");
                    return 1;
                }
                Console.WriteLine("STRESS OK");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("STRESS FAIL: " + ex.Message);
                return 1;
            }
            finally
            {
                try { Directory.Delete(stressDirectory, true); } catch { }
            }
        }

        static int CountBinaryClass(string path, byte classification)
        {
            if (!File.Exists(path)) return -1;
            byte[] bytes = File.ReadAllBytes(path);
            int count = 0;
            for (int offset = 15; offset < bytes.Length; offset += 16)
            {
                if (bytes[offset] == classification) count++;
            }
            return count;
        }
    }
}
