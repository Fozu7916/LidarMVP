using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;

namespace LidarProcessorMVP
{
    public struct Point3D
    {
        public float X;
        public float Y;
        public float Z;
        public byte R;
        public byte G;
        public byte B;

        public Point3D(float x, float y, float z, byte r, byte g, byte b)
        {
            X = x; Y = y; Z = z; R = r; G = g; B = b;
        }
    }

    public static class LidarPipeline
    {
        /// <summary>
        /// Корректный пайплайн обработки скана с учетом правильного порядка трансформаций (Вращение -> Смещение)
        /// </summary>
        public static List<Point3D> ProcessScanPipeline(string plyPath, Vector3 dronePos, Vector3 repPos, Quaternion droneRotation, float voxelSize)
        {
            var rawPoints = LoadPlyFile(plyPath);
            var transformedPoints = new List<Point3D>(rawPoints.Count);

            // Строим матрицу трансформации из кватерниона поворота дрона
            // Важно: вначале локальная точка вращается в ориентацию мира, затем сдвигается на позицию дрона
            Matrix4x4 transformMatrix = Matrix4x4.CreateFromQuaternion(droneRotation) * Matrix4x4.CreateTranslation(dronePos);

            foreach (var pt in rawPoints)
            {
                Vector3 localPt = new Vector3(pt.X, pt.Y, pt.Z);
                
                // Применяем общую матрицу трансформации (Локальные координаты лидара -> Глобальный мир)
                Vector3 globalPt = Vector3.Transform(localPt, transformMatrix);

                transformedPoints.Add(new Point3D(globalPt.X, globalPt.Y, globalPt.Z, pt.R, pt.G, pt.B));
            }

            if (voxelSize > 0.0f)
            {
                return VoxelFilter(transformedPoints, voxelSize);
            }

            return transformedPoints;
        }

        private static List<Point3D> LoadPlyFile(string path)
        {
            var points = new List<Point3D>();
            using (var reader = new StreamReader(path))
            {
                string line;
                bool isHeader = true;
                int vertexCount = 0;

                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (isHeader)
                    {
                        if (line.StartsWith("element vertex", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 3) int.TryParse(parts[2], out vertexCount);
                        }
                        if (line.Equals("end_header", StringComparison.OrdinalIgnoreCase))
                        {
                            isHeader = false;
                        }
                        continue;
                    }

                    var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 3)
                    {
                        float x = float.Parse(tokens[0], System.Globalization.CultureInfo.InvariantCulture);
                        float y = float.Parse(tokens[1], System.Globalization.CultureInfo.InvariantCulture);
                        float z = float.Parse(tokens[2], System.Globalization.CultureInfo.InvariantCulture);

                        byte r = 255, g = 255, b = 255;
                        if (tokens.Length >= 6)
                        {
                            byte.TryParse(tokens[3], out r);
                            byte.TryParse(tokens[4], out g);
                            byte.TryParse(tokens[5], out b);
                        }

                        points.Add(new Point3D(x, y, z, r, g, b));
                    }
                }
            }
            return points;
        }

        /// <summary>
        /// Многопоточная воксельная фильтрация (дедупликация облака точек)
        /// </summary>
        public static List<Point3D> VoxelFilter(List<Point3D> points, float leafSize)
        {
            if (leafSize <= 0.0f) return points;

            var voxelMap = new System.Collections.Concurrent.ConcurrentDictionary<ValueTuple<int, int, int>, Point3D>();

            Parallel.ForEach(points, pt =>
            {
                int vx = (int)MathF.Floor(pt.X / leafSize);
                int vy = (int)MathF.Floor(pt.Y / leafSize);
                int vz = (int)MathF.Floor(pt.Z / leafSize);

                var key = (vx, vy, vz);
                // Оставляем первую попавшуюся точку вокселя как репрезентативную
                voxelMap.TryAdd(key, pt);
            });

            return new List<Point3D>(voxelMap.Values);
        }

        public static void ExportToXYZ(List<Point3D> points, string outputPath)
        {
            using (var writer = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8))
            {
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var pt in points)
                {
                    writer.WriteLine($"{pt.X.ToString(culture)} {pt.Y.ToString(culture)} {pt.Z.ToString(culture)} {pt.R} {pt.G} {pt.B}");
                }
            }
        }
    }
}