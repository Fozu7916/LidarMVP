using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Numerics;
using System.Threading;
using LidarProcessorMVP;

namespace LidarRunner
{
    class Program
    {
        private static string plyDirectory = Directory.GetCurrentDirectory();
        private static string csvDirectory = Directory.GetCurrentDirectory();
        private static string outputXyzPath = "global_master_map.xyz";
        
        // Связанные параметры плотности (инициализируются базовым значением шага 0.10 м)
        private static float generationStep = 0.10f; 
        private static int estimatedPoints = 33600; 

        // Фиксированная площадь тестовой генерации (пол, потолок, 4 стены 12х12х4 м)
        private const float ROOM_SURFACE_AREA = 336.0f;

        static void Main(string[] args)
        {
            // Инициализация стартовых значений
            UpdateDensityByStep(0.10f);

            // Поддержка передачи параметров через аргументы (например: LidarRunner.exe -step 0.05 ИЛИ -points 50000)
            if (args.Length >= 2)
            {
                var culture = CultureInfo.InvariantCulture;
                if (args[0] == "-step" && float.TryParse(args[1], NumberStyles.Float, culture, out float cliStep))
                {
                    UpdateDensityByStep(cliStep);
                }
                else if (args[0] == "-points" && int.TryParse(args[1], out int cliPoints))
                {
                    UpdateDensityByCount(cliPoints);
                }
            }

            while (true)
            {
                Console.Clear();
                Console.WriteLine("==================================================");
                Console.WriteLine("    ЛИДАР-БИМ: КОМПЛЕКС АНАЛИЗА ОБЛАКОВ ТОЧЕК      ");
                Console.WriteLine("==================================================");
                Console.WriteLine($" [ Рабочая папка PLY ]: {plyDirectory}");
                Console.WriteLine($" [ Рабочая папка CSV ]: {csvDirectory}");
                Console.WriteLine($" [ Плотность сканов  ]: Шаг: {generationStep:F3} м | Точек: ~{estimatedPoints}");
                Console.WriteLine("--------------------------------------------------");
                Console.WriteLine(" 1. Выбрать папки с входными файлами (PLY и CSV)");
                Console.WriteLine(" 2. Настроить плотность (расстояние или кол-во точек)");
                Console.WriteLine(" 3. [Пайплайн] Собрать мастер-облако точек (.xyz)");
                Console.WriteLine(" 4. Посмотреть результат в Web-интерфейсе (Three.js)");
                Console.WriteLine(" 5. Выход");
                Console.WriteLine("==================================================");
                Console.Write("Выберите пункт меню (1-5): ");

                var key = Console.ReadKey(true);
                Console.WriteLine();

                if (key.Key == ConsoleKey.D5 || key.Key == ConsoleKey.NumPad5)
                {
                    Console.WriteLine("Завершение работы программы.");
                    break;
                }

                switch (key.Key)
                {
                    case ConsoleKey.D1:
                    case ConsoleKey.NumPad1:
                        ConfigureDirectories();
                        break;

                    case ConsoleKey.D2:
                    case ConsoleKey.NumPad2:
                        ConfigureDensity();
                        break;

                    case ConsoleKey.D3:
                    case ConsoleKey.NumPad3:
                        RunXyzPipeline();
                        Console.WriteLine("\nНажмите любую клавишу для возврата в меню...");
                        Console.ReadKey();
                        break;

                    case ConsoleKey.D4:
                    case ConsoleKey.NumPad4:
                        ShowWebVisualizer();
                        break;
                }
            }
        }

        static void UpdateDensityByStep(float step)
        {
            generationStep = Math.Max(0.005f, step); // Минимальный шаг 5 мм для защиты от OutOfMemory
            estimatedPoints = (int)(ROOM_SURFACE_AREA / (generationStep * generationStep));
        }

        static void UpdateDensityByCount(int targetCount)
        {
            estimatedPoints = Math.Max(100, targetCount); // Минимум 100 точек
            generationStep = (float)Math.Sqrt(ROOM_SURFACE_AREA / estimatedPoints);
        }

        static void ConfigureDirectories()
        {
            Console.Clear();
            Console.WriteLine("=== НАСТРОЙКА ПУТЕЙ К ФАЙЛАМ ===");
            
            Console.Write($"Введите путь к папке с PLY файлами [Enter — оставить текущую]: ");
            string plyInput = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(plyInput))
            {
                if (Directory.Exists(plyInput))
                {
                    plyDirectory = Path.GetFullPath(plyInput);
                    Console.WriteLine($"[OK] Папка PLY обновлена: {plyDirectory}");
                }
                else Console.WriteLine("[Ошибка] Указанная папка не существует.");
            }

            Console.Write($"Введите путь к папке с CSV телеметрией [Enter — оставить текущую]: ");
            string csvInput = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(csvInput))
            {
                if (Directory.Exists(csvInput))
                {
                    csvDirectory = Path.GetFullPath(csvInput);
                    Console.WriteLine($"[OK] Папка CSV обновлена: {csvDirectory}");
                }
                else Console.WriteLine("[Ошибка] Указанная папка не существует.");
            }

            Console.WriteLine("\nНажмите любую клавишу для возврата...");
            Console.ReadKey();
        }

        static void ConfigureDensity()
        {
            Console.Clear();
            Console.WriteLine("=== НАСТРОЙКА ПЛОТНОСТИ ОБЛАКА ТОЧЕК ===");
            Console.WriteLine("Математическая справка: Площадь помещения фиксирована (~336 кв.м).");
            Console.WriteLine("Расстояние между точками и их количество жестко зависят друг от друга.");
            Console.WriteLine($"Текущие параметры: Расстояние = {generationStep:F3} м | Кол-во точек = ~{estimatedPoints}\n");
            
            Console.WriteLine("Как вы хотите задать плотность?");
            Console.WriteLine(" 1. Указать точное расстояние между точками (шаг генерации в метрах)");
            Console.WriteLine(" 2. Указать желаемое количество точек (шаг вычислится автоматически)");
            Console.Write(" Выбор (1-2): ");

            var choice = Console.ReadKey(true);
            Console.WriteLine();
            var culture = CultureInfo.InvariantCulture;

            if (choice.Key == ConsoleKey.D1 || choice.Key == ConsoleKey.NumPad1)
            {
                Console.Write("\nВведите расстояние между точками в метрах (например, 0.05): ");
                if (float.TryParse(Console.ReadLine()?.Trim(), NumberStyles.Float, culture, out float customStep))
                {
                    UpdateDensityByStep(customStep);
                    Console.WriteLine($"[OK] Шаг установлен: {generationStep:F3} м. Ожидаемое кол-во точек: ~{estimatedPoints}");
                }
                else Console.WriteLine("[Ошибка] Неверный формат числа.");
            }
            else if (choice.Key == ConsoleKey.D2 || choice.Key == ConsoleKey.NumPad2)
            {
                Console.Write("\nВведите целевое количество точек на файл (например, 50000): ");
                if (int.TryParse(Console.ReadLine()?.Trim(), out int customCount))
                {
                    UpdateDensityByCount(customCount);
                    Console.WriteLine($"[OK] Целевое кол-во установлено: {estimatedPoints}. Расчетный шаг: {generationStep:F3} м");
                }
                else Console.WriteLine("[Ошибка] Неверный формат числа.");
            }
            else
            {
                Console.WriteLine("[Отмена] Возврат без изменений.");
            }

            Console.WriteLine("\nНажмите любую клавишу для возврата в меню...");
            Console.ReadKey();
        }

        static void RunXyzPipeline()
        {
            Console.WriteLine("\n=== ПАЙПЛАЙН: СБОРКА И ФИЛЬТРАЦИЯ ОБЛАКА ТОЧЕК (.XYZ) ===\n");
            Console.WriteLine($"[ИНФО] Генерация/загрузка (Шаг: {generationStep:F3} м, Цель: ~{estimatedPoints} точек)");

            var missionScans = DiscoverOrGenerateScans(plyDirectory, csvDirectory, generationStep);
            List<Point3D> masterGlobalCloud = new List<Point3D>();
            float voxelLeafSize = 0.02f; // Размер вокселя для финальной фильтрации

            Stopwatch stopwatch = Stopwatch.StartNew();

            foreach (var scan in missionScans)
            {
                Console.WriteLine($"[ ОБРАБОТКА ] Файл: {Path.GetFileName(scan.PlyPath)}");
                (Vector3 dronePos, Vector3 repPos, Quaternion droneRot) = LoadTelemetryFromCsv(scan.CsvPath, scan.DefaultPos, scan.RepPos);

                try
                {
                    List<Point3D> processedPoints = LidarPipeline.ProcessScanPipeline(
                        scan.PlyPath, dronePos, repPos, droneRot, voxelSize: 0.0f 
                    );

                    Console.WriteLine($"  - Добавлено точек из зоны: {processedPoints.Count}");
                    masterGlobalCloud.AddRange(processedPoints);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  [ ОШИБКА ] {ex.Message}");
                    Console.ResetColor();
                }
            }

            Console.WriteLine($"\n[ СЛИЯНИЕ ] Всего точек до дедупликации: {masterGlobalCloud.Count}");
            Console.WriteLine("[ ПРОЦЕСС ] Глобальный воксельный фильтр пространственной оптимизации...");
            
            List<Point3D> optimizedGlobalCloud = LidarPipeline.VoxelFilter(masterGlobalCloud, voxelLeafSize);
            Console.WriteLine($"  - Точек после фильтрации: {optimizedGlobalCloud.Count}");

            Console.WriteLine($"[ ПРОЦЕСС ] Экспорт в {outputXyzPath}...");
            LidarPipeline.ExportToXYZ(optimizedGlobalCloud, outputXyzPath);

            stopwatch.Stop();
            Console.WriteLine($"\n=== УСПЕХ === Мастер-облако готово за {stopwatch.ElapsedMilliseconds} мс");
        }

        static void ShowWebVisualizer()
        {
            string url = "http://localhost:8080/";
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add(url);

            try
            {
                listener.Start();
                Console.Clear();
                Console.WriteLine("==================================================");
                Console.WriteLine("          ВЕБ-ВИЗУАЛИЗАЦИЯ (THREE.JS)             ");
                Console.WriteLine("==================================================");
                Console.WriteLine($" [СТАТУС]: Сервер запущен на {url}");
                Console.WriteLine(" Для остановки сервера и возврата в меню нажмите любую клавишу...\n");

                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { Console.WriteLine("[INFO] Откройте браузер вручную: " + url); }

                bool isRunning = true;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    while (isRunning && listener.IsListening)
                    {
                        try
                        {
                            var context = listener.GetContext();
                            string localPath = context.Request.Url.LocalPath;
                            string filePath = localPath == "/" ? "viewer.html" : localPath.TrimStart('/');

                            if (!File.Exists(filePath) && localPath == "/")
                            {
                                string parentPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "viewer.html");
                                if (File.Exists(parentPath)) filePath = parentPath;
                            }

                            if (File.Exists(filePath))
                            {
                                byte[] buffer = File.ReadAllBytes(filePath);
                                if (filePath.EndsWith(".html")) context.Response.ContentType = "text/html; charset=utf-8";
                                else if (filePath.EndsWith(".xyz")) context.Response.ContentType = "text/plain; charset=utf-8";

                                context.Response.ContentLength64 = buffer.Length;
                                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                            }
                            else
                            {
                                context.Response.StatusCode = 404;
                                byte[] err = System.Text.Encoding.UTF8.GetBytes("404 Not Found");
                                context.Response.ContentLength64 = err.Length;
                                context.Response.OutputStream.Write(err, 0, err.Length);
                            }
                            context.Response.OutputStream.Close();
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
                Console.WriteLine($"[ОШИБКА] Сервер не запущен (вероятно порт 8080 занят): {ex.Message}");
                Console.ReadKey();
            }
        }

        static List<ScanMissionItem> DiscoverOrGenerateScans(string plyDir, string csvDir, float stepSize)
        {
            var defaultScans = new[]
            {
                new ScanMissionItem(Path.Combine(plyDir, "scan_zone_1.ply"), Path.Combine(csvDir, "telemetry_1.csv"), new Vector3(10.0f, 0.0f, 2.0f), new Vector3(0.0f, 0.0f, 0.0f)),
                new ScanMissionItem(Path.Combine(plyDir, "scan_zone_2.ply"), Path.Combine(csvDir, "telemetry_2.csv"), new Vector3(25.0f, 5.0f, 3.0f), new Vector3(50.0f, 0.0f, 0.0f)),
                new ScanMissionItem(Path.Combine(plyDir, "scan_zone_3.ply"), Path.Combine(csvDir, "telemetry_3.csv"), new Vector3(40.0f, -5.0f, 4.0f), new Vector3(100.0f, 0.0f, 0.0f))
            };

            foreach (var scan in defaultScans)
            {
                EnsureScanAndTelemetryExist(scan.PlyPath, scan.CsvPath, scan.DefaultPos, scan.RepPos, stepSize);
            }
            return new List<ScanMissionItem>(defaultScans);
        }

        public class ScanMissionItem
        {
            public string PlyPath { get; set; }
            public string CsvPath { get; set; }
            public Vector3 DefaultPos { get; set; }
            public Vector3 RepPos { get; set; }

            public ScanMissionItem(string ply, string csv, Vector3 dPos, Vector3 rPos)
            {
                PlyPath = ply; CsvPath = csv; DefaultPos = dPos; RepPos = rPos;
            }
        }

        static (Vector3 drone, Vector3 repeater, Quaternion rotation) LoadTelemetryFromCsv(string csvPath, Vector3 fallbackDrone, Vector3 fallbackRep)
        {
            if (!File.Exists(csvPath)) return (fallbackDrone, fallbackRep, Quaternion.Identity);
            try
            {
                var lines = File.ReadAllLines(csvPath);
                if (lines.Length < 2) return (fallbackDrone, fallbackRep, Quaternion.Identity);
                var parts = lines[1].Split(',');
                if (parts.Length < 6) return (fallbackDrone, fallbackRep, Quaternion.Identity);

                var culture = CultureInfo.InvariantCulture;
                return (
                    new Vector3(float.Parse(parts[0], culture), float.Parse(parts[1], culture), float.Parse(parts[2], culture)),
                    new Vector3(float.Parse(parts[3], culture), float.Parse(parts[4], culture), float.Parse(parts[5], culture)),
                    Quaternion.Identity
                );
            }
            catch { return (fallbackDrone, fallbackRep, Quaternion.Identity); }
        }

        static void EnsureScanAndTelemetryExist(string plyPath, string csvPath, Vector3 dronePos, Vector3 repPos, float stepSize)
        {
            var culture = CultureInfo.InvariantCulture;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(plyPath)));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath)));

            if (!File.Exists(plyPath))
            {
                var generatedPoints = new List<string>();
                
                for (float x = -6.0f; x <= 6.0f; x += stepSize)
                {
                    for (float z = -6.0f; z <= 6.0f; z += stepSize)
                    {
                        int r = (int)((x + 6.0f) / 12.0f * 200) + 50;
                        int b = (int)((z + 6.0f) / 12.0f * 200) + 50;
                        generatedPoints.Add($"{x.ToString(culture)} 0.0 {z.ToString(culture)} {r} 120 {b}");
                        generatedPoints.Add($"{x.ToString(culture)} 4.0 {z.ToString(culture)} 210 210 210");
                    }
                }

                for (float y = 0.0f; y <= 4.0f; y += stepSize)
                {
                    for (float x = -6.0f; x <= 6.0f; x += stepSize)
                    {
                        generatedPoints.Add($"{x.ToString(culture)} {y.ToString(culture)} -6.0 140 140 240");
                        generatedPoints.Add($"{x.ToString(culture)} {y.ToString(culture)} 6.0 240 140 140");
                    }
                    for (float z = -6.0f; z <= 6.0f; z += stepSize)
                    {
                        generatedPoints.Add($"-6.0 {y.ToString(culture)} {z.ToString(culture)} 140 240 140");
                        generatedPoints.Add($"6.0 {y.ToString(culture)} {z.ToString(culture)} 230 230 140");
                    }
                }

                using (StreamWriter writer = new StreamWriter(plyPath))
                {
                    writer.WriteLine("ply");
                    writer.WriteLine("format ascii 1.0");
                    writer.WriteLine($"element vertex {generatedPoints.Count}");
                    writer.WriteLine("property float x\nproperty float y\nproperty float z");
                    writer.WriteLine("property uchar red\nproperty uchar green\nproperty uchar blue");
                    writer.WriteLine("end_header");
                    foreach (var pt in generatedPoints) writer.WriteLine(pt);
                }
            }

            if (!File.Exists(csvPath))
            {
                using (StreamWriter writer = new StreamWriter(csvPath))
                {
                    writer.WriteLine("DroneX,DroneY,DroneZ,RepX,RepY,RepZ");
                    writer.WriteLine($"{dronePos.X.ToString(culture)},{dronePos.Y.ToString(culture)},{dronePos.Z.ToString(culture)},{repPos.X.ToString(culture)},{repPos.Y.ToString(culture)},{repPos.Z.ToString(culture)}");
                }
            }
        }
    }
}