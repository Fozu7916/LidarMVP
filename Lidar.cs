using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace LidarProcessorMVP
{
    /// <summary>
    /// Облегченная структура точки для максимальной производительности
    /// и минимальной нагрузки на Garbage Collector при работе с миллионами записей.
    /// </summary>
    public struct Point3D
    {
        public float X;
        public float Y;
        public float Z;
        public byte R, G, B;

        public Point3D(float x, float y, float z, byte r = 255, byte g = 255, byte b = 255)
        {
            X = x; Y = y; Z = z;
            R = r; G = g; B = b;
        }
    }

    public class LidarPipeline
    {
        /// <summary>
        /// Шаг 1. Чтение и парсинг PLY файла в локальной системе координат лидара.
        /// </summary>
        public static List<Point3D> ParseAsciiPly(string filePath)
        {
            var points = new List<Point3D>();
            int vertexCount = 0;
            bool readingHeader = true;

            var culture = CultureInfo.InvariantCulture;

            using (StreamReader reader = new StreamReader(filePath))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    if (readingHeader)
                    {
                        if (line.StartsWith("element vertex"))
                        {
                            string[] parts = line.Split(' ');
                            vertexCount = int.Parse(parts[2]);
                        }
                        else if (line == "end_header")
                        {
                            readingHeader = false;
                            points.Capacity = vertexCount; 
                        }
                        continue;
                    }

                    string[] data = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (data.Length >= 3)
                    {
                        float x = float.Parse(data[0], culture);
                        float y = float.Parse(data[1], culture);
                        float z = float.Parse(data[2], culture);
                        
                        byte r = data.Length > 3 ? byte.Parse(data[3]) : (byte)255;
                        byte g = data.Length > 4 ? byte.Parse(data[4]) : (byte)255;
                        byte b = data.Length > 5 ? byte.Parse(data[5]) : (byte)255;

                        points.Add(new Point3D(x, y, z, r, g, b));
                        
                        if (points.Count >= vertexCount) break;
                    }
                }
            }
            return points;
        }

        /// <summary>
        /// Шаг 2. Пространственная трансформация (сшивка).
        /// Перевод точек из локальной системы дрона в глобальную систему ретранслятора.
        /// </summary>
        public static List<Point3D> TransformToGlobal(List<Point3D> localPoints, Vector3 dronePos, Vector3 repeaterPos, Quaternion droneRotation)
        {
            var globalPoints = new List<Point3D>(localPoints.Count);
            
            // Исправлено: позиция дрона должна смещаться относительно глобальной базы (repeaterPos),
            // а не складываться с ней простым суммированием.
            Vector3 globalOrigin = repeaterPos + dronePos;
            Matrix4x4 transformMatrix = Matrix4x4.CreateFromQuaternion(droneRotation) * Matrix4x4.CreateTranslation(globalOrigin);

            foreach (var pt in localPoints)
            {
                Vector3 localVec = new Vector3(pt.X, pt.Y, pt.Z);
                Vector3 globalVec = Vector3.Transform(localVec, transformMatrix);

                globalPoints.Add(new Point3D(
                    globalVec.X, globalVec.Y, globalVec.Z, pt.R, pt.G, pt.B
                ));
            }

            return globalPoints;
        }

        /// <summary>
        /// Шаг 3. Статистический фильтр удаления выбросов (Statistical Outlier Removal - SOR).
        /// Очищает облако от пыли, капель и мелких одиночных помех в воздухе.
        /// </summary>
        /// <param name="points">Исходное облако точек</param>
        /// <param name="kNearest">Количество ближайших соседей для анализа (обычно 8–16)</param>
        /// <param name="stdDevMulThresh">Множитель стандартного отклонения (выше порога — точка считается мусором)</param>
        public static List<Point3D> RemoveOutliers(List<Point3D> points, int kNearest = 12, float stdDevMulThresh = 1.0f)
        {
            int n = points.Count;
            if (n <= kNearest) return points;

            // Упрощенный расчет средних расстояний до соседей (для MVP оптимизирован по памяти)
            var meanDistances = new float[n];
            float globalSum = 0f;

            // Находим среднее расстояние до K ближайших соседей для каждой точки
            for (int i = 0; i < n; i++)
            {
                var p1 = points[i];
                // Собираем расстояния до всех остальных точек (для чистого MVP; в продакшене заменяется на Kd-Tree)
                var distances = new List<float>(n - 1);
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    var p2 = points[j];
                    float dist = MathF.Sqrt(
                        (p1.X - p2.X) * (p1.X - p2.X) +
                        (p1.Y - p2.Y) * (p1.Y - p2.Y) +
                        (p1.Z - p2.Z) * (p1.Z - p2.Z)
                    );
                    distances.Add(dist);
                }

                distances.Sort();
                int countToTake = Math.Min(kNearest, distances.Count);
                float sumDist = 0f;
                for (int k = 0; k < countToTake; k++)
                {
                    sumDist += distances[k];
                }

                meanDistances[i] = sumDist / countToTake;
                globalSum += meanDistances[i];
            }

            float globalMean = globalSum / n;

            // Считаем стандартное отклонение
            float varianceSum = 0f;
            for (int i = 0; i < n; i++)
            {
                varianceSum += (meanDistances[i] - globalMean) * (meanDistances[i] - globalMean);
            }
            float stdDev = MathF.Sqrt(varianceSum / n);
            float threshold = globalMean + stdDevMulThresh * stdDev;

            // Фильтруем точки
            var filteredPoints = new List<Point3D>(n);
            for (int i = 0; i < n; i++)
            {
                if (meanDistances[i] <= threshold)
                {
                    filteredPoints.Add(points[i]);
                }
            }

            return filteredPoints;
        }

        /// <summary>
        /// Шаг 4. Воксельный фильтр (Downsampling).
        /// Удаляет дубликаты и снижает плотность облака для мешинга.
        /// </summary>
        public static List<Point3D> VoxelFilter(List<Point3D> points, float leafSize)
        {
            var voxelGrid = new Dictionary<(int, int, int), Point3D>();

            foreach (var pt in points)
            {
                var key = (
                    (int)Math.Floor(pt.X / leafSize),
                    (int)Math.Floor(pt.Y / leafSize),
                    (int)Math.Floor(pt.Z / leafSize)
                );

                voxelGrid.TryAdd(key, pt);
            }

            return new List<Point3D>(voxelGrid.Values);
        }

        /// <summary>
        /// Шаг 5. Экспорт глобального облака в итоговый текстовый формат (XYZ).
        /// </summary>
        public static void ExportToXYZ(List<Point3D> points, string outputPath)
        {
            var culture = CultureInfo.InvariantCulture;
            using (StreamWriter writer = new StreamWriter(outputPath))
            {
                foreach (var pt in points)
                {
                    writer.WriteLine($"{pt.X.ToString(culture)} {pt.Y.ToString(culture)} {pt.Z.ToString(culture)} {pt.R} {pt.G} {pt.B}");
                }
            }
        }

        /// <summary>
        /// Метод-оркестратор: полный цикл обработки скана с очисткой от шума и вокселизацией.
        /// </summary>
        public static List<Point3D> ProcessScanPipeline(string inputPlyPath, Vector3 dronePos, Vector3 repeaterPos, Quaternion droneRot, float voxelSize)
        {
            // 1. Читаем сырые данные
            List<Point3D> rawPoints = ParseAsciiPly(inputPlyPath);

            // 2. Сшиваем в глобальные координаты
            List<Point3D> globalPoints = TransformToGlobal(rawPoints, dronePos, repeaterPos, droneRot);

            // 3. Убираем шумовые выбросы (пыль, помехи)
            globalPoints = RemoveOutliers(globalPoints, kNearest: 10, stdDevMulThresh: 1.2f);

            // 4. Прореживаем точки (если voxelSize > 0)
            if (voxelSize > 0.0f)
            {
                globalPoints = VoxelFilter(globalPoints, voxelSize);
            }

            return globalPoints;
        }
    }
}