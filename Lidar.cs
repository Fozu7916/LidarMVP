using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace LidarProcessorMVP
{
    public class ProjectConfig
    {
        public double OriginX { get; set; } = 0.0;
        public double OriginY { get; set; } = 0.0;
        public double OriginZ { get; set; } = 0.0;
        public bool IsInitialized { get; set; } = false;
        public string CoordinateSystemInfo { get; set; } = "Local Geocentric Center";

        public static ProjectConfig LoadOrCreate(string directory)
        {
            string path = Path.Combine(directory, "project.json");
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var cfg = JsonSerializer.Deserialize<ProjectConfig>(json);
                    if (cfg != null && cfg.IsInitialized) return cfg;
                }
                catch { }
            }
            return new ProjectConfig();
        }

        public void Save(string directory)
        {
            string path = Path.Combine(directory, "project.json");
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct Point3D
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;
        public readonly byte Classification;
        public readonly byte ReturnInfo; // Биты 0-3: Return Number, Биты 4-7: Number of Returns (Спецификация DJI L2)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Point3D(float x, float y, float z, byte r, byte g, byte b, byte classification = 1, byte returnInfo = 0x11)
        {
            X = x; Y = y; Z = z; R = r; G = g; B = b; Classification = classification; ReturnInfo = returnInfo;
        }

        public byte ReturnNumber => (byte)(ReturnInfo & 0x0F);
        public byte NumberOfReturns => (byte)((ReturnInfo >> 4) & 0x0F);
        public bool IsLastReturn => ReturnNumber >= NumberOfReturns;
    }

    public class OctreeNodeMetadata
    {
        public string Id { get; set; } = string.Empty;
        public string BinFile { get; set; } = string.Empty;
        public float[] MinBounds { get; set; } = new float[3];
        public float[] MaxBounds { get; set; } = new float[3];
        public int PointCount { get; set; }
        public int Level { get; set; }
    }

    public class OctreeMetadata
    {
        public float[] GlobalMin { get; set; } = new float[3];
        public float[] GlobalMax { get; set; } = new float[3];
        public List<OctreeNodeMetadata> Nodes { get; set; } = new List<OctreeNodeMetadata>();
    }

    public static class LidarPipeline
    {
        public static ProjectConfig CurrentProject = new ProjectConfig();

        public static List<Point3D> LoadScanAuto(string filePath, string projectDir)
        {
            if (!CurrentProject.IsInitialized)
            {
                CurrentProject = ProjectConfig.LoadOrCreate(projectDir);
            }

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".las") return FastLoadLasFile(filePath, projectDir);
            return FastLoadPlyFile(filePath);
        }

        public static List<Point3D> FastLoadLasFile(string path, string projectDir)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 2 * 1024 * 1024);
            using var br = new BinaryReader(fs);

            byte[] signature = br.ReadBytes(4);
            if (signature[0] != 'L' || signature[1] != 'A' || signature[2] != 'S' || signature[3] != 'F')
                throw new InvalidDataException("Файл не является стандартом ASPRS LAS.");

            fs.Seek(96, SeekOrigin.Begin);
            uint offsetToPoints = br.ReadUInt32();
            uint numberOfVlrs = br.ReadUInt32();

            byte pointFormat = br.ReadByte();
            ushort pointRecordLength = br.ReadUInt16();
            uint legacyPointCount = br.ReadUInt32();

            fs.Seek(131, SeekOrigin.Begin);
            double scaleX = br.ReadDouble();
            double scaleY = br.ReadDouble();
            double scaleZ = br.ReadDouble();
            double offsetX = br.ReadDouble();
            double offsetY = br.ReadDouble();
            double offsetZ = br.ReadDouble();

            fs.Seek(179, SeekOrigin.Begin);
            double maxX = br.ReadDouble();
            double minX = br.ReadDouble();
            double maxY = br.ReadDouble();
            double minY = br.ReadDouble();
            double maxZ = br.ReadDouble();
            double minZ = br.ReadDouble();

            long totalPoints = legacyPointCount;
            if (totalPoints == 0 && fs.Length > 255)
            {
                fs.Seek(247, SeekOrigin.Begin);
                totalPoints = (long)br.ReadUInt64();
            }

            if (!CurrentProject.IsInitialized)
            {
                CurrentProject.OriginX = (minX + maxX) * 0.5;
                CurrentProject.OriginY = (minY + maxY) * 0.5;
                CurrentProject.OriginZ = minZ;
                CurrentProject.IsInitialized = true;
                CurrentProject.Save(projectDir);
            }

            double originX = CurrentProject.OriginX;
            double originY = CurrentProject.OriginY;
            double originZ = CurrentProject.OriginZ;

            var points = new List<Point3D>((int)Math.Min(totalPoints, 10_000_000));
            fs.Seek(offsetToPoints, SeekOrigin.Begin);

            byte[] recordBuffer = new byte[pointRecordLength];

            // Настройка смещений для стандартов LAS 1.2-1.4 (DJI Zenmuse L2 нативно пишет формат 6-8)
            int classOffset = 15;
            int returnByteOffset = 14;
            int rgbOffset = -1;

            if (pointFormat == 2) { rgbOffset = 20; classOffset = 15; returnByteOffset = 14; }
            else if (pointFormat == 3) { rgbOffset = 28; classOffset = 15; returnByteOffset = 14; }
            else if (pointFormat >= 6 && pointFormat <= 10)
            {
                // LAS 1.4: 6-10 форматы точки
                returnByteOffset = 14;
                classOffset = 16;
                if (pointFormat == 7 || pointFormat == 8 || pointFormat == 10) rgbOffset = 30;
            }

            for (long i = 0; i < totalPoints; i++)
            {
                int read = fs.Read(recordBuffer, 0, pointRecordLength);
                if (read < pointRecordLength) break;

                int rawX = BinaryPrimitives.ReadInt32LittleEndian(recordBuffer.AsSpan(0, 4));
                int rawY = BinaryPrimitives.ReadInt32LittleEndian(recordBuffer.AsSpan(4, 4));
                int rawZ = BinaryPrimitives.ReadInt32LittleEndian(recordBuffer.AsSpan(8, 4));

                double realX = (rawX * scaleX) + offsetX;
                double realY = (rawY * scaleY) + offsetY;
                double realZ = (rawZ * scaleZ) + offsetZ;

                float localX = (float)(realX - originX);
                float localY = (float)(realY - originY);
                float localZ = (float)(realZ - originZ);

                byte classification = 1;
                if (classOffset < pointRecordLength)
                {
                    classification = recordBuffer[classOffset];
                    if (pointFormat < 6) classification = (byte)(classification & 0x1F);
                }

                byte returnInfo = 0x11;
                if (returnByteOffset < pointRecordLength)
                {
                    returnInfo = recordBuffer[returnByteOffset];
                }

                byte r = 200, g = 200, b = 200;
                if (rgbOffset > 0 && rgbOffset + 6 <= pointRecordLength)
                {
                    ushort rawR = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.AsSpan(rgbOffset, 2));
                    ushort rawG = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.AsSpan(rgbOffset + 2, 2));
                    ushort rawB = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.AsSpan(rgbOffset + 4, 2));
                    r = (byte)(rawR >> 8);
                    g = (byte)(rawG >> 8);
                    b = (byte)(rawB >> 8);
                }

                points.Add(new Point3D(localX, localZ, localY, r, g, b, classification, returnInfo));
            }

            return points;
        }

        public static List<Point3D> FastLoadPlyFile(string path)
        {
            const int BufferSize = 1024 * 1024;
            var points = new List<Point3D>(128000);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            byte[] buffer = new byte[BufferSize];
            int bytesRead, remainingBytes = 0;
            bool inHeader = true;

            while ((bytesRead = fs.Read(buffer, remainingBytes, buffer.Length - remainingBytes)) > 0)
            {
                int totalBytes = remainingBytes + bytesRead;
                var span = buffer.AsSpan(0, totalBytes);
                int lineStart = 0;

                for (int i = 0; i < totalBytes; i++)
                {
                    if (span[i] == (byte)'\n')
                    {
                        var line = span.Slice(lineStart, i - lineStart);
                        if (line.Length > 0 && line[^1] == (byte)'\r')
                            line = line.Slice(0, line.Length - 1);

                        if (inHeader)
                        {
                            if (line.SequenceEqual("end_header"u8)) inHeader = false;
                        }
                        else if (line.Length > 0)
                        {
                            if (TryParsePoint(line, out Point3D pt)) points.Add(pt);
                        }
                        lineStart = i + 1;
                    }
                }
                remainingBytes = totalBytes - lineStart;
                if (remainingBytes > 0) span.Slice(lineStart, remainingBytes).CopyTo(buffer);
            }
            return points;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryParsePoint(ReadOnlySpan<byte> line, out Point3D point)
        {
            point = default;
            int offset = 0;
            if (!SkipWhitespaces(line, ref offset) || !Utf8Parser.TryParse(line.Slice(offset), out float x, out int c)) return false;
            offset += c;
            if (!SkipWhitespaces(line, ref offset) || !Utf8Parser.TryParse(line.Slice(offset), out float y, out c)) return false;
            offset += c;
            if (!SkipWhitespaces(line, ref offset) || !Utf8Parser.TryParse(line.Slice(offset), out float z, out c)) return false;
            offset += c;

            byte r = 255, g = 255, b = 255;
            if (SkipWhitespaces(line, ref offset) && Utf8Parser.TryParse(line.Slice(offset), out byte pr, out c))
            {
                r = pr; offset += c;
                if (SkipWhitespaces(line, ref offset) && Utf8Parser.TryParse(line.Slice(offset), out byte pg, out c))
                {
                    g = pg; offset += c;
                    if (SkipWhitespaces(line, ref offset) && Utf8Parser.TryParse(line.Slice(offset), out byte pb, out c)) b = pb;
                }
            }
            point = new Point3D(x, y, z, r, g, b, 1);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SkipWhitespaces(ReadOnlySpan<byte> span, ref int offset)
        {
            while (offset < span.Length && (span[offset] == (byte)' ' || span[offset] == (byte)'\t')) offset++;
            return offset < span.Length;
        }

        public static List<Point3D> VoxelFilter(List<Point3D> points, float leafSize)
        {
            if (leafSize <= 0.0f || points.Count == 0) return points;

            float invLeaf = 1.0f / leafSize;
            int count = points.Count;

            var items = new (long Key, int Index)[count];

            Parallel.For(0, count, i =>
            {
                var pt = points[i];
                long vx = (int)MathF.Floor(pt.X * invLeaf);
                long vy = (int)MathF.Floor(pt.Y * invLeaf);
                long vz = (int)MathF.Floor(pt.Z * invLeaf);

                long key = ((vx & 0x1FFFFF) << 42) | ((vy & 0x1FFFFF) << 21) | (vz & 0x1FFFFF);
                items[i] = (key, i);
            });

            Array.Sort(items, (a, b) => a.Key.CompareTo(b.Key));

            var result = new List<Point3D>(count / 2);
            long prevKey = long.MinValue;

            for (int i = 0; i < count; i++)
            {
                if (items[i].Key != prevKey)
                {
                    result.Add(points[items[i].Index]);
                    prevKey = items[i].Key;
                }
            }

            return result;
        }

        public static void ExportToBinary(List<Point3D> points, string outputPath)
        {
            const int ChunkSize = 65536;
            byte[] buffer = new byte[ChunkSize * 16];
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
            int idx = 0;

            while (idx < points.Count)
            {
                int count = Math.Min(ChunkSize, points.Count - idx);
                int offset = 0;
                for (int i = 0; i < count; i++)
                {
                    var pt = points[idx + i];
                    Unsafe.WriteUnaligned(ref buffer[offset], pt.X);
                    Unsafe.WriteUnaligned(ref buffer[offset + 4], pt.Y);
                    Unsafe.WriteUnaligned(ref buffer[offset + 8], pt.Z);
                    buffer[offset + 12] = pt.R;
                    buffer[offset + 13] = pt.G;
                    buffer[offset + 14] = pt.B;
                    buffer[offset + 15] = pt.Classification;
                    offset += 16;
                }
                fs.Write(buffer, 0, offset);
                idx += count;
            }
        }

        public static OctreeMetadata BuildAndExportOctreeLOD(List<Point3D> points, string outputDir, string epochPrefix, int maxPointsPerNode = 50000)
        {
            var meta = new OctreeMetadata();
            if (points == null || points.Count == 0) return meta;

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                if (pt.X < minX) minX = pt.X; if (pt.X > maxX) maxX = pt.X;
                if (pt.Y < minY) minY = pt.Y; if (pt.Y > maxY) maxY = pt.Y;
                if (pt.Z < minZ) minZ = pt.Z; if (pt.Z > maxZ) maxZ = pt.Z;
            }

            meta.GlobalMin = new[] { minX, minY, minZ };
            meta.GlobalMax = new[] { maxX, maxY, maxZ };

            var rootSample = new List<Point3D>(Math.Min(points.Count, maxPointsPerNode));
            int step = Math.Max(1, points.Count / maxPointsPerNode);
            for (int i = 0; i < points.Count; i += step)
            {
                rootSample.Add(points[i]);
            }

            string rootBin = $"{epochPrefix}_lod0_root.bin";
            ExportToBinary(rootSample, Path.Combine(outputDir, rootBin));

            meta.Nodes.Add(new OctreeNodeMetadata
            {
                Id = "root",
                BinFile = rootBin,
                Level = 0,
                PointCount = rootSample.Count,
                MinBounds = new[] { minX, minY, minZ },
                MaxBounds = new[] { maxX, maxY, maxZ }
            });

            float midX = (minX + maxX) * 0.5f;
            float midY = (minY + maxY) * 0.5f;
            float midZ = (minZ + maxZ) * 0.5f;

            var octantLists = new List<Point3D>[8];
            for (int o = 0; o < 8; o++) octantLists[o] = new List<Point3D>(points.Count / 8);

            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                int octIdx = (p.X >= midX ? 1 : 0) | ((p.Y >= midY ? 1 : 0) << 1) | ((p.Z >= midZ ? 1 : 0) << 2);
                octantLists[octIdx].Add(p);
            }

            for (int o = 0; o < 8; o++)
            {
                if (octantLists[o].Count == 0) continue;

                string nodeBin = $"{epochPrefix}_node_{o}.bin";
                ExportToBinary(octantLists[o], Path.Combine(outputDir, nodeBin));

                float bMinX = (o & 1) != 0 ? midX : minX;
                float bMaxX = (o & 1) != 0 ? maxX : midX;
                float bMinY = (o & 2) != 0 ? midY : minY;
                float bMaxY = (o & 2) != 0 ? maxY : midY;
                float bMinZ = (o & 4) != 0 ? midZ : minZ;
                float bMaxZ = (o & 4) != 0 ? maxZ : midZ;

                meta.Nodes.Add(new OctreeNodeMetadata
                {
                    Id = $"node_{o}",
                    BinFile = nodeBin,
                    Level = 1,
                    PointCount = octantLists[o].Count,
                    MinBounds = new[] { bMinX, bMinY, bMinZ },
                    MaxBounds = new[] { bMaxX, bMaxY, bMaxZ }
                });
            }

            return meta;
        }
    }
}