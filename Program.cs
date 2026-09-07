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
    class Program
    {
        private static string workDirectory = Directory.GetCurrentDirectory();
        private static float voxelStep = 0.15f; 

        static void Main(string[] args)
        {
            while (true)
            {
                var (files, isLas) = ScanWorkDirectory(workDirectory);

                Console.Clear();
                Console.WriteLine("==========================================================");
                Console.WriteLine("   ЛИДАР-БИМ: N-МЕРНЫЙ 4D МОНИТОРИНГ ВЫРАБОТКИ            ");
                Console.WriteLine("==========================================================");
                Console.WriteLine($" [ Каталог ]: {workDirectory}");

                if (files.Count > 0)
                {
                    Console.WriteLine($" [ Тип     ]: {(isLas ? "LAS (Промышленный)" : "PLY (Текстовый)")}");
                    Console.WriteLine($" [ Сканов  ]: {files.Count} шт.");
                }
                else
                {
                    Console.WriteLine($" [ Статус  ]: Файлы не найдены. Готов к автогенерации.");
                }

                Console.WriteLine("----------------------------------------------------------");
                Console.WriteLine(" 1. Задать путь к папке со сканами");
                Console.WriteLine(" 2. Настроить шаг воксельной сетки");
                Console.WriteLine(" 3. [ЗАПУСК] ICP сведение N-сканов + Расчет объемов");
                Console.WriteLine(" 4. Открыть Web-визуализатор (Three.js)");
                Console.WriteLine(" 5. Выход");
                Console.WriteLine("==========================================================");
                Console.Write(" Выберите пункт: ");

                var key = Console.ReadKey(true);
                Console.WriteLine();

                switch (key.Key)
                {
                    case ConsoleKey.D1: ConfigureDirectory(); break;
                    case ConsoleKey.D2: ConfigureVoxel(); break;
                    case ConsoleKey.D3: 
                        RunNDPipeline(files); 
                        Console.WriteLine("\nНажмите любую клавишу для возврата в меню...");
                        Console.ReadKey();
                        break;
                    case ConsoleKey.D4: ShowWebVisualizer(); break;
                    case ConsoleKey.D5: return;
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

        static void RunNDPipeline(List<string> inputFiles)
        {
            Console.WriteLine("\n=== 1. ПОДГОТОВКА ДАННЫХ (N ЭПОХ) ===");

            if (inputFiles.Count == 0)
            {
                Console.WriteLine("[ИНФО] Данных нет. Автогенерация 3-х эпох карьера...");
                string e0 = Path.Combine(workDirectory, "epoch_0_base.las");
                string e1 = Path.Combine(workDirectory, "epoch_1_dig1.las");
                string e2 = Path.Combine(workDirectory, "epoch_2_dig2.las");

                // Имитация реального дрона с RTK: дрейф всего 3-5 сантиметров
                GenerateSyntheticLas(e0, 0.00f, 0.0f, 0.0f);
                GenerateSyntheticLas(e1, 0.03f, 4.0f, 2.0f);
                GenerateSyntheticLas(e2, 0.05f, 6.5f, 4.0f);

                inputFiles.AddRange(new[] { e0, e1, e2 });
            }

            Stopwatch sw = Stopwatch.StartNew();
            LidarPipeline.HasGlobalOrigin = false;

            List<Vector3>? baseVectors = null;
            List<Point3D>? baseCloud = null;
            
            var metaEpochs = new List<object>();
            float cellSize = Math.Max(0.1f, voxelStep * 2.0f);

            for (int i = 0; i < inputFiles.Count; i++)
            {
                string currentFile = inputFiles[i];
                string binFile = $"epoch_{i}.bin";
                
                Console.WriteLine($"\n=== ОБРАБОТКА ЭПОХИ {i}: {Path.GetFileName(currentFile)} ===");

                var optimizedCloud = ProcessEpoch(currentFile, binFile, baseVectors);

                double extractedTotal = 0;

                if (i == 0)
                {
                    baseCloud = optimizedCloud;
                    baseVectors = ExtractVectors(optimizedCloud);
                }
                else
                {
                    // ИСТИННЫЙ РАСЧЕТ: Напрямую сравниваем ячейки новой сетки с ячейками Эпохи 0
                    extractedTotal = MathApparatus.CalculateVolumeDifference(baseCloud!, optimizedCloud, cellSize);
                }
                
                Console.WriteLine($"  -> Извлечено породы от Эпохи 0: {extractedTotal:N2} м3");

                metaEpochs.Add(new 
                { 
                    Id = i, 
                    Name = Path.GetFileName(currentFile), 
                    BinFile = binFile,
                    ExtractedTotal = Math.Round(extractedTotal, 2)
                });
            }

            var metaData = new { Epochs = metaEpochs };
            File.WriteAllText(Path.Combine(workDirectory, "meta.json"), JsonSerializer.Serialize(metaData));

            sw.Stop();
            Console.WriteLine($"\n[УСПЕХ] Пайплайн завершен за {sw.ElapsedMilliseconds} мс.");
        }

        static List<Point3D> ProcessEpoch(string filePath, string outBin, List<Vector3>? referenceStrip)
        {
            var currentPoints = LidarPipeline.LoadScanAuto(filePath);
            var masterCloud = new List<Point3D>();

            if (referenceStrip != null)
            {
                Console.Write("  -> Выравнивание коллизии (Trimmed ICP)... ");
                var currentVectors = ExtractVectors(currentPoints);
                var (icpTransform, error) = MathApparatus.AlignCloudsICP(currentVectors, referenceStrip, 10);
                Console.WriteLine($"[OK] (Невязка: {error:F4} м)");

                for (int j = 0; j < currentPoints.Count; j++)
                {
                    var p = currentPoints[j];
                    var v = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), icpTransform);
                    masterCloud.Add(new Point3D(v.X, v.Y, v.Z, p.R, p.G, p.B));
                }
            }
            else
            {
                masterCloud.AddRange(currentPoints);
                Console.WriteLine("  -> Базис зафиксирован.");
            }

            Console.Write($"  -> Дедупликация ({voxelStep:F3} м)... ");
            var optimizedCloud = LidarPipeline.VoxelFilter(masterCloud, voxelStep);
            Console.WriteLine($"[Осталось точек: {optimizedCloud.Count:N0}]");

            LidarPipeline.ExportToBinary(optimizedCloud, Path.Combine(workDirectory, outBin));
            return optimizedCloud;
        }

        static List<Vector3> ExtractVectors(List<Point3D> points)
        {
            var list = new List<Vector3>(points.Count);
            foreach (var p in points) list.Add(new Vector3(p.X, p.Y, p.Z));
            return list;
        }

        static void GenerateSyntheticLas(string lasPath, float driftOffset, float craterRadius, float digDepthMax)
        {
            float step = 0.1f;
            float min = -15.0f, max = 15.0f;
            int steps = (int)((max - min) / step) + 1;
            uint totalPoints = (uint)(steps * steps);

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
            bw.Write(100.0); bw.Write(-100.0); bw.Write(100.0); bw.Write(-100.0); bw.Write(100.0); bw.Write(-100.0); 

            for (float x = min; x <= max + 1e-4f; x += step)
            {
                for (float y = min; y <= max + 1e-4f; y += step)
                {
                    float rDist = MathF.Sqrt(x * x + y * y);
                    
                    float baseTerrainZ = (x * 0.08f) + (y * 0.06f); 
                    float z = baseTerrainZ; 
                    
                    if (rDist < 9.0f)
                    {
                        z += 4.0f * MathF.Cos(rDist * MathF.PI / 18.0f); 
                    }

                    float originalZ = z;

                    if (craterRadius > 0)
                    {
                        float craterDist = MathF.Sqrt((x - 1.5f) * (x - 1.5f) + (y + 1.0f) * (y + 1.0f));
                        if (craterDist < craterRadius)
                        {
                            float currentDig = digDepthMax * MathF.Cos(craterDist * MathF.PI / (craterRadius * 2.0f));
                            z -= currentDig;
                            
                            float minDigZ = baseTerrainZ + 0.3f;
                            if (z < minDigZ) z = minDigZ; 
                        }
                    }

                    z += (MathF.Sin(x * 3.0f) * MathF.Cos(y * 3.0f)) * 0.15f; 

                    float finalX = x + driftOffset;
                    float finalY = y + (driftOffset * 0.3f);
                    float finalZ = z;

                    bw.Write((int)(finalX / scale)); bw.Write((int)(finalY / scale)); bw.Write((int)(finalZ / scale));
                    bw.Write((ushort)0); bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); bw.Write((ushort)0); 
                    
                    ushort colorR = 100, colorG = 90, colorB = 80; 
                    if (rDist < 9.0f)
                    {
                        if (craterRadius > 0 && z < originalZ - 0.2f)
                        {
                            colorR = 140; colorG = 100; colorB = 70; 
                        }
                        else
                        {
                            colorR = 190; colorG = 170; colorB = 120; 
                        }
                    }

                    bw.Write((ushort)(colorR << 8)); bw.Write((ushort)(colorG << 8)); bw.Write((ushort)(colorB << 8));
                }
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

                            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                            if (File.Exists(localPath))
                            {
                                byte[] buf = File.ReadAllBytes(localPath);
                                if (localPath.EndsWith(".html")) ctx.Response.ContentType = "text/html; charset=utf-8";
                                else if (localPath.EndsWith(".json")) ctx.Response.ContentType = "application/json; charset=utf-8";
                                else ctx.Response.ContentType = "application/octet-stream";

                                ctx.Response.ContentLength64 = buf.Length;
                                ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                            }
                            else ctx.Response.StatusCode = 404;
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