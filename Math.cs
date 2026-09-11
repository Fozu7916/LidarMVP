using System;
using System.Collections.Generic;
using System.Numerics;
using MathNet.Numerics.LinearAlgebra.Single;

namespace LidarProcessorMVP
{
    public readonly struct VolumeBalance
    {
        public readonly double CutVolume;   // Выемка (извлечено породы / снято коробок)
        public readonly double FillVolume;  // Насыпь (добавлено грунта / складировано)
        public readonly double NetVolume;   // Баланс (Cut - Fill)

        public VolumeBalance(double cut, double fill)
        {
            CutVolume = cut;
            FillVolume = fill;
            NetVolume = cut - fill;
        }
    }

    public static class MathApparatus
    {
        // =====================================================================
        // 1. ПРОМЫШЛЕННЫЙ МАРКШЕЙДЕРСКИЙ РАСЧЕТ CUT & FILL (2.5D DEM)
        // =====================================================================

        public static VolumeBalance CalculateVolumeBalance(List<Point3D> baseCloud, List<Point3D> currentCloud, float cellSize, bool onlyGround = false)
        {
            if (baseCloud == null || currentCloud == null || baseCloud.Count == 0 || currentCloud.Count == 0)
                return new VolumeBalance(0, 0);

            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;

            Action<List<Point3D>> findBounds = (cloud) =>
            {
                for (int i = 0; i < cloud.Count; i++)
                {
                    var pt = cloud[i];
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

            float[] baseGrid = BuildRobustDEM(baseCloud, cellSize, minX, minZ, cols, rows, onlyGround);
            float[] currentGrid = BuildRobustDEM(currentCloud, cellSize, minX, minZ, cols, rows, onlyGround);

            // Закрытие пробелов и теневых зон (IDW/Nearest Neighbor Interpolation)
            InterpolateHoles(baseGrid, cols, rows);
            InterpolateHoles(currentGrid, cols, rows);

            double totalCut = 0.0;
            double totalFill = 0.0;
            double cellArea = cellSize * cellSize;
            int totalCells = cols * rows;

            for (int i = 0; i < totalCells; i++)
            {
                float bH = baseGrid[i];
                float cH = currentGrid[i];

                if (bH > -9999.0f && cH > -9999.0f)
                {
                    float diff = bH - cH;

                    // Зона нечувствительности к инструментальному шуму лидара (±3 см)
                    if (diff > 0.03f)
                    {
                        totalCut += diff * cellArea;
                    }
                    else if (diff < -0.03f)
                    {
                        totalFill += Math.Abs(diff) * cellArea;
                    }
                }
            }

            return new VolumeBalance(totalCut, totalFill);
        }

        private static float[] BuildRobustDEM(List<Point3D> points, float cellSize, float minX, float minZ, int cols, int rows, bool onlyGround)
        {
            int totalCells = cols * rows;
            float[] grid = new float[totalCells];
            Array.Fill(grid, -10000.0f);

            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];

                // Если включен фильтр грунта, обрабатываем только ASPRS класс 2 (Ground)
                if (onlyGround && pt.Classification != 2) continue;

                int c = (int)((pt.X - minX) / cellSize);
                int r = (int)((pt.Z - minZ) / cellSize);

                if (c >= 0 && c < cols && r >= 0 && r < rows)
                {
                    int idx = r * cols + c;
                    // Для грунта берём минимальные точки или устойчивый максимум поверхности
                    if (pt.Y > grid[idx])
                    {
                        grid[idx] = pt.Y;
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

                        // Сбор 4-связных соседей
                        float left = grid[idx - 1];
                        float right = grid[idx + 1];
                        float top = grid[idx - cols];
                        float bottom = grid[idx + cols];

                        if (left > -9999.0f) { sum += left; count++; }
                        if (right > -9999.0f) { sum += right; count++; }
                        if (top > -9999.0f) { sum += top; count++; }
                        if (bottom > -9999.0f) { sum += bottom; count++; }

                        if (count >= 2)
                        {
                            grid[idx] = sum / count;
                        }
                    }
                }
            }
        }

        // =====================================================================
        // 2. ICP ВЫРАВНИВАНИЕ
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
                var pointPairs = new List<(Vector3 Src, Vector3 Tgt, float DistSq)>(currentSource.Count / 4);
                var distances = new List<float>(currentSource.Count / 4);

                for (int i = 0; i < currentSource.Count; i += 4)
                {
                    var sPt = currentSource[i];
                    var (bestPt, bestDistSq) = kdTree.FindNearest(sPt);

                    if (bestDistSq < 15.0f)
                    {
                        pointPairs.Add((sPt, bestPt, bestDistSq));
                        distances.Add(bestDistSq);
                    }
                }

                if (pointPairs.Count < 20) break;

                distances.Sort();
                float medianDistSq = distances[distances.Count / 2];
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