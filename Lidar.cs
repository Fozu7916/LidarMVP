using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace LidarProcessorMVP
{
    public readonly struct Point3D
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Point3D(float x, float y, float z, byte r, byte g, byte b)
        {
            X = x; Y = y; Z = z; R = r; G = g; B = b;
        }
    }

    public static class LidarPipeline
    {
        public static double GlobalOriginX = 0.0;
        public static double GlobalOriginY = 0.0;
        public static double GlobalOriginZ = 0.0;
        public static bool HasGlobalOrigin = false;

        public static List<Point3D> LoadScanAuto(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".las") return FastLoadLasFile(filePath);
            return FastLoadPlyFile(filePath);
        }

        public static List<Point3D> FastLoadLasFile(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 2 * 1024 * 1024);
            using var br = new BinaryReader(fs);

            byte[] signature = br.ReadBytes(4);
            if (signature[0] != 'L' || signature[1] != 'A' || signature[2] != 'S' || signature[3] != 'F')
                throw new InvalidDataException("Файл не является стандартом ASPRS LAS.");

            fs.Seek(96, SeekOrigin.Begin);
            uint offsetToPoints = br.ReadUInt32();
            
            fs.Seek(104, SeekOrigin.Begin);
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

            long totalPoints = legacyPointCount;
            if (totalPoints == 0 && fs.Length > 255)
            {
                fs.Seek(247, SeekOrigin.Begin);
                totalPoints = (long)br.ReadUInt64();
            }

            var points = new List<Point3D>((int)Math.Min(totalPoints, 5_000_000));
            fs.Seek(offsetToPoints, SeekOrigin.Begin);

            byte[] recordBuffer = new byte[pointRecordLength];

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

                if (!HasGlobalOrigin)
                {
                    GlobalOriginX = realX;
                    GlobalOriginY = realY;
                    GlobalOriginZ = realZ;
                    HasGlobalOrigin = true;
                }

                float localX = (float)(realX - GlobalOriginX);
                float localY = (float)(realY - GlobalOriginY);
                float localZ = (float)(realZ - GlobalOriginZ);

                byte r = 200, g = 200, b = 200;
                int rgbOffset = -1;

                // Поддержка LAS 1.4 форматов (Point Format 6-10)
                if (pointFormat == 2) rgbOffset = 20;
                else if (pointFormat == 3) rgbOffset = 28;
                else if (pointFormat == 7 || pointFormat == 8) rgbOffset = 30;
                else if (pointFormat == 9 || pointFormat == 10) rgbOffset = 30; // Экстра-байты начинаются дальше, но RGB на 30

                if (rgbOffset > 0 && rgbOffset + 6 <= pointRecordLength)
                {
                    ushort rawR = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.AsSpan(rgbOffset, 2));
                    ushort rawG = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.AsSpan(rgbOffset + 2, 2));
                    ushort rawB = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.AsSpan(rgbOffset + 4, 2));
                    r = (byte)(rawR >> 8);
                    g = (byte)(rawG >> 8);
                    b = (byte)(rawB >> 8);
                }

                points.Add(new Point3D(localX, localZ, localY, r, g, b)); 
            }

            return points;
        }

        public static List<Point3D> FastLoadPlyFile(string path)
        {
            // Оставлено без изменений (логика парсинга PLY оптимальна)
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
            point = new Point3D(x, y, z, r, g, b);
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
            if (leafSize <= 0.0f) return points;
            float invLeaf = 1.0f / leafSize;
            var voxelMap = new ConcurrentDictionary<long, Point3D>(Environment.ProcessorCount * 2, points.Count);

            Parallel.ForEach(points, pt =>
            {
                int vx = (int)MathF.Floor(pt.X * invLeaf);
                int vy = (int)MathF.Floor(pt.Y * invLeaf);
                int vz = (int)MathF.Floor(pt.Z * invLeaf);

                long key = ((long)(vx & 0x1FFFFF) << 42) | ((long)(vy & 0x1FFFFF) << 21) | ((long)(vz & 0x1FFFFF));
                voxelMap.TryAdd(key, pt);
            });

            return new List<Point3D>(voxelMap.Values);
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
                    buffer[offset + 15] = 255;
                    offset += 16;
                }
                fs.Write(buffer, 0, offset);
                idx += count;
            }
        }
    }
}