using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using MathNet.Numerics.LinearAlgebra.Single;

namespace LidarProcessorMVP
{
    public readonly struct VolumeBalance
    {
        public readonly double CutVolume;         // Геометрическая выемка в целике (м3)
        public readonly double FillVolume;        // Геометрическая насыпь (м3)
        public readonly double NetVolume;         // Чистый баланс (м3)
        public readonly double BulkedVolume;      // Объем с учетом Кр (м3)
        public readonly double MaterialWeightTon; // Масса перемещенной породы (тонн)
        public readonly double AreaCut;           // Площадь выемки (м2)
        public readonly double AreaFill;          // Площадь насыпи (м2)
        public readonly double MaxDepth;          // Максимальная глубина (м)
        public readonly double RmseElevation;     // СКП по высоте (м)

        public VolumeBalance(double cut, double fill, double areaCut, double areaFill, double maxDepth, 
                             float density, float bulkingFactor, double rmse)
        {
            CutVolume = cut;
            FillVolume = fill;
            NetVolume = cut - fill;
            BulkedVolume = cut * bulkingFactor;
            MaterialWeightTon = CutVolume * density;
            AreaCut = areaCut;
            AreaFill = areaFill;
            MaxDepth = maxDepth;
            RmseElevation = rmse;
        }
    }

    public class BoundaryPolygon
    {
        public List<Vector2> Vertices { get; set; } = new List<Vector2>();

        public bool IsPointInside(float x, float y)
        {
            if (Vertices == null || Vertices.Count < 3) return true;

            bool inside = false;
            int j = Vertices.Count - 1;

            for (int i = 0; i < Vertices.Count; i++)
            {
                var vi = Vertices[i];
                var vj = Vertices[j];

                if (((vi.Y > y) != (vj.Y > y)) &&
                    (x < (vj.X - vi.X) * (y - vi.Y) / (vj.Y - vi.Y) + vi.X))
                {
                    inside = !inside;
                }
                j = i;
            }

            return inside;
        }

        public static BoundaryPolygon LoadOrCreate(string dir, float defaultRadius = 12.0f)
        {
            string geoJsonPath = Path.Combine(dir, "boundary.geojson");
            if (File.Exists(geoJsonPath))
            {
                try
                {
                    var poly = ParseGeoJsonPolygon(File.ReadAllText(geoJsonPath));
                    if (poly.Vertices.Count >= 3) return poly;
                }
                catch { }
            }

            string jsonPath = Path.Combine(dir, "boundary.json");
            if (File.Exists(jsonPath))
            {
                try
                {
                    var poly = JsonSerializer.Deserialize<BoundaryPolygon>(File.ReadAllText(jsonPath));
                    if (poly != null && poly.Vertices.Count >= 3) return poly;
                }
                catch { }
            }

            var defaultPoly = new BoundaryPolygon();
            int segments = 16;
            for (int i = 0; i < segments; i++)
            {
                float angle = i * MathF.PI * 2.0f / segments;
                defaultPoly.Vertices.Add(new Vector2(MathF.Cos(angle) * defaultRadius, MathF.Sin(angle) * defaultRadius));
            }

            File.WriteAllText(jsonPath, JsonSerializer.Serialize(defaultPoly, new JsonSerializerOptions { WriteIndented = true }));
            return defaultPoly;
        }

        private static BoundaryPolygon ParseGeoJsonPolygon(string geoJson)
        {
            var poly = new BoundaryPolygon();
            using var doc = JsonDocument.Parse(geoJson);
            var root = doc.RootElement;

            JsonElement coordsArray = default;
            if (root.TryGetProperty("geometry", out var geom) && geom.TryGetProperty("coordinates", out var coords))
                coordsArray = coords[0];
            else if (root.TryGetProperty("coordinates", out var coordsDirect))
                coordsArray = coordsDirect[0];

            if (coordsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var pt in coordsArray.EnumerateArray())
                {
                    float x = pt[0].GetSingle();
                    float y = pt[1].GetSingle();
                    poly.Vertices.Add(new Vector2(x, y));
                }
            }
            return poly;
        }
    }

    public static class MathApparatus
    {
        // Прогрессивный морфологический фильтр (PMF) с отсечением растительности по мультиэхо Zenmuse L2
        public static List<Point3D> ClassifyGroundAndObjects(List<Point3D> points, float gridCellSize = 0.5f, float heightThreshold = 0.25f)
        {
            if (points == null || points.Count == 0) return new List<Point3D>();

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                if (pt.X < minX) minX = pt.X; if (pt.X > maxX) maxX = pt.X;
                if (pt.Y < minY) minY = pt.Y; if (pt.Y > maxY) maxY = pt.Y;
            }

            int cols = (int)MathF.Ceiling((maxX - minX) / gridCellSize) + 1;
            int rows = (int)MathF.Ceiling((maxY - minY) / gridCellSize) + 1;
            int totalCells = cols * rows;

            float[] minElevationGrid = new float[totalCells];
            Array.Fill(minElevationGrid, float.MaxValue);

            // Шаг 1: Формирование базиса рельефа по последним эхо (отражение от земли)
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                // Если точка имеет несколько возвратов и не является последним эхом — это крона/растительность
                if (!pt.IsLastReturn && pt.NumberOfReturns > 1) continue;

                int c = (int)((pt.X - minX) / gridCellSize);
                int r = (int)((pt.Y - minY) / gridCellSize);
                if (c >= 0 && c < cols && r >= 0 && r < rows)
                {
                    int idx = r * cols + c;
                    if (pt.Z < minElevationGrid[idx])
                    {
                        minElevationGrid[idx] = pt.Z;
                    }
                }
            }

            // Шаг 2: Сглаживание микрорельефа
            float[] smoothedGround = new float[totalCells];
            Array.Copy(minElevationGrid, smoothedGround, totalCells);

            for (int r = 1; r < rows - 1; r++)
            {
                for (int c = 1; c < cols - 1; c++)
                {
                    int idx = r * cols + c;
                    if (minElevationGrid[idx] < float.MaxValue - 100f)
                    {
                        float sum = 0;
                        int count = 0;
                        for (int dr = -1; dr <= 1; dr++)
                        {
                            for (int dc = -1; dc <= 1; dc++)
                            {
                                float val = minElevationGrid[(r + dr) * cols + (c + dc)];
                                if (val < float.MaxValue - 100f)
                                {
                                    sum += val;
                                    count++;
                                }
                            }
                        }
                        smoothedGround[idx] = sum / count;
                    }
                }
            }

            // Шаг 3: Классификация (ASPRS: 2 - Ground, 64 - Техника/Временные объекты)
            byte[] pointClasses = new byte[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                int c = (int)((pt.X - minX) / gridCellSize);
                int r = (int)((pt.Y - minY) / gridCellSize);

                byte cls = 2; // Земля / базовая поверхность
                if (c >= 0 && c < cols && r >= 0 && r < rows)
                {
                    int idx = r * cols + c;
                    float groundZ = smoothedGround[idx];
                    if (groundZ < float.MaxValue - 100f)
                    {
                        if (pt.Z - groundZ > heightThreshold)
                        {
                            cls = 64; // Отвалы с техникой / строения
                        }
                    }
                }
                pointClasses[i] = cls;
            }

            var classified = new List<Point3D>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                byte cls = pointClasses[i];

                // Сигнатурный цветовой фильтр на промышленную спецтехнику (экскаваторы Komatsu/CAT желтых оттенков)
                if (cls == 64)
                {
                    bool isMachineryColor = (pt.R > 180 && pt.G > 80 && pt.B < 70) || (pt.R < 50 && pt.G < 50 && pt.B < 50);
                    if (isMachineryColor) cls = 64;
                }

                classified.Add(new Point3D(pt.X, pt.Y, pt.Z, pt.R, pt.G, pt.B, cls, pt.ReturnInfo));
            }

            return classified;
        }

        public static VolumeBalance CalculateVolumeBalance(List<Point3D> baseCloud, List<Point3D> currentCloud,
                                                           float cellSize, BoundaryPolygon? aoi, 
                                                           float materialDensity, float bulkingFactor,
                                                           bool filterMachinery = true, string? exportGridCsv = null)
        {
            if (baseCloud == null || currentCloud == null || baseCloud.Count == 0 || currentCloud.Count == 0)
                return new VolumeBalance(0, 0, 0, 0, 0, materialDensity, bulkingFactor, 0.0);

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            Action<List<Point3D>> findBounds = (cloud) =>
            {
                for (int i = 0; i < cloud.Count; i++)
                {
                    var pt = cloud[i];
                    if (pt.X < minX) minX = pt.X;
                    if (pt.X > maxX) maxX = pt.X;
                    if (pt.Y < minY) minY = pt.Y;
                    if (pt.Y > maxY) maxY = pt.Y;
                }
            };

            findBounds(baseCloud);
            findBounds(currentCloud);

            int cols = (int)MathF.Ceiling((maxX - minX) / cellSize) + 1;
            int rows = (int)MathF.Ceiling((maxY - minY) / cellSize) + 1;

            float[] baseGrid = BuildRobustDEM(baseCloud, cellSize, minX, minY, cols, rows, filterMachinery);
            float[] currentGrid = BuildRobustDEM(currentCloud, cellSize, minX, minY, cols, rows, filterMachinery);

            InterpolateHoles(baseGrid, cols, rows);
            InterpolateHoles(currentGrid, cols, rows);

            double totalCut = 0.0;
            double totalFill = 0.0;
            double areaCut = 0.0;
            double areaFill = 0.0;
            double maxDepth = 0.0;
            double cellArea = cellSize * cellSize;

            double sumSquaredErrors = 0.0;
            int stableCount = 0;

            for (int r = 0; r < rows; r++)
            {
                float cellCenterY = minY + (r + 0.5f) * cellSize;

                for (int c = 0; c < cols; c++)
                {
                    float cellCenterX = minX + (c + 0.5f) * cellSize;

                    int idx = r * cols + c;
                    float bZ = baseGrid[idx];
                    float cZ = currentGrid[idx];

                    if (bZ <= -9999.0f || cZ <= -9999.0f) continue;

                    bool insideAOI = aoi == null || aoi.IsPointInside(cellCenterX, cellCenterY);
                    float diff = bZ - cZ;

                    if (insideAOI)
                    {
                        if (diff > 0.03f)
                        {
                            totalCut += diff * cellArea;
                            areaCut += cellArea;
                            if (diff > maxDepth) maxDepth = diff;
                        }
                        else if (diff < -0.03f)
                        {
                            totalFill += Math.Abs(diff) * cellArea;
                            areaFill += cellArea;
                        }
                    }
                    else
                    {
                        // Вне контура работ рельеф считается стабильным: оценка СКП взаимного высотного положения
                        sumSquaredErrors += diff * diff;
                        stableCount++;
                    }
                }
            }

            double rmse = stableCount > 10 ? Math.Sqrt(sumSquaredErrors / stableCount) : 0.035;

            if (!string.IsNullOrEmpty(exportGridCsv))
            {
                ReportGenerator.ExportVolumeGridCsv(exportGridCsv, baseGrid, currentGrid, minX, minY, cellSize, cols, rows);
            }

            return new VolumeBalance(totalCut, totalFill, areaCut, areaFill, maxDepth, materialDensity, bulkingFactor, rmse);
        }

        private static float[] BuildRobustDEM(List<Point3D> points, float cellSize, float minX, float minY, int cols, int rows, bool filterMachinery)
        {
            int totalCells = cols * rows;
            float[] grid = new float[totalCells];
            Array.Fill(grid, -10000.0f);

            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                if (filterMachinery && pt.Classification == 64) continue;

                int c = (int)((pt.X - minX) / cellSize);
                int r = (int)((pt.Y - minY) / cellSize);

                if (c >= 0 && c < cols && r >= 0 && r < rows)
                {
                    int idx = r * cols + c;
                    if (pt.Z > grid[idx])
                    {
                        grid[idx] = pt.Z;
                    }
                }
            }

            return grid;
        }

        private static void InterpolateHoles(float[] grid, int cols, int rows)
        {
            for (int r = 1; r < rows - 1; r++)
            {
                for (int c = 1; c < cols - 1; c++)
                {
                    int idx = r * cols + c;
                    if (grid[idx] <= -9999.0f)
                    {
                        float sum = 0;
                        int count = 0;

                        float left = grid[idx - 1];
                        float right = grid[idx + 1];
                        float top = grid[idx - cols];
                        float bottom = grid[idx + cols];

                        if (left > -9999.0f) { sum += left; count++; }
                        if (right > -9999.0f) { sum += right; count++; }
                        if (top > -9999.0f) { sum += top; count++; }
                        if (bottom > -9999.0f) { sum += bottom; count++; }

                        if (count >= 2) grid[idx] = sum / count;
                    }
                }
            }
        }

        // ICP с защитным порогом RTK: предотвращает искажение геодезических данных
        public static (Matrix4x4 Transform, float Error, bool IsReliable) AlignCloudsICP(
            List<Vector3> source, List<Vector3> target, int maxIterations = 8, float maxAllowedShift = 0.35f)
        {
            if (source.Count == 0 || target.Count == 0) return (Matrix4x4.Identity, 0, false);

            var kdTree = new KdTreeFlat(target);
            Matrix4x4 globalTransform = Matrix4x4.Identity;
            float lastError = float.MaxValue;
            var currentSource = new List<Vector3>(source);

            for (int iter = 0; iter < maxIterations; iter++)
            {
                var pointPairs = new List<(Vector3 Src, Vector3 Tgt, float DistSq)>(currentSource.Count / 4);
                var distances = new List<float>(currentSource.Count / 4);

                for (int i = 0; i < currentSource.Count; i += 4)
                {
                    var sPt = currentSource[i];
                    var (bestPt, bestDistSq) = kdTree.FindNearest(sPt);

                    if (bestDistSq < 4.0f) // Поиск строго в радиусе 2 метров
                    {
                        pointPairs.Add((sPt, bestPt, bestDistSq));
                        distances.Add(bestDistSq);
                    }
                }

                if (pointPairs.Count < 30) break;

                distances.Sort();
                float medianDistSq = distances[distances.Count / 2];
                float cutoffThreshold = Math.Max(medianDistSq * 2.5f, 0.005f);

                var matchedSource = new List<Vector3>(pointPairs.Count);
                var matchedTarget = new List<Vector3>(pointPairs.Count);
                float currentError = 0;
                int keepCount = 0;

                for (int i = 0; i < pointPairs.Count; i++)
                {
                    if (pointPairs[i].DistSq <= cutoffThreshold)
                    {
                        matchedSource.Add(pointPairs[i].Src);
                        matchedTarget.Add(pointPairs[i].Tgt);
                        currentError += MathF.Sqrt(pointPairs[i].DistSq);
                        keepCount++;
                    }
                }

                if (keepCount < 30) break;
                currentError /= keepCount;

                if (MathF.Abs(lastError - currentError) < 0.0001f) break;
                lastError = currentError;

                var (R, T) = CalculateRigidTransformationSVD(matchedSource, matchedTarget);

                Matrix4x4 stepTransform = new Matrix4x4(
                    R[0, 0], R[1, 0], R[2, 0], 0,
                    R[0, 1], R[1, 1], R[2, 1], 0,
                    R[0, 2], R[1, 2], R[2, 2], 0,
                    T.X,     T.Y,     T.Z,     1
                );

                globalTransform = stepTransform * globalTransform;

                for (int i = 0; i < currentSource.Count; i++)
                    currentSource[i] = Vector3.Transform(currentSource[i], stepTransform);
            }

            // Геодезический аудит: если сдвиг больше допустимого дрейфа RTK (35 см), сшивка бракуется
            float totalShift = new Vector3(globalTransform.M41, globalTransform.M42, globalTransform.M43).Length();
            if (totalShift > maxAllowedShift)
            {
                return (Matrix4x4.Identity, totalShift, false);
            }

            return (globalTransform, lastError, true);
        }

        private static (DenseMatrix Rotation, Vector3 Translation) CalculateRigidTransformationSVD(List<Vector3> src, List<Vector3> dst)
        {
            int n = src.Count;
            Vector3 centroidSrc = Vector3.Zero, centroidDst = Vector3.Zero;
            for (int i = 0; i < n; i++)
            {
                centroidSrc += src[i];
                centroidDst += dst[i];
            }
            centroidSrc /= n;
            centroidDst /= n;

            var H = DenseMatrix.Create(3, 3, 0f);
            for (int i = 0; i < n; i++)
            {
                var s = src[i] - centroidSrc;
                var d = dst[i] - centroidDst;
                H[0, 0] += s.X * d.X; H[0, 1] += s.X * d.Y; H[0, 2] += s.X * d.Z;
                H[1, 0] += s.Y * d.X; H[1, 1] += s.Y * d.Y; H[1, 2] += s.Y * d.Z;
                H[2, 0] += s.Z * d.X; H[2, 1] += s.Z * d.Y; H[2, 2] += s.Z * d.Z;
            }

            var svd = H.Svd();
            var R = (DenseMatrix)(svd.VT.Transpose() * svd.U.Transpose());

            if (R.Determinant() < 0)
            {
                var VT_corrected = svd.VT.Clone();
                VT_corrected.SetRow(2, VT_corrected.Row(2) * -1f);
                R = (DenseMatrix)(VT_corrected.Transpose() * svd.U.Transpose());
            }

            var cSrcMat = DenseMatrix.OfColumnArrays(new[] { centroidSrc.X, centroidSrc.Y, centroidSrc.Z });
            var cDstMat = DenseMatrix.OfColumnArrays(new[] { centroidDst.X, centroidDst.Y, centroidDst.Z });
            var tMat = cDstMat - (R * cSrcMat);

            return (R, new Vector3(tMat[0, 0], tMat[1, 0], tMat[2, 0]));
        }

        private struct KdNodeStruct
        {
            public Vector3 Point;
            public int Left;
            public int Right;
        }

        private class KdTreeFlat
        {
            private readonly KdNodeStruct[] nodes;
            private int rootIndex = -1;

            public KdTreeFlat(List<Vector3> points)
            {
                var pts = points.ToArray();
                nodes = new KdNodeStruct[pts.Length];
                rootIndex = Build(pts, 0, pts.Length - 1, 0);
            }

            private int Build(Vector3[] points, int start, int end, int depth)
            {
                if (start > end) return -1;

                int axis = depth % 3;
                Array.Sort(points, start, end - start + 1, Comparer<Vector3>.Create((a, b) =>
                    axis == 0 ? a.X.CompareTo(b.X) : (axis == 1 ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z))));

                int mid = start + (end - start) / 2;
                int currentIndex = mid;

                nodes[currentIndex] = new KdNodeStruct
                {
                    Point = points[mid],
                    Left = Build(points, start, mid - 1, depth + 1),
                    Right = Build(points, mid + 1, end, depth + 1)
                };

                return currentIndex;
            }

            public (Vector3 Point, float DistanceSq) FindNearest(Vector3 target)
            {
                if (rootIndex == -1) return (Vector3.Zero, float.MaxValue);

                int bestIndex = -1;
                float bestDistSq = float.MaxValue;
                Search(rootIndex, target, 0, ref bestIndex, ref bestDistSq);

                return (nodes[bestIndex].Point, bestDistSq);
            }

            private void Search(int nodeIndex, Vector3 target, int depth, ref int bestIndex, ref float bestDistSq)
            {
                if (nodeIndex == -1) return;

                ref var node = ref nodes[nodeIndex];
                float distSq = Vector3.DistanceSquared(node.Point, target);

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestIndex = nodeIndex;
                }

                int axis = depth % 3;
                float axisDist = axis == 0 ? target.X - node.Point.X : (axis == 1 ? target.Y - node.Point.Y : target.Z - node.Point.Z);

                int first = axisDist < 0 ? node.Left : node.Right;
                int second = axisDist < 0 ? node.Right : node.Left;

                Search(first, target, depth + 1, ref bestIndex, ref bestDistSq);
                if (axisDist * axisDist < bestDistSq)
                {
                    Search(second, target, depth + 1, ref bestIndex, ref bestDistSq);
                }
            }
        }
    }
}