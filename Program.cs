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
        
        // Настраиваемый шаг генерации по умолчанию (в метрах)
        // Меньше шаг -> выше плотность точек (больше деталей), больше шаг -> быстрее расчет.
        private static float generationStep = 0.10f; 

        static void Main(string[] args)
        {
            while (true)
            {
                Console.Clear();
                Console.WriteLine("==================================================");
                Console.WriteLine("     ЛИДАР-БИМ: КОМПЛЕКС АНАЛИЗА ОБЛАКОВ ТОЧЕК      ");
                Console.WriteLine("==================================================");
                Console.WriteLine($" [ Рабочая папка PLY ]: {plyDirectory}");
                Console.WriteLine($" [ Рабочая папка CSV ]: {csvDirectory}");
                Console.WriteLine($" [ Плотность генерации]: Шаг сетки = {generationStep:F2} м");
                Console.WriteLine("--------------------------------------------------");
                Console.WriteLine(" 1. Выбрать папки с входными файлами (PLY и CSV)");
                Console.WriteLine(" 2. Настроить параметры автогенерации тестовых сканов");
                Console.WriteLine(" 3. [Пайплайн] Собрать мастер-облако точек (.xyz) и отфильтровать");
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
                        ConfigureGenerationStep();
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
                else
                {
                    Console.WriteLine("[Ошибка] Указанная папка не существует.");
                }
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
                else
                {
                    Console.WriteLine("[Ошибка] Указанная папка не существует.");
                }
            }

            Console.WriteLine("\nНажмите любую клавишу для возврата в меню...");
            Console.ReadKey();
        }

        static void ConfigureGenerationStep()
        {
            Console.Clear();
            Console.WriteLine("=== НАСТРОЙКА ПАРАМЕТРОВ АВТОГЕНЕРАЦИИ СКАНОВ ===");
            Console.WriteLine("Шаг сетки определяет плотность синтетических данных (облака точек).");
            Console.WriteLine(" - Меньший шаг (например, 0.05м) создает высокую детализацию и монолитность стен.");
            Console.WriteLine(" - Больший шаг (например, 0.20м) ускоряет расчеты и снижает нагрузку на память.\n");
            
            Console.WriteLine("Выберите пресет или введите свое значение:");
            Console.WriteLine(" 1. Высокая детализация (шаг 0.05 м — плотное покрытие)");
            Console.WriteLine(" 2. Стандартный баланс (шаг 0.10 м — оптимально для MVP)");
            Console.WriteLine(" 3. Быстрый черновой режим (шаг 0.20 м — для тестов)");
            Console.Write(" Введите номер пресета или произвольное число (в метрах): ");

            string input = Console.ReadLine()?.Trim();
            var culture = CultureInfo.InvariantCulture;

            if (input == "1")
            {
                generationStep = 0.05f;
                Console.WriteLine("[OK] Установлен пресет: Высокая детализация (0.05 м)");
            }
            else if (input == "2")
            {
                generationStep = 0.10f;
                Console.WriteLine("[OK] Установлен пресет: Стандартный баланс (0.10 м)");
            }
            else if (input == "3")
            {
                generationStep = 0.20f;
                Console.WriteLine("[OK] Установлен пресет: Быстрый черновой режим (0.20 м)");
            }
            else if (float.TryParse(input, NumberStyles.Float, culture, out float customVal) && customVal > 0.001f)
            {
                generationStep = customVal;
                Console.WriteLine($"[OK] Установлен пользовательский шаг: {generationStep:F3} м");
            }
            else
            {
                Console.WriteLine("[Инфо] Неверный ввод, значение осталось без изменений.");
            }

            Console.WriteLine("\nНажмите любую клавишу для возврата в меню...");
            Console.ReadKey();
        }

        /// <summary>
        /// Основной пайплайн: Сбор сырых сканов, применение телеметрии, глобальная фильтрация и экспорт в .xyz
        /// </summary>
        static void RunXyzPipeline()
        {
            Console.WriteLine("\n=== ПАЙПЛАЙН: СБОРКА И ФИЛЬТРАЦИЯ ОБЛАКА ТОЧЕК (.XYZ) ===\n");
            Console.WriteLine($"[ИНФО] Генерация/загрузка с текущим шагом: {generationStep:F2} м");

            var missionScans = DiscoverOrGenerateScans(plyDirectory, csvDirectory, generationStep);
            List<Point3D> masterGlobalCloud = new List<Point3D>();
            float voxelLeafSize = 0.02f; // Размер вокселя для финальной глобальной фильтрации

            Stopwatch stopwatch = Stopwatch.StartNew();

            foreach (var scan in missionScans)
            {
                Console.WriteLine($"[ ОБРАБОТКА ] Файл: {Path.GetFileName(scan.PlyPath)}");

                (Vector3 dronePos, Vector3 repPos, Quaternion droneRot) = LoadTelemetryFromCsv(scan.CsvPath, scan.DefaultPos, scan.RepPos);

                try
                {
                    List<Point3D> processedPoints = LidarPipeline.ProcessScanPipeline(
                        scan.PlyPath,
                        dronePos,
                        repPos,
                        droneRot,
                        voxelSize: 0.0f // Отключаем внутренний срез на уровне отдельного файла для максимальной точности слияния
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
            Console.WriteLine("[ ПРОЦЕСС ] Применение глобального воксельного фильтра пространственной оптимизации...");
            
            List<Point3D> optimizedGlobalCloud = LidarPipeline.VoxelFilter(masterGlobalCloud, voxelLeafSize);
            
            Console.WriteLine($"  - Итоговое количество точек после фильтрации: {optimizedGlobalCloud.Count}");

            Console.WriteLine($"[ ПРОЦЕСС ] Экспорт мастер-карты в промышленный формат {outputXyzPath}...");
            LidarPipeline.ExportToXYZ(optimizedGlobalCloud, outputXyzPath);

            stopwatch.Stop();
            Console.WriteLine($"\n=== УСПЕХ === Мастер-облако точек успешно сформировано за {stopwatch.ElapsedMilliseconds} мс");
            Console.WriteLine($" Абсолютный путь: {Path.GetFullPath(outputXyzPath)}");
        }

        /// <summary>
        /// Запуск встроенного легковесного HTTP-сервера для визуализации через Three.js
        /// </summary>
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
                Console.WriteLine($" [СТАТУС]: Локальный сервер запущен на {url}");
                Console.WriteLine(" Браузер откроется автоматически.");
                Console.WriteLine(" Для остановки сервера и возврата в меню нажмите любую клавишу...\n");

                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch
                {
                    Console.WriteLine("[INFO] Не удалось автоматически открыть браузер. Перейдите по ссылке вручную: " + url);
                }

                bool isRunning = true;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    while (isRunning && listener.IsListening)
                    {
                        try
                        {
                            var context = listener.GetContext();
                            var request = context.Request;
                            var response = context.Response;

                            string localPath = request.Url.LocalPath;
                            string filePath = localPath == "/" ? "viewer.html" : localPath.TrimStart('/');

                            if (!File.Exists(filePath) && localPath == "/")
                            {
                                string parentPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "viewer.html");
                                if (File.Exists(parentPath)) filePath = parentPath;
                            }

                            if (File.Exists(filePath))
                            {
                                byte[] buffer = File.ReadAllBytes(filePath);
                                
                                if (filePath.EndsWith(".html")) response.ContentType = "text/html; charset=utf-8";
                                else if (filePath.EndsWith(".xyz")) response.ContentType = "text/plain; charset=utf-8";

                                response.ContentLength64 = buffer.Length;
                                response.OutputStream.Write(buffer, 0, buffer.Length);
                            }
                            else
                            {
                                response.StatusCode = (int)HttpStatusCode.NotFound;
                                byte[] errorBytes = System.Text.Encoding.UTF8.GetBytes("404 Файл не найден. Убедитесь, что viewer.html находится в корне проекта.");
                                response.ContentLength64 = errorBytes.Length;
                                response.OutputStream.Write(errorBytes, 0, errorBytes.Length);
                            }
                            response.OutputStream.Close();
                        }
                        catch
                        {
                            // Предотвращение падений потока при остановке сервера
                        }
                    }
                });

                Console.ReadKey();
                isRunning = false;
                listener.Stop();
                Console.WriteLine("\n[INFO] Веб-сервер остановлен. Возврат в главное меню...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ОШИБКА] Не удалось запустить HTTP-сервер: {ex.Message}");
                Console.WriteLine("Возможно, порт 8080 занят. Нажмите любую клавишу...");
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
                PlyPath = ply;
                CsvPath = csv;
                DefaultPos = dPos;
                RepPos = rPos;
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
                float dx = float.Parse(parts[0], culture);
                float dy = float.Parse(parts[1], culture);
                float dz = float.Parse(parts[2], culture);
                float rx = float.Parse(parts[3], culture);
                float ry = float.Parse(parts[4], culture);
                float rz = float.Parse(parts[5], culture);

                return (new Vector3(dx, dy, dz), new Vector3(rx, ry, rz), Quaternion.Identity);
            }
            catch
            {
                return (fallbackDrone, fallbackRep, Quaternion.Identity);
            }
        }

        static void EnsureScanAndTelemetryExist(string plyPath, string csvPath, Vector3 dronePos, Vector3 repPos, float stepSize)
        {
            var culture = CultureInfo.InvariantCulture;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(plyPath)));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath)));

            // Процедурная генерация тестового облака точек с учетом динамического шага
            if (!File.Exists(plyPath))
            {
                var generatedPoints = new List<string>();

                // Пол и потолок
                for (float x = -6.0f; x <= 6.0f; x += stepSize)
                {
                    for (float z = -6.0f; z <= 6.0f; z += stepSize)
                    {
                        int r = (int)((x + 6.0f) / 12.0f * 200) + 50;
                        int g = 120;
                        int b = (int)((z + 6.0f) / 12.0f * 200) + 50;
                        
                        generatedPoints.Add($"{x.ToString(culture)} 0.0 {z.ToString(culture)} {r} {g} {b}");
                        generatedPoints.Add($"{x.ToString(culture)} 4.0 {z.ToString(culture)} 210 210 210");
                    }
                }

                // Периметр стен
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
                    writer.WriteLine("property float x");
                    writer.WriteLine("property float y");
                    writer.WriteLine("property float z");
                    writer.WriteLine("property uchar red");
                    writer.WriteLine("property uchar green");
                    writer.WriteLine("property uchar blue");
                    writer.WriteLine("end_header");

                    foreach (var pt in generatedPoints)
                    {
                        writer.WriteLine(pt);
                    }
                }
            }

            if (!File.Exists(csvPath))
            {
                using (StreamWriter writer = new StreamWriter(csvPath))
                {
                    writer.WriteLine("DroneX,DroneY,DroneZ,RepX,RepY,RepZ");
                    writer.WriteLine(
                        $"{dronePos.X.ToString(culture)}," +
                        $"{dronePos.Y.ToString(culture)}," +
                        $"{dronePos.Z.ToString(culture)}," +
                        $"{repPos.X.ToString(culture)}," +
                        $"{repPos.Y.ToString(culture)}," +
                        $"{repPos.Z.ToString(culture)}"
                    );
                }
            }
        }
    }
}