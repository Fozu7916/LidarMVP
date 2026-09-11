using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using LidarProcessorMVP;

namespace LidarRunner
{
    public enum SceneType
    {
        Quarry,    // Карьерная куча/насыпь
        Warehouse, // Склад коробок/паллет
        Crater     // Котлован/кратер
    }

    class Program
    {
        private static string workDirectory = Directory.GetCurrentDirectory();
        private static float voxelStep = 0.15f;
        private static SceneType selectedScene = SceneType.Quarry;

        static void Main(string[] args)
        {
            while (true)
            {
                var (files, isLas) = ScanWorkDirectory(workDirectory);

                Console.Clear();
                Console.WriteLine("==========================================================");
                Console.WriteLine("   ЛИДАР-БИМ: МАРКШЕЙДЕРСКИЙ КОНТРОЛЬ ОБЪЕМОВ (ASPRS)     ");
                Console.WriteLine("==========================================================");
                Console.WriteLine($" [ Каталог ]: {workDirectory}");
                Console.WriteLine($" [ Текущая сцена ]: {selectedScene}");

                if (files.Count > 0)
                {
                    Console.WriteLine($" [ Формат файлов ]: {(isLas ? "LAS (Промышленный)" : "PLY")}");
                    Console.WriteLine($" [ Сканов в папке]: {files.Count} шт.");
                }
                else
                {
                    Console.WriteLine($" [ Статус ]: Сканы не обнаружены. Готов к автогенерации.");
                }

                Console.WriteLine("----------------------------------------------------------");
                Console.WriteLine(" 1. Задать путь к рабочей папке");
                Console.WriteLine(" 2. Настроить шаг воксельной сетки");
                Console.WriteLine(" 3. Выбрать сцену (Куча / Склад / Котлован)");
                Console.WriteLine(" 4. Сгенерировать эпохи (ASPRS классы: Земля=2, Техника=64)");
                Console.WriteLine(" 5. [ЗАПУСК] Маркшейдерский расчет Cut&Fill баланса масс");
                Console.WriteLine(" 6. Запустить Web-визуализатор");
                Console.WriteLine(" 7. Выход");
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
                    case ConsoleKey.D7: return;
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
            Console.WriteLine(" 2. Склад паллет и штабелей");
            Console.WriteLine(" 3. Котлован / Кратер выемки");
            Console.Write("Выбор: ");
            var k = Console.ReadKey(true);
            switch (k.Key)
            {
                case ConsoleKey.D1: selectedScene = SceneType.Quarry; break;
                case ConsoleKey.D2: selectedScene = SceneType.Warehouse; break;
                case ConsoleKey.D3: selectedScene = SceneType.Crater; break;
            }
        }

        static void GenerateCustomEpochs()
        {
            Console.Clear();
            Console.WriteLine($"Генерация сканов со стандартом ASPRS для сцены: {selectedScene}");
            Console.Write("Сколько этапов динамики создать после базиса? (по умолчанию 3): ");
            string? input = Console.ReadLine();
            int nStages = 3;
            if (int.TryParse(input, out int parsed) && parsed > 0) nStages = parsed;

            foreach (var f in Directory.GetFiles(workDirectory, "epoch_*.las")) File.Delete(f);
            foreach (var f in Directory.GetFiles(workDirectory, "epoch_*.bin")) File.Delete(f);

            Console.WriteLine("\n[1/3] Этап 1: Сырой скан с техникой...");
            string rawFile = Path.Combine(workDirectory, "epoch_0_raw.las");
            GenerateSyntheticLas(rawFile, selectedScene, 0.0f, true, 0.0f);

            Console.WriteLine("[2/3] Этап 2: Базовый скан (нулевой маркшейдерский горизонт)...");
            string baseFile = Path.Combine(workDirectory, "epoch_1_base.las");
            GenerateSyntheticLas(baseFile, selectedScene, 0.0f, true, 0.01f);

            for (int i = 1; i <= nStages; i++)
            {
                float frac = (float)i / nStages;
                Console.WriteLine($"[3/3] Этап {i + 1}: Шаг выработки {i}/{nStages} ({frac * 100:F0}%)...");
                string stageFile = Path.Combine(workDirectory, $"epoch_{i + 1}_stage_{i}.las");
                GenerateSyntheticLas(stageFile, selectedScene, frac, true, 0.02f * i);
            }

            Console.WriteLine("\n[ГОТОВО] Сканы сгенерированы со спецификацией ASPRS.");
            Console.ReadKey();
        }

        static void RunNDPipeline(List<string> inputFiles)
        {
            Console.WriteLine("\n=== 1. ЗАГРУЗКА И ВЫРАВНИВАНИЕ СКАНОВ ===");

            if (inputFiles.Count == 0)
            {
                Console.WriteLine("[ИНФО] Данных нет. Автогенерация базового сценария...");
                GenerateCustomEpochs();
                inputFiles = ScanWorkDirectory(workDirectory).files;
            }

            Stopwatch globalSw = Stopwatch.StartNew();
            LidarPipeline.HasGlobalOrigin = false;

            List<Vector3>? baseVectors = null;
            List<Point3D>? baseCloud = null;

            var metaEpochs = new List<object>();
            float cellSize = Math.Max(0.1f, voxelStep * 2.0f);

            for (int i = 0; i < inputFiles.Count; i++)
            {
                string currentFile = inputFiles[i];
                string binFile = $"epoch_{i}.bin";

                string stageType = i == 0 ? "RAW_SCAN" : (i == 1 ? "CLEANED_BASE" : "EXCAVATION_PROGRESS");
                string stageTitle = i == 0 ? "Этап 1: До очистки техники" : (i == 1 ? "Этап 2: Нулевой горизонт" : $"Этап {i + 1}: Шаг выработки #{i - 1}");

                Console.WriteLine($"\n=== {stageTitle.ToUpper()} ({Path.GetFileName(currentFile)}) ===");

                Stopwatch readSw = Stopwatch.StartNew();
                var currentPoints = LidarPipeline.LoadScanAuto(currentFile);
                readSw.Stop();
                Console.WriteLine($"  [Чтение]: {readSw.ElapsedMilliseconds} мс ({currentPoints.Count} точек, класс грунта ASPRS)");

                var masterCloud = new List<Point3D>();

                if (baseVectors != null)
                {
                    Stopwatch icpSw = Stopwatch.StartNew();
                    var currentVectors = ExtractVectors(currentPoints);
                    var (icpTransform, error) = MathApparatus.AlignCloudsICP(currentVectors, baseVectors, 10);

                    for (int j = 0; j < currentPoints.Count; j++)
                    {
                        var p = currentPoints[j];
                        var v = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), icpTransform);
                        masterCloud.Add(new Point3D(v.X, v.Y, v.Z, p.R, p.G, p.B, p.Classification));
                    }
                    icpSw.Stop();
                    Console.WriteLine($"  [ICP стабилизация]: {icpSw.ElapsedMilliseconds} мс. Невязка: {error:F4} м");
                }
                else
                {
                    masterCloud.AddRange(currentPoints);
                }

                Stopwatch voxelSw = Stopwatch.StartNew();
                var optimizedCloud = LidarPipeline.VoxelFilter(masterCloud, voxelStep);
                voxelSw.Stop();
                Console.WriteLine($"  [Воксель]: {voxelSw.ElapsedMilliseconds} мс. Точек в памяти: {optimizedCloud.Count}");

                VolumeBalance balance = new VolumeBalance(0, 0);
                Stopwatch volumeSw = Stopwatch.StartNew();

                if (i == 1)
                {
                    baseCloud = optimizedCloud;
                    baseVectors = ExtractVectors(optimizedCloud);
                }
                else if (i > 1 && baseCloud != null)
                {
                    balance = MathApparatus.CalculateVolumeBalance(baseCloud, optimizedCloud, cellSize, onlyGround: false);
                }
                volumeSw.Stop();

                Console.WriteLine($"  [Маркшейдерия]: {volumeSw.ElapsedMilliseconds} мс");
                Console.WriteLine($"  -> ВЫЕМКА (Cut): +{balance.CutVolume:N2} м3 | НАСЫПЬ (Fill): -{balance.FillVolume:N2} м3 | БАЛАНС: {balance.NetVolume:N2} м3");

                LidarPipeline.ExportToBinary(optimizedCloud, Path.Combine(workDirectory, binFile));

                metaEpochs.Add(new
                {
                    Id = i,
                    StageType = stageType,
                    Title = stageTitle,
                    BinFile = binFile,
                    CutVolume = Math.Round(balance.CutVolume, 2),
                    FillVolume = Math.Round(balance.FillVolume, 2),
                    NetVolume = Math.Round(balance.NetVolume, 2)
                });
            }

            var metaData = new
            {
                Scene = selectedScene.ToString(),
                Epochs = metaEpochs
            };

            File.WriteAllText(Path.Combine(workDirectory, "meta.json"), JsonSerializer.Serialize(metaData, new JsonSerializerOptions { WriteIndented = true }));

            globalSw.Stop();
            Console.WriteLine($"\n[ИТОГ] Расчет завершен за {globalSw.ElapsedMilliseconds} мс.");
        }

        static List<Vector3> ExtractVectors(List<Point3D> points)
        {
            var list = new List<Vector3>(points.Count);
            for (int i = 0; i < points.Count; i++) list.Add(new Vector3(points[i].X, points[i].Y, points[i].Z));
            return list;
        }

        public static void GenerateSyntheticLas(string lasPath, SceneType sceneType, float stageFraction, bool includeMachinery, float driftOffset)
        {
            var points = new List<Point3D>(160000);

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

            WriteLasFile(lasPath, points, driftOffset);
        }

        private static void GenerateQuarryPoints(List<Point3D> points, float digFraction)
        {
            float step = 0.12f;
            for (float x = -16.0f; x <= 16.0f; x += step)
            {
                for (float y = -16.0f; y <= 16.0f; y += step)
                {
                    float r = MathF.Sqrt(x * x + y * y);
                    float baseZ = (x * 0.05f) + (y * 0.04f);
                    float z = baseZ;

                    if (r < 10.0f) z += 5.0f * MathF.Cos(r * MathF.PI / 20.0f);

                    if (digFraction > 0.0f)
                    {
                        float pitDist = MathF.Sqrt((x - 2.0f) * (x - 2.0f) + (y + 1.5f) * (y + 1.5f));
                        float pitRadius = 6.0f * digFraction + 1.0f;
                        if (pitDist < pitRadius)
                        {
                            float digDepth = (4.5f * digFraction) * MathF.Cos(pitDist * MathF.PI / (pitRadius * 2.0f));
                            z -= digDepth;
                            if (z < baseZ + 0.2f) z = baseZ + 0.2f;
                        }
                    }

                    z += (MathF.Sin(x * 2.5f) * MathF.Cos(y * 2.5f)) * 0.08f;

                    // По стандарту ASPRS: 2 - Ground
                    byte rC = (byte)(r < 10.0f ? 175 : 120);
                    byte gC = (byte)(r < 10.0f ? 145 : 110);
                    byte bC = (byte)(r < 10.0f ? 100 : 95);

                    points.Add(new Point3D(x, z, y, rC, gC, bC, classification: 2));
                }
            }
        }

        private static void GenerateWarehousePoints(List<Point3D> points, float pickupFraction)
        {
            float step = 0.12f;
            for (float x = -14.0f; x <= 14.0f; x += step)
            {
                for (float y = -14.0f; y <= 14.0f; y += step)
                {
                    byte rC = 160, gC = 165, bC = 175;
                    if (MathF.Abs(x) < 0.25f || MathF.Abs(y) < 0.25f) { rC = 230; gC = 190; bC = 20; }
                    points.Add(new Point3D(x, 0.0f, y, rC, gC, bC, classification: 2)); // Пол = Ground
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
                        // Грузы/паллеты = класс 1 (Unassigned / Goods)
                        AddSolidBox(points, boxX, rowY, 0.0f, 2.2f, 1.8f, currentHeight, 185, 135, 75, classification: 1);
                    }
                }
            }
        }

        private static void GenerateCraterPoints(List<Point3D> points, float digFraction)
        {
            float step = 0.12f;
            for (float x = -16.0f; x <= 16.0f; x += step)
            {
                for (float y = -16.0f; y <= 16.0f; y += step)
                {
                    float r = MathF.Sqrt(x * x + y * y);
                    float z = 3.5f;
                    float craterOuterR = 11.0f;

                    if (r < craterOuterR)
                    {
                        float targetDepth = 7.0f * (0.35f + 0.65f * digFraction);
                        z -= targetDepth * MathF.Cos(r * MathF.PI / (craterOuterR * 2.0f));
                        z += MathF.Sin(r * 3.5f) * 0.25f;
                    }

                    byte rC = (byte)(r >= craterOuterR ? 110 : (z < 0 ? 150 : 90));
                    byte gC = (byte)(r >= craterOuterR ? 130 : (z < 0 ? 115 : 80));
                    byte bC = (byte)(r >= craterOuterR ? 85 : (z < 0 ? 80 : 70));

                    points.Add(new Point3D(x, z, y, rC, gC, bC, classification: 2));
                }
            }
        }

        private static void AddQuarryMachinery(List<Point3D> points)
        {
            // Техника помечается классом 64 (Пользовательский класс ASPRS: Mining Equipment)
            AddSolidBox(points, -6.5f, -5.5f, 1.2f, 3.8f, 2.8f, 2.0f, 245, 175, 15, 64);
            AddSolidBox(points, -6.5f, -5.5f, 0.2f, 4.0f, 3.0f, 1.0f, 45, 45, 45, 64);
            AddSolidBox(points, -5.5f, -4.7f, 3.2f, 1.4f, 1.3f, 1.5f, 50, 180, 230, 64);

            for (float t = 0; t <= 1.0f; t += 0.05f)
            {
                float bx = -4.5f + t * 3.5f;
                float by = -5.5f + t * 1.5f;
                float bz = 2.5f + MathF.Sin(t * MathF.PI) * 3.0f;
                AddSolidBox(points, bx, by, bz, 0.45f, 0.45f, 0.45f, 245, 175, 15, 64);
            }

            AddSolidBox(points, 7.5f, 6.0f, 1.2f, 4.2f, 2.6f, 1.8f, 220, 140, 20, 64);
            AddSolidBox(points, -12.0f, 10.0f, 0.0f, 4.5f, 2.4f, 2.4f, 40, 100, 200, 64);
        }

        private static void AddWarehouseMachinery(List<Point3D> points)
        {
            AddSolidBox(points, 0.0f, -1.0f, 0.2f, 1.8f, 1.2f, 1.1f, 255, 190, 0, 64);
            AddSolidBox(points, 1.1f, -1.0f, 0.1f, 0.2f, 0.9f, 2.5f, 50, 50, 55, 64);
            AddSolidBox(points, 4.5f, 4.0f, 0.05f, 1.4f, 0.6f, 0.35f, 220, 40, 30, 64);
        }

        private static void AddCraterMachinery(List<Point3D> points)
        {
            AddSolidBox(points, -8.5f, 4.5f, 3.6f, 3.5f, 2.4f, 1.8f, 220, 90, 20, 64);
            AddSolidBox(points, -6.8f, 4.5f, 3.6f, 0.4f, 0.4f, 5.2f, 40, 40, 40, 64);
        }

        private static void AddSolidBox(List<Point3D> points, float cx, float cy, float cz,
                                        float sizeX, float sizeY, float sizeZ,
                                        byte r, byte g, byte b, byte classification)
        {
            float step = 0.10f;
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
                            points.Add(new Point3D(x, z, y, r, g, b, classification));
                        }
                    }
                }
            }
        }

        private static void WriteLasFile(string lasPath, List<Point3D> points, float driftOffset)
        {
            uint totalPoints = (uint)points.Count;

            using var fs = new FileStream(lasPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
            using var bw = new BinaryWriter(fs);

            bw.Write(new byte[] { (byte)'L', (byte)'A', (byte)'S', (byte)'F' });
            bw.Write((ushort)0); bw.Write((ushort)0); bw.Write(new byte[16]);
            bw.Write((byte)1); bw.Write((byte)2); bw.Write(new byte[32]); bw.Write(new byte[32]);
            bw.Write((ushort)0); bw.Write((ushort)2026); bw.Write((ushort)227); bw.Write((uint)227);
            bw.Write((uint)0); bw.Write((byte)2); bw.Write((ushort)26); bw.Write(totalPoints);

            bw.Write(totalPoints);
            bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0);

            double scale = 0.001;
            bw.Write(scale); bw.Write(scale); bw.Write(scale);
            bw.Write(0.0); bw.Write(0.0); bw.Write(0.0);
            bw.Write(200.0); bw.Write(-200.0); bw.Write(200.0); bw.Write(-200.0); bw.Write(200.0); bw.Write(-200.0);

            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                float realX = pt.X + driftOffset;
                float realY = pt.Z + (driftOffset * 0.4f);
                float realZ = pt.Y;

                bw.Write((int)(realX / scale));
                bw.Write((int)(realY / scale));
                bw.Write((int)(realZ / scale));

                bw.Write((ushort)0);
                bw.Write((byte)0);
                bw.Write(pt.Classification); // Запись байта классификации ASPRS
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)0);

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