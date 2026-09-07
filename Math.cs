using System;
using System.Collections.Generic;
using System.Numerics;
using MathNet.Numerics.LinearAlgebra.Single;

namespace LidarProcessorMVP
{
    public static class MathApparatus
    {
        // =====================================================================
        // 1. ИСТИННЫЙ МАРКШЕЙДЕРСКИЙ РАСЧЕТ (Cell-to-Cell / Grid-to-Grid)
        // =====================================================================
        
        public static double CalculateVolumeDifference(List<Point3D> baseCloud, List<Point3D> currentCloud, float cellSize)
        {
            if (baseCloud == null || currentCloud == null || baseCloud.Count == 0 || currentCloud.Count == 0) return 0.0;

            // Строим ЦМР (Цифровую модель рельефа) для базовой эпохи
            var baseGrid = BuildDEM(baseCloud, cellSize);
            // Строим ЦМР для текущей эпохи
            var currentGrid = BuildDEM(currentCloud, cellSize);

            double extractedVolume = 0.0;
            double cellArea = cellSize * cellSize;

            // Сравниваем ячейку с ячейкой. Ищем только места, где уровень упал (выемка породы)
            foreach (var kvp in baseGrid)
            {
                var key = kvp.Key;
                float baseHeight = kvp.Value;

                if (currentGrid.TryGetValue(key, out float currentHeight))
                {
                    float heightDiff = baseHeight - currentHeight;
                    
                    // Если разница больше 5 см (защита от шума лидара), считаем это выработанным объемом
                    if (heightDiff > 0.05f) 
                    {
                        extractedVolume += heightDiff * cellArea;
                    }
                }
            }

            return extractedVolume;
        }

        private static Dictionary<(int, int), float> BuildDEM(List<Point3D> points, float cellSize)
        {
            var grid = new Dictionary<(int, int), float>();
            foreach (var pt in points)
            {
                int gridX = (int)MathF.Floor(pt.X / cellSize);
                int gridZ = (int)MathF.Floor(pt.Z / cellSize);
                var key = (gridX, gridZ);

                if (!grid.TryGetValue(key, out float currentMaxY) || pt.Y > currentMaxY)
                    grid[key] = pt.Y;
            }
            return grid;
        }

        // =====================================================================
        // 2. ЖЕСТКИЙ TRIMMED ICP (ДЛЯ RTK ДРОНОВ)
        // =====================================================================

        public static (Matrix4x4 Transform, float Error) AlignCloudsICP(List<Vector3> source, List<Vector3> target, int maxIterations = 8)
        {
            if (source.Count == 0 || target.Count == 0) return (Matrix4x4.Identity, 0);

            var kdTree = new KdTree(target);
            Matrix4x4 globalTransform = Matrix4x4.Identity;
            float lastError = float.MaxValue;
            var currentSource = new List<Vector3>(source);

            // ЖЕСТКОЕ ОТСЕЧЕНИЕ: Доверяем только 50% точек (считаем их нетронутым бортом), 
            // остальные 50% (экскаваторы, ямы, шум) полностью игнорируем при расчете матрицы.
            float overlapRatio = 0.50f; 

            for (int iter = 0; iter < maxIterations; iter++)
            {
                var pointPairs = new List<(Vector3 Src, Vector3 Tgt, float DistSq)>();

                for (int i = 0; i < currentSource.Count; i += 5) // Прореживание
                {
                    var sPt = currentSource[i];
                    var (bestPt, bestDistSq) = kdTree.FindNearest(sPt);

                    if (bestDistSq < 4.0f) 
                    {
                        pointPairs.Add((sPt, bestPt, bestDistSq));
                    }
                }

                if (pointPairs.Count < 20) break;

                // Сортировка по возрастанию ошибки (от лучших совпадений к худшим)
                pointPairs.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));

                // Отрезаем хвост (зону выработки)
                int keepCount = (int)(pointPairs.Count * overlapRatio);
                
                var matchedSource = new List<Vector3>(keepCount);
                var matchedTarget = new List<Vector3>(keepCount);
                float currentError = 0;

                for (int i = 0; i < keepCount; i++)
                {
                    matchedSource.Add(pointPairs[i].Src);
                    matchedTarget.Add(pointPairs[i].Tgt);
                    currentError += MathF.Sqrt(pointPairs[i].DistSq);
                }

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

        private class KdNode
        {
            public Vector3 Point;
            public KdNode? Left, Right;
            public KdNode(Vector3 pt) => Point = pt;
        }

        private class KdTree
        {
            private readonly KdNode? root;

            public KdTree(List<Vector3> points)
            {
                var pts = points.ToArray();
                root = Build(pts, 0, pts.Length - 1, 0);
            }

            private KdNode? Build(Vector3[] points, int start, int end, int depth)
            {
                if (start > end) return null;
                int axis = depth % 3;

                Array.Sort(points, start, end - start + 1, Comparer<Vector3>.Create((a, b) =>
                    axis == 0 ? a.X.CompareTo(b.X) : (axis == 1 ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z))));

                int mid = start + (end - start) / 2;
                return new KdNode(points[mid])
                {
                    Left = Build(points, start, mid - 1, depth + 1),
                    Right = Build(points, mid + 1, end, depth + 1)
                };
            }

            public (Vector3 Point, float DistanceSq) FindNearest(Vector3 target)
            {
                KdNode? bestNode = null;
                float bestDistSq = float.MaxValue;
                Search(root, target, 0, ref bestNode, ref bestDistSq);
                return (bestNode?.Point ?? Vector3.Zero, bestDistSq);
            }

            private void Search(KdNode? node, Vector3 target, int depth, ref KdNode? bestNode, ref float bestDistSq)
            {
                if (node == null) return;
                float distSq = Vector3.DistanceSquared(node.Point, target);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestNode = node;
                }

                int axis = depth % 3;
                float axisDist = axis == 0 ? target.X - node.Point.X : (axis == 1 ? target.Y - node.Point.Y : target.Z - node.Point.Z);

                KdNode? first = axisDist < 0 ? node.Left : node.Right;
                KdNode? second = axisDist < 0 ? node.Right : node.Left;

                Search(first, target, depth + 1, ref bestNode, ref bestDistSq);
                if (axisDist * axisDist < bestDistSq)
                    Search(second, target, depth + 1, ref bestNode, ref bestDistSq);
            }
        }
    }
}