using System;
using System.Collections.Generic;
using System.Numerics;
using MathNet.Numerics.LinearAlgebra.Single;

namespace LidarProcessorMVP
{
    public static class MathApparatus
    {
        // =====================================================================
        // 1. ИСТИННЫЙ МАРКШЕЙДЕРСКИЙ РАСЧЕТ (Flat 1D Array DEM)
        // =====================================================================
        
        public static double CalculateVolumeDifference(List<Point3D> baseCloud, List<Point3D> currentCloud, float cellSize)
        {
            if (baseCloud == null || currentCloud == null || baseCloud.Count == 0 || currentCloud.Count == 0) return 0.0;

            // Находим общий Bounding Box для обеих эпох для точного совпадения ячеек
            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;

            Action<List<Point3D>> findBounds = (cloud) => {
                foreach (var pt in cloud) {
                    if (pt.X < minX) minX = pt.X;
                    if (pt.X > maxX) maxX = pt.X;
                    if (pt.Z < minZ) minZ = pt.Z;
                    if (pt.Z > maxZ) maxZ = pt.Z;
                }
            };

            findBounds(baseCloud);
            findBounds(currentCloud);

            int cols = (int)MathF.Ceiling((maxX - minX) / cellSize) + 1;
            int rows = (int)MathF.Ceiling((maxZ - minZ) / cellSize) + 1;

            float[] baseGrid = BuildDEMArray(baseCloud, cellSize, minX, minZ, cols, rows);
            float[] currentGrid = BuildDEMArray(currentCloud, cellSize, minX, minZ, cols, rows);

            double extractedVolume = 0.0;
            double cellArea = cellSize * cellSize;
            int totalCells = cols * rows;

            for (int i = 0; i < totalCells; i++)
            {
                float bHeight = baseGrid[i];
                float cHeight = currentGrid[i];

                if (bHeight != float.MinValue && cHeight != float.MinValue)
                {
                    float heightDiff = bHeight - cHeight;
                    // Робастный фильтр: игнорируем шум лидара (до 5 см)
                    if (heightDiff > 0.05f) 
                    {
                        extractedVolume += heightDiff * cellArea;
                    }
                }
            }

            return extractedVolume;
        }

        private static float[] BuildDEMArray(List<Point3D> points, float cellSize, float minX, float minZ, int cols, int rows)
        {
            float[] grid = new float[cols * rows];
            Array.Fill(grid, float.MinValue);

            foreach (var pt in points)
            {
                int c = (int)((pt.X - minX) / cellSize);
                int r = (int)((pt.Z - minZ) / cellSize);
                
                if (c >= 0 && c < cols && r >= 0 && r < rows)
                {
                    int idx = r * cols + c;
                    if (pt.Y > grid[idx]) grid[idx] = pt.Y;
                }
            }
            return grid;
        }

        // =====================================================================
        // 2. ЖЕСТКИЙ TRIMMED ICP (ДЛЯ RTK ДРОНОВ)
        // =====================================================================

        public static (Matrix4x4 Transform, float Error) AlignCloudsICP(List<Vector3> source, List<Vector3> target, int maxIterations = 8)
        {
            if (source.Count == 0 || target.Count == 0) return (Matrix4x4.Identity, 0);

            var kdTree = new KdTreeFlat(target);
            Matrix4x4 globalTransform = Matrix4x4.Identity;
            float lastError = float.MaxValue;
            var currentSource = new List<Vector3>(source);

            for (int iter = 0; iter < maxIterations; iter++)
            {
                var pointPairs = new List<(Vector3 Src, Vector3 Tgt, float DistSq)>(currentSource.Count / 5);
                var distances = new List<float>(currentSource.Count / 5);

                // Равномерное прореживание через хэширование (Spatial Subsampling)
                // Для MVP берем просто каждую 5-ю, но в продакшене нужен Voxel Grid для Source.
                for (int i = 0; i < currentSource.Count; i += 5) 
                {
                    var sPt = currentSource[i];
                    var (bestPt, bestDistSq) = kdTree.FindNearest(sPt);

                    if (bestDistSq < 10.0f) // Отбрасываем откровенные промахи на этапе поиска
                    {
                        pointPairs.Add((sPt, bestPt, bestDistSq));
                        distances.Add(bestDistSq);
                    }
                }

                if (pointPairs.Count < 20) break;

                // ДИНАМИЧЕСКОЕ ОТСЕЧЕНИЕ (Динамический порог на основе распределения ошибок)
                distances.Sort();
                // Находим медианную ошибку. Ожидаем, что большая часть карьера не изменилась.
                float medianDistSq = distances[distances.Count / 2];
                // Отсекаем все, что превышает медиану более чем в 3 раза (робастное отклонение)
                float cutoffThreshold = Math.Max(medianDistSq * 3.0f, 0.01f); 

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

                if (keepCount < 20) break;
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

            return (globalTransform, lastError);
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

        // =====================================================================
        // 3. FLAT ARRAY KD-TREE (БЕЗ АЛЛОКАЦИЙ В КУЧЕ GC)
        // =====================================================================
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
                
                int currentIndex = mid; // Используем индекс mid как идентификатор ноды
                
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