using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Altemiq.IO.Las;

namespace DroneLiDAR
{
    public class ProjectConfig
    {
        public double OriginX { get; set; } = 0.0;
        public double OriginY { get; set; } = 0.0;
        public double OriginZ { get; set; } = 0.0;
        public bool IsInitialized { get; set; } = false;
        public string CoordinateSystemInfo { get; set; } = "МСК / локальный сдвиг от базиса первого скана";
        public float MaterialDensity { get; set; } = 1.65f;
        public float BulkingFactor { get; set; } = 1.20f;
        public string SensorInfo { get; set; } = "БПЛА с RTK + LiDAR-сенсор";
        public string CoordinateSystemWkt { get; set; } = string.Empty;
        public bool IsSynthetic { get; set; }

        public static ProjectConfig LoadOrCreate(string directory)
        {
            string path = Path.Combine(directory, "project.json");
            if (File.Exists(path))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<ProjectConfig>(File.ReadAllText(path));
                    if (cfg == null) throw new JsonException("Пустой JSON.");
                    cfg.Validate();
                    return cfg;
                }
                catch (Exception ex) when (ex is JsonException
                    || ex is InvalidDataException
                    || ex is IOException
                    || ex is UnauthorizedAccessException)
                {
                    throw new InvalidDataException(
                        $"Не удалось прочитать параметры проекта {path}: {ex.Message}",
                        ex);
                }
            }
            return new ProjectConfig();
        }

        public void Save(string directory)
        {
            Validate();
            string path = Path.Combine(directory, "project.json");
            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, true);
        }

        private void Validate()
        {
            if (!double.IsFinite(OriginX) || !double.IsFinite(OriginY) || !double.IsFinite(OriginZ))
                throw new InvalidDataException("Origin проекта содержит нечисловые координаты.");
            if (!float.IsFinite(MaterialDensity) || MaterialDensity <= 0)
                throw new InvalidDataException("Плотность материала должна быть положительной.");
            if (!float.IsFinite(BulkingFactor) || BulkingFactor < 1.0f)
                throw new InvalidDataException("Коэффициент разрыхления должен быть не меньше 1.");
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
        public readonly byte ReturnInfo;

        public const byte ClassUnclassified = 1;
        public const byte ClassGround = 2;
        public const byte ClassLowVeg = 3;
        public const byte ClassMediumVeg = 4;
        public const byte ClassHighVeg = 5;
        public const byte ClassBuilding = 6;
        public const byte ClassNoise = 7;
        public const byte ClassWater = 9;
        public const byte ClassOverlap = 12;
        public const byte ClassBridge = 17;
        public const byte ClassHighNoise = 18;
        public const byte ClassMachinery = 64;
        public const byte ClassMaterial = 65;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Point3D(float x, float y, float z, byte r, byte g, byte b, byte classification = 1, byte returnInfo = 0x11)
        {
            X = x; Y = y; Z = z; R = r; G = g; B = b; Classification = classification; ReturnInfo = returnInfo;
        }

        public byte ReturnNumber => (byte)(ReturnInfo & 0x0F);
        public byte NumberOfReturns => (byte)((ReturnInfo >> 4) & 0x0F);
        public bool IsLastReturn => NumberOfReturns <= 1 || ReturnNumber >= NumberOfReturns;

        public static bool IsExcludedFromDem(byte classification)
        {
            return classification != 0
                && classification != ClassUnclassified
                && classification != ClassGround
                && classification != ClassMaterial;
        }

        public static bool IsRemovedFromCleanView(byte classification)
        {
            return classification == ClassLowVeg
                || classification == ClassMediumVeg
                || classification == ClassHighVeg
                || classification == ClassNoise
                || classification == ClassHighNoise
                || classification == ClassOverlap
                || classification == ClassMachinery;
        }

        public static int ClassPriority(byte classification)
        {
            if (classification == ClassMachinery) return 100;
            if (classification == ClassNoise || classification == ClassHighNoise) return 90;
            if (classification == ClassBuilding || classification == ClassBridge) return 80;
            if (classification == ClassHighVeg || classification == ClassMediumVeg || classification == ClassLowVeg) return 70;
            if (classification == ClassWater) return 60;
            if (classification == ClassMaterial) return 50;
            if (classification == ClassGround) return 40;
            return 10;
        }
    }

    public sealed class LasFileMetadata
    {
        public string FileName { get; set; } = string.Empty;
        public string SourceFormat { get; set; } = string.Empty;
        public bool IsCompressed { get; set; }
        public string Version { get; set; } = string.Empty;
        public byte PointFormat { get; set; }
        public ushort PointRecordLength { get; set; }
        public long DeclaredPointCount { get; set; }
        public long LoadedPointCount { get; set; }
        public bool HasRgb { get; set; }
        public string CoordinateSystemWkt { get; set; } = string.Empty;
        public bool IsSynthetic { get; set; }
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
        public const int DefaultMaxLoadPoints = 8_000_000;
        public static ProjectConfig CurrentProject = new ProjectConfig();
        public static LasFileMetadata LastMetadata { get; private set; } = new LasFileMetadata();

        public static List<Point3D> LoadScanAuto(string filePath, string projectDir, int maxPoints = DefaultMaxLoadPoints)
        {
            if (!CurrentProject.IsInitialized)
                CurrentProject = ProjectConfig.LoadOrCreate(projectDir);

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".laz") return FastLoadLazFile(filePath, projectDir, maxPoints);
            if (ext == ".las") return FastLoadLasFile(filePath, projectDir, maxPoints);
            return FastLoadPlyFile(filePath, maxPoints);
        }

        public static List<Point3D> FastLoadLasFile(string path, string projectDir, int maxPoints = DefaultMaxLoadPoints)
        {
            if (maxPoints <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPoints), "Лимит точек должен быть положительным.");
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.SequentialScan);
            using var br = new BinaryReader(fs);

            byte[] signature = br.ReadBytes(4);
            if (signature.Length < 4 || signature[0] != 'L' || signature[1] != 'A' || signature[2] != 'S' || signature[3] != 'F')
                throw new InvalidDataException($"Файл не является ASPRS LAS: {Path.GetFileName(path)}");

            fs.Seek(24, SeekOrigin.Begin);
            byte versionMajor = br.ReadByte();
            byte versionMinor = br.ReadByte();

            fs.Seek(94, SeekOrigin.Begin);
            ushort headerSize = br.ReadUInt16();
            uint offsetToPoints = br.ReadUInt32();
            uint numberOfVlrs = br.ReadUInt32();
            byte rawPointFormat = br.ReadByte();
            bool compressedPoints = (rawPointFormat & 0x80) != 0;
            byte pointFormat = (byte)(rawPointFormat & 0x3F);
            ushort pointRecordLength = br.ReadUInt16();
            uint legacyPointCount = br.ReadUInt32();

            if (compressedPoints)
                throw new InvalidDataException(
                    "LAS содержит сжатые LAZ-записи. Экспортируйте несжатый LAS.");
            if (pointFormat > 10)
                throw new InvalidDataException($"Формат записи точки LAS {pointFormat} не поддерживается.");
            if (versionMajor != 1 || versionMinor > 4 || versionMinor < 2)
                throw new InvalidDataException($"Версия LAS {versionMajor}.{versionMinor} не поддерживается.");
            if ((pointFormat == 4 || pointFormat == 5) && versionMinor < 3)
                throw new InvalidDataException($"Point format {pointFormat} требует LAS 1.3 или новее.");
            if (pointFormat >= 6 && versionMinor < 4)
                throw new InvalidDataException($"Point format {pointFormat} требует LAS 1.4.");
            int minimumRecordLength = MinimumPointRecordLength(pointFormat);
            if (pointRecordLength < minimumRecordLength)
                throw new InvalidDataException(
                    $"Формат LAS {pointFormat} требует минимум {minimumRecordLength} байт, получено {pointRecordLength}.");
            if (headerSize < 227 || headerSize > fs.Length || offsetToPoints < headerSize || offsetToPoints > fs.Length)
                throw new InvalidDataException("Некорректные размеры заголовка или смещение массива точек LAS.");

            fs.Seek(131, SeekOrigin.Begin);
            double scaleX = br.ReadDouble();
            double scaleY = br.ReadDouble();
            double scaleZ = br.ReadDouble();
            double offsetX = br.ReadDouble();
            double offsetY = br.ReadDouble();
            double offsetZ = br.ReadDouble();

            if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || !double.IsFinite(scaleZ)
                || scaleX <= 0 || scaleY <= 0 || scaleZ <= 0
                || !double.IsFinite(offsetX) || !double.IsFinite(offsetY) || !double.IsFinite(offsetZ))
            {
                throw new InvalidDataException("LAS содержит некорректные scale/offset координат.");
            }

            fs.Seek(179, SeekOrigin.Begin);
            double maxX = br.ReadDouble();
            double minX = br.ReadDouble();
            double maxY = br.ReadDouble();
            double minY = br.ReadDouble();
            double maxZ = br.ReadDouble();
            double minZ = br.ReadDouble();

            if (!double.IsFinite(minX) || !double.IsFinite(maxX)
                || !double.IsFinite(minY) || !double.IsFinite(maxY)
                || !double.IsFinite(minZ) || !double.IsFinite(maxZ)
                || minX > maxX || minY > maxY || minZ > maxZ)
            {
                throw new InvalidDataException("LAS содержит некорректные границы координат.");
            }

            long totalPoints = legacyPointCount;
            if (versionMajor == 1 && versionMinor >= 4 && fs.Length >= 255 && headerSize >= 375)
            {
                fs.Seek(247, SeekOrigin.Begin);
                ulong las14Count = br.ReadUInt64();
                if (las14Count > long.MaxValue)
                    throw new InvalidDataException("Число точек LAS превышает поддерживаемый диапазон.");
                if (las14Count > 0) totalPoints = (long)las14Count;
            }

            if (totalPoints <= 0)
            {
                long body = fs.Length - offsetToPoints;
                if (body > 0) totalPoints = body / pointRecordLength;
            }

            if (totalPoints <= 0)
                throw new InvalidDataException($"В {Path.GetFileName(path)} нет точек.");
            long availableRecords = (fs.Length - offsetToPoints) / pointRecordLength;
            if (availableRecords < totalPoints)
                throw new InvalidDataException(
                    $"LAS обрезан: объявлено {totalPoints:N0} точек, доступно {availableRecords:N0}.");

            string coordinateSystemWkt = ReadCoordinateSystemWkt(
                fs, br, headerSize, numberOfVlrs, offsetToPoints, versionMajor, versionMinor);
            bool isSynthetic = coordinateSystemWkt.Contains("SYNTHETIC", StringComparison.OrdinalIgnoreCase);

            PrepareProject(path, projectDir, minX, maxX, minY, maxY, minZ, coordinateSystemWkt, isSynthetic);

            double originX = CurrentProject.OriginX;
            double originY = CurrentProject.OriginY;
            double originZ = CurrentProject.OriginZ;

            double keepRatio = maxPoints > 0 && totalPoints > maxPoints
                ? maxPoints / (double)totalPoints
                : 1.0;
            int estimated = (int)Math.Min(totalPoints, maxPoints > 0 ? maxPoints + 1024L : totalPoints);
            var points = new List<Point3D>(estimated);

            int classOffset = 15;
            int returnByteOffset = 14;
            int flagsOffset = 15;
            int rgbOffset = -1;
            bool las14Point = pointFormat >= 6;

            if (pointFormat == 2) { rgbOffset = 20; }
            else if (pointFormat == 3 || pointFormat == 5) { rgbOffset = 28; }
            else if (las14Point)
            {
                classOffset = 16;
                flagsOffset = 15;
                if (pointFormat == 7 || pointFormat == 8 || pointFormat == 10) rgbOffset = 30;
            }

            fs.Seek(offsetToPoints, SeekOrigin.Begin);
            const int ReadBatchBytes = 4 * 1024 * 1024;
            int recordsPerBatch = Math.Max(1, ReadBatchBytes / pointRecordLength);
            byte[] recordBuffer = new byte[recordsPerBatch * pointRecordLength];
            long kept = 0;
            int nextProgressPercent = 10;
            var readStopwatch = Stopwatch.StartNew();

            long processed = 0;
            while (processed < totalPoints)
            {
                int batchRecords = (int)Math.Min(recordsPerBatch, totalPoints - processed);
                int batchBytes = batchRecords * pointRecordLength;
                fs.ReadExactly(recordBuffer.AsSpan(0, batchBytes));

                for (int recordIndex = 0; recordIndex < batchRecords; recordIndex++)
                {
                    ReadOnlySpan<byte> record = recordBuffer.AsSpan(
                        recordIndex * pointRecordLength,
                        pointRecordLength);
                    if (IsWithheld(record, pointFormat, flagsOffset, classOffset))
                        continue;

                    int rawX = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(0, 4));
                    int rawY = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(4, 4));
                    int rawZ = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(8, 4));
                    if (keepRatio < 1.0 && !KeepBySpatialHash(rawX, rawY, rawZ, keepRatio))
                        continue;
                    if (maxPoints > 0 && points.Count >= maxPoints)
                        continue;

                    float localX = (float)((rawX * scaleX + offsetX) - originX);
                    float localY = (float)((rawY * scaleY + offsetY) - originY);
                    float localZ = (float)((rawZ * scaleZ + offsetZ) - originZ);

                    if (!float.IsFinite(localX) || !float.IsFinite(localY) || !float.IsFinite(localZ))
                        continue;

                    byte classification = Point3D.ClassUnclassified;
                    if (classOffset < pointRecordLength)
                    {
                        classification = record[classOffset];
                        if (!las14Point) classification = (byte)(classification & 0x1F);
                    }

                    byte returnInfo = 0x11;
                    if (returnByteOffset < pointRecordLength)
                        returnInfo = NormalizeReturnInfo(record[returnByteOffset], las14Point);

                    byte r = 200, g = 200, b = 200;
                    if (rgbOffset > 0 && rgbOffset + 6 <= pointRecordLength)
                    {
                        ushort rawR = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(rgbOffset, 2));
                        ushort rawG = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(rgbOffset + 2, 2));
                        ushort rawB = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(rgbOffset + 4, 2));
                        r = ToRgb8(rawR);
                        g = ToRgb8(rawG);
                        b = ToRgb8(rawB);
                    }
                    else if (pointRecordLength >= 14)
                    {
                        ushort intensity = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(12, 2));
                        byte gray = intensity > 255 ? (byte)(intensity >> 8) : (byte)Math.Clamp(intensity, (ushort)40, (ushort)255);
                        r = g = b = gray;
                    }

                    points.Add(new Point3D(localX, localY, localZ, r, g, b, classification, returnInfo));
                    kept++;
                }
                processed += batchRecords;
                if (totalPoints >= 20_000_000
                    && processed * 100.0 / totalPoints >= nextProgressPercent)
                {
                    double millionsPerSecond = processed / Math.Max(0.001, readStopwatch.Elapsed.TotalSeconds) / 1_000_000.0;
                    Console.WriteLine(
                        $"  [LAS] прочитано {nextProgressPercent}% ({processed:N0}/{totalPoints:N0}), {millionsPerSecond:F1} млн точек/с");
                    nextProgressPercent += 10;
                }
            }

            LastMetadata = new LasFileMetadata
            {
                FileName = Path.GetFileName(path),
                SourceFormat = "LAS",
                IsCompressed = false,
                Version = $"{versionMajor}.{versionMinor}",
                PointFormat = pointFormat,
                PointRecordLength = pointRecordLength,
                DeclaredPointCount = totalPoints,
                LoadedPointCount = kept,
                HasRgb = rgbOffset > 0,
                CoordinateSystemWkt = coordinateSystemWkt,
                IsSynthetic = isSynthetic
            };

            if (keepRatio < 1.0)
                Console.WriteLine($"  [LAS {versionMajor}.{versionMinor} / fmt {pointFormat}] пространственная выборка {totalPoints:N0} → {kept:N0} точек");

            return points;
        }

        public static List<Point3D> FastLoadLazFile(string path, string projectDir, int maxPoints = DefaultMaxLoadPoints)
        {
            if (maxPoints <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPoints), "Лимит точек должен быть положительным.");

            byte versionMajor;
            byte versionMinor;
            ushort headerSize;
            uint offsetToPoints;
            uint numberOfVlrs;
            byte pointFormat;
            ushort pointRecordLength;
            long totalPoints;
            double scaleX, scaleY, scaleZ;
            double offsetX, offsetY, offsetZ;
            double minX, maxX, minY, maxY, minZ, maxZ;
            string coordinateSystemWkt;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            using (var br = new BinaryReader(fs))
            {
                byte[] signature = br.ReadBytes(4);
                if (signature.Length < 4 || signature[0] != 'L' || signature[1] != 'A' || signature[2] != 'S' || signature[3] != 'F')
                    throw new InvalidDataException($"Файл не является ASPRS LAS/LAZ: {Path.GetFileName(path)}");

                fs.Seek(24, SeekOrigin.Begin);
                versionMajor = br.ReadByte();
                versionMinor = br.ReadByte();
                fs.Seek(94, SeekOrigin.Begin);
                headerSize = br.ReadUInt16();
                offsetToPoints = br.ReadUInt32();
                numberOfVlrs = br.ReadUInt32();
                byte rawPointFormat = br.ReadByte();
                pointFormat = (byte)(rawPointFormat & 0x3F);
                pointRecordLength = br.ReadUInt16();
                uint legacyPointCount = br.ReadUInt32();

                if (versionMajor != 1 || versionMinor < 2 || versionMinor > 4)
                    throw new InvalidDataException($"Версия LAS {versionMajor}.{versionMinor} не поддерживается.");
                if (pointFormat > 10)
                    throw new InvalidDataException($"Формат записи точки LAZ {pointFormat} не поддерживается.");
                int minimumRecordLength = MinimumPointRecordLength(pointFormat);
                if (pointRecordLength < minimumRecordLength)
                    throw new InvalidDataException(
                        $"Формат LAZ {pointFormat} требует минимум {minimumRecordLength} байт, получено {pointRecordLength}.");
                if (headerSize < 227 || headerSize > fs.Length || offsetToPoints < headerSize || offsetToPoints > fs.Length)
                    throw new InvalidDataException("Некорректные размеры заголовка или смещение массива точек LAZ.");

                fs.Seek(131, SeekOrigin.Begin);
                scaleX = br.ReadDouble();
                scaleY = br.ReadDouble();
                scaleZ = br.ReadDouble();
                offsetX = br.ReadDouble();
                offsetY = br.ReadDouble();
                offsetZ = br.ReadDouble();
                if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || !double.IsFinite(scaleZ)
                    || scaleX <= 0 || scaleY <= 0 || scaleZ <= 0
                    || !double.IsFinite(offsetX) || !double.IsFinite(offsetY) || !double.IsFinite(offsetZ))
                {
                    throw new InvalidDataException("LAZ содержит некорректные scale/offset координат.");
                }

                fs.Seek(179, SeekOrigin.Begin);
                maxX = br.ReadDouble();
                minX = br.ReadDouble();
                maxY = br.ReadDouble();
                minY = br.ReadDouble();
                maxZ = br.ReadDouble();
                minZ = br.ReadDouble();
                if (!double.IsFinite(minX) || !double.IsFinite(maxX)
                    || !double.IsFinite(minY) || !double.IsFinite(maxY)
                    || !double.IsFinite(minZ) || !double.IsFinite(maxZ)
                    || minX > maxX || minY > maxY || minZ > maxZ)
                {
                    throw new InvalidDataException("LAZ содержит некорректные границы координат.");
                }

                totalPoints = legacyPointCount;
                if (versionMinor >= 4 && headerSize >= 375)
                {
                    fs.Seek(247, SeekOrigin.Begin);
                    ulong las14Count = br.ReadUInt64();
                    if (las14Count > long.MaxValue)
                        throw new InvalidDataException("Число точек LAZ превышает поддерживаемый диапазон.");
                    if (las14Count > 0) totalPoints = (long)las14Count;
                }
                if (totalPoints <= 0)
                    throw new InvalidDataException($"В {Path.GetFileName(path)} не объявлено ни одной точки.");

                coordinateSystemWkt = ReadCoordinateSystemWkt(
                    fs, br, headerSize, numberOfVlrs, offsetToPoints, versionMajor, versionMinor);
            }

            bool isSynthetic = coordinateSystemWkt.Contains("SYNTHETIC", StringComparison.OrdinalIgnoreCase);
            PrepareProject(path, projectDir, minX, maxX, minY, maxY, minZ, coordinateSystemWkt, isSynthetic);

            double originX = CurrentProject.OriginX;
            double originY = CurrentProject.OriginY;
            double originZ = CurrentProject.OriginZ;
            double keepRatio = totalPoints > maxPoints ? maxPoints / (double)totalPoints : 1.0;
            int estimated = (int)Math.Min(totalPoints, maxPoints + 1024L);
            var points = new List<Point3D>(estimated);
            long kept = 0;
            long processed = 0;
            int nextProgressPercent = 10;
            var readStopwatch = Stopwatch.StartNew();

            ILasReader? reader = null;
            try
            {
                reader = LazReader.Create(path);
                if (reader is not LazReader)
                    throw new InvalidDataException(
                        $"{Path.GetFileName(path)} имеет расширение LAZ, но не содержит поток сжатия LAZ.");
                while (processed < totalPoints)
                {
                    LasPointSpan point = reader.ReadPointDataRecord();
                    IBasePointDataRecord? record = point.PointDataRecord;
                    if (record == null)
                        throw new InvalidDataException(
                            $"LAZ обрезан: объявлено {totalPoints:N0} точек, прочитано {processed:N0}.");
                    processed++;

                    if (record.Withheld)
                        continue;
                    int rawX = record.X;
                    int rawY = record.Y;
                    int rawZ = record.Z;
                    if (keepRatio < 1.0 && !KeepBySpatialHash(rawX, rawY, rawZ, keepRatio))
                        continue;
                    if (points.Count >= maxPoints)
                        continue;

                    float localX = (float)((rawX * scaleX + offsetX) - originX);
                    float localY = (float)((rawY * scaleY + offsetY) - originY);
                    float localZ = (float)((rawZ * scaleZ + offsetZ) - originZ);
                    if (!float.IsFinite(localX) || !float.IsFinite(localY) || !float.IsFinite(localZ))
                        continue;

                    byte classification = record switch
                    {
                        IExtendedPointDataRecord extended => (byte)extended.Classification,
                        IPointDataRecord legacy => (byte)legacy.Classification,
                        _ => Point3D.ClassUnclassified
                    };
                    byte returnNumber = (byte)Math.Clamp((int)record.ReturnNumber, 1, 15);
                    byte numberOfReturns = (byte)Math.Clamp((int)record.NumberOfReturns, returnNumber, 15);
                    byte returnInfo = (byte)(returnNumber | (numberOfReturns << 4));

                    byte r, g, b;
                    if (record is IColorPointDataRecord colored)
                    {
                        r = ToRgb8(colored.Color.R);
                        g = ToRgb8(colored.Color.G);
                        b = ToRgb8(colored.Color.B);
                    }
                    else
                    {
                        ushort intensity = record.Intensity;
                        byte gray = intensity > 255
                            ? (byte)(intensity >> 8)
                            : (byte)Math.Clamp(intensity, (ushort)40, (ushort)255);
                        r = g = b = gray;
                    }

                    points.Add(new Point3D(localX, localY, localZ, r, g, b, classification, returnInfo));
                    kept++;

                    if (totalPoints >= 20_000_000
                        && processed * 100.0 / totalPoints >= nextProgressPercent)
                    {
                        double millionsPerSecond = processed / Math.Max(0.001, readStopwatch.Elapsed.TotalSeconds) / 1_000_000.0;
                        Console.WriteLine(
                            $"  [LAZ] прочитано {nextProgressPercent}% ({processed:N0}/{totalPoints:N0}), {millionsPerSecond:F1} млн точек/с");
                        nextProgressPercent += 10;
                    }
                }
            }
            catch (Exception ex) when (ex is not InvalidDataException)
            {
                throw new InvalidDataException(
                    $"Не удалось распаковать LAZ {Path.GetFileName(path)}: {ex.Message}",
                    ex);
            }
            finally
            {
                if (reader is IDisposable disposable)
                    disposable.Dispose();
                else if (reader is IAsyncDisposable asyncDisposable)
                    asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            bool hasRgb = pointFormat == 2 || pointFormat == 3 || pointFormat == 5
                || pointFormat == 7 || pointFormat == 8 || pointFormat == 10;
            LastMetadata = new LasFileMetadata
            {
                FileName = Path.GetFileName(path),
                SourceFormat = "LAZ",
                IsCompressed = true,
                Version = $"{versionMajor}.{versionMinor}",
                PointFormat = pointFormat,
                PointRecordLength = pointRecordLength,
                DeclaredPointCount = totalPoints,
                LoadedPointCount = kept,
                HasRgb = hasRgb,
                CoordinateSystemWkt = coordinateSystemWkt,
                IsSynthetic = isSynthetic
            };

            if (keepRatio < 1.0)
                Console.WriteLine($"  [LAZ {versionMajor}.{versionMinor} / fmt {pointFormat}] пространственная выборка {totalPoints:N0} → {kept:N0} точек");
            return points;
        }

        private static void PrepareProject(
            string path,
            string projectDir,
            double minX,
            double maxX,
            double minY,
            double maxY,
            double minZ,
            string coordinateSystemWkt,
            bool isSynthetic)
        {
            if (!CurrentProject.IsInitialized)
            {
                CurrentProject.OriginX = (minX + maxX) * 0.5;
                CurrentProject.OriginY = (minY + maxY) * 0.5;
                CurrentProject.OriginZ = minZ;
                CurrentProject.IsInitialized = true;
                CurrentProject.CoordinateSystemWkt = coordinateSystemWkt;
                CurrentProject.IsSynthetic = isSynthetic;
                CurrentProject.Save(projectDir);
            }
            else if (!string.IsNullOrWhiteSpace(CurrentProject.CoordinateSystemWkt)
                && !string.IsNullOrWhiteSpace(coordinateSystemWkt)
                && !NormalizeWkt(CurrentProject.CoordinateSystemWkt).Equals(
                    NormalizeWkt(coordinateSystemWkt), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"CRS файла {Path.GetFileName(path)} не совпадает с CRS базисного скана.");
            }
            else if (string.IsNullOrWhiteSpace(CurrentProject.CoordinateSystemWkt)
                && !string.IsNullOrWhiteSpace(coordinateSystemWkt))
            {
                CurrentProject.CoordinateSystemWkt = coordinateSystemWkt;
                CurrentProject.IsSynthetic = isSynthetic;
                CurrentProject.Save(projectDir);
            }
        }

        private static int MinimumPointRecordLength(byte pointFormat) => pointFormat switch
        {
            0 => 20, 1 => 28, 2 => 26, 3 => 34, 4 => 57, 5 => 63,
            6 => 30, 7 => 36, 8 => 38, 9 => 59, 10 => 67,
            _ => int.MaxValue
        };

        private static bool KeepBySpatialHash(int x, int y, int z, double keepRatio)
        {
            unchecked
            {
                uint hash = 2166136261;
                hash = (hash ^ (uint)x) * 16777619;
                hash = (hash ^ (uint)y) * 16777619;
                hash = (hash ^ (uint)z) * 16777619;
                return hash / (double)uint.MaxValue < keepRatio;
            }
        }

        private static string NormalizeWkt(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                if (!char.IsWhiteSpace(ch) && ch != '\0')
                    sb.Append(char.ToUpperInvariant(ch));
            }
            return sb.ToString();
        }

        private static string ReadCoordinateSystemWkt(
            FileStream fs,
            BinaryReader br,
            ushort headerSize,
            uint numberOfVlrs,
            uint offsetToPoints,
            byte versionMajor,
            byte versionMinor)
        {
            long returnPosition = fs.Position;
            try
            {
                fs.Seek(headerSize, SeekOrigin.Begin);
                for (uint i = 0; i < numberOfVlrs && fs.Position + 54 <= offsetToPoints; i++)
                {
                    br.ReadUInt16();
                    string userId = Encoding.ASCII.GetString(br.ReadBytes(16)).TrimEnd('\0', ' ');
                    ushort recordId = br.ReadUInt16();
                    ushort length = br.ReadUInt16();
                    br.ReadBytes(32);
                    if (fs.Position + length > offsetToPoints) break;
                    byte[] payload = br.ReadBytes(length);
                    if (userId.Equals("LASF_Projection", StringComparison.OrdinalIgnoreCase)
                        && (recordId == 2111 || recordId == 2112))
                    {
                        return Encoding.UTF8.GetString(payload).TrimEnd('\0', ' ');
                    }
                }

                if (versionMajor == 1 && versionMinor >= 4 && headerSize >= 375)
                {
                    fs.Seek(235, SeekOrigin.Begin);
                    ulong firstEvlr = br.ReadUInt64();
                    uint evlrCount = br.ReadUInt32();
                    if (firstEvlr > 0 && firstEvlr < (ulong)fs.Length)
                    {
                        fs.Seek((long)firstEvlr, SeekOrigin.Begin);
                        for (uint i = 0; i < evlrCount && fs.Position + 60 <= fs.Length; i++)
                        {
                            br.ReadUInt16();
                            string userId = Encoding.ASCII.GetString(br.ReadBytes(16)).TrimEnd('\0', ' ');
                            ushort recordId = br.ReadUInt16();
                            ulong length = br.ReadUInt64();
                            br.ReadBytes(32);
                            if (length > int.MaxValue || (ulong)fs.Position + length > (ulong)fs.Length) break;
                            byte[] payload = br.ReadBytes((int)length);
                            if (userId.Equals("LASF_Projection", StringComparison.OrdinalIgnoreCase)
                                && (recordId == 2111 || recordId == 2112))
                            {
                                return Encoding.UTF8.GetString(payload).TrimEnd('\0', ' ');
                            }
                        }
                    }
                }
            }
            finally
            {
                fs.Seek(returnPosition, SeekOrigin.Begin);
            }
            return string.Empty;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsWithheld(ReadOnlySpan<byte> record, byte pointFormat, int flagsOffset, int classOffset)
        {
            if (pointFormat >= 6)
            {
                if (flagsOffset >= record.Length) return false;
                return (record[flagsOffset] & 0x04) != 0;
            }
            if (classOffset >= record.Length) return false;
            return (record[classOffset] & 0x80) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte NormalizeReturnInfo(byte raw, bool las14Point)
        {
            byte retNo, nRet;
            if (las14Point)
            {
                retNo = (byte)(raw & 0x0F);
                nRet = (byte)((raw >> 4) & 0x0F);
            }
            else
            {
                retNo = (byte)(raw & 0x07);
                nRet = (byte)((raw >> 3) & 0x07);
            }
            if (retNo == 0) retNo = 1;
            if (nRet == 0) nRet = 1;
            return (byte)((nRet << 4) | retNo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ToRgb8(ushort value) => value > 255 ? (byte)(value >> 8) : (byte)value;

        public static List<Point3D> FastLoadPlyFile(string path, int maxPoints = DefaultMaxLoadPoints)
        {
            if (maxPoints <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPoints), "Лимит точек должен быть положительным.");
            const int BufferSize = 1024 * 1024;
            var points = new List<Point3D>(128000);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            byte[] buffer = new byte[BufferSize];
            int remainingBytes = 0;
            bool inHeader = true;
            int bytesRead;

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
                            if (TryParsePoint(line, out Point3D pt))
                            {
                                if (maxPoints > 0 && points.Count >= maxPoints)
                                {
                                    throw new InvalidDataException(
                                        $"PLY превышает безопасный лимит {maxPoints:N0} точек. Используйте LAS для пространственной выборки.");
                                }
                                points.Add(pt);
                            }
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
            var items = new (int X, int Y, int Z, int Index)[count];

            Parallel.For(0, count, i =>
            {
                var pt = points[i];
                items[i] = (
                    (int)MathF.Floor(pt.X * invLeaf),
                    (int)MathF.Floor(pt.Y * invLeaf),
                    (int)MathF.Floor(pt.Z * invLeaf),
                    i);
            });

            Array.Sort(items, CompareVoxelItems);

            var result = new List<Point3D>(count / 2);
            int start = 0;
            while (start < count)
            {
                int end = start + 1;
                while (end < count
                    && items[end].X == items[start].X
                    && items[end].Y == items[start].Y
                    && items[end].Z == items[start].Z)
                {
                    end++;
                }

                double sx = 0, sy = 0, sz = 0;
                long sr = 0, sg = 0, sb = 0;
                byte selectedClass = Point3D.ClassUnclassified;
                byte returnInfo = 0x11;
                int selectedPriority = int.MinValue;

                for (int i = start; i < end; i++)
                {
                    var pt = points[items[i].Index];
                    sx += pt.X; sy += pt.Y; sz += pt.Z;
                    sr += pt.R; sg += pt.G; sb += pt.B;
                    int priority = Point3D.ClassPriority(pt.Classification);
                    if (priority > selectedPriority)
                    {
                        selectedPriority = priority;
                        selectedClass = pt.Classification;
                        returnInfo = pt.ReturnInfo;
                    }
                }

                int n = end - start;
                result.Add(new Point3D(
                    (float)(sx / n),
                    (float)(sy / n),
                    (float)(sz / n),
                    (byte)(sr / n),
                    (byte)(sg / n),
                    (byte)(sb / n),
                    selectedClass,
                    returnInfo));
                start = end;
            }
            return result;
        }

        public static (
            List<Point3D> Raw,
            List<Point3D> Clean,
            List<Point3D> Analysis,
            int MachineryRemoved,
            int VegetationRemoved) VoxelizeForPipeline(List<Point3D> points, float leafSize)
        {
            if (leafSize <= 0.0f)
                throw new ArgumentOutOfRangeException(nameof(leafSize));
            if (points.Count == 0)
                return (new List<Point3D>(), new List<Point3D>(), new List<Point3D>(), 0, 0);

            float invLeaf = 1.0f / leafSize;
            int count = points.Count;
            var items = new (int X, int Y, int Z, int Index)[count];
            Parallel.For(0, count, i =>
            {
                var pt = points[i];
                items[i] = (
                    (int)MathF.Floor(pt.X * invLeaf),
                    (int)MathF.Floor(pt.Y * invLeaf),
                    (int)MathF.Floor(pt.Z * invLeaf),
                    i);
            });
            Array.Sort(items, CompareVoxelItems);

            int voxelCount = 1;
            for (int i = 1; i < count; i++)
            {
                if (items[i].X != items[i - 1].X
                    || items[i].Y != items[i - 1].Y
                    || items[i].Z != items[i - 1].Z)
                {
                    voxelCount++;
                }
            }
            var raw = new List<Point3D>(voxelCount);
            var clean = new List<Point3D>(voxelCount);
            var analysis = new List<Point3D>(voxelCount);
            int machinery = 0;
            int vegetation = 0;

            int start = 0;
            while (start < count)
            {
                int end = start + 1;
                while (end < count
                    && items[end].X == items[start].X
                    && items[end].Y == items[start].Y
                    && items[end].Z == items[start].Z)
                {
                    end++;
                }

                var rawAccumulator = new VoxelAccumulator();
                var cleanAccumulator = new VoxelAccumulator();
                var analysisAccumulator = new VoxelAccumulator();
                for (int i = start; i < end; i++)
                {
                    var point = points[items[i].Index];
                    rawAccumulator.Add(point);

                    if (point.Classification == Point3D.ClassMachinery)
                        machinery++;
                    else if (point.Classification == Point3D.ClassLowVeg
                        || point.Classification == Point3D.ClassMediumVeg
                        || point.Classification == Point3D.ClassHighVeg)
                        vegetation++;

                    if (!Point3D.IsRemovedFromCleanView(point.Classification))
                        cleanAccumulator.Add(point);
                    if (!Point3D.IsExcludedFromDem(point.Classification))
                        analysisAccumulator.Add(point);
                }

                raw.Add(rawAccumulator.ToPoint());
                if (cleanAccumulator.Count > 0) clean.Add(cleanAccumulator.ToPoint());
                if (analysisAccumulator.Count > 0) analysis.Add(analysisAccumulator.ToPoint());
                start = end;
            }
            return (raw, clean, analysis, machinery, vegetation);
        }

        private static int CompareVoxelItems(
            (int X, int Y, int Z, int Index) a,
            (int X, int Y, int Z, int Index) b)
        {
            int cmp = a.X.CompareTo(b.X);
            if (cmp != 0) return cmp;
            cmp = a.Y.CompareTo(b.Y);
            return cmp != 0 ? cmp : a.Z.CompareTo(b.Z);
        }

        private struct VoxelAccumulator
        {
            private double sx, sy, sz;
            private long sr, sg, sb;
            private byte selectedClass;
            private byte returnInfo;
            private int selectedPriority;
            public int Count { get; private set; }

            public void Add(Point3D point)
            {
                sx += point.X; sy += point.Y; sz += point.Z;
                sr += point.R; sg += point.G; sb += point.B;
                int priority = Point3D.ClassPriority(point.Classification);
                if (Count == 0 || priority > selectedPriority)
                {
                    selectedPriority = priority;
                    selectedClass = point.Classification;
                    returnInfo = point.ReturnInfo;
                }
                Count++;
            }

            public Point3D ToPoint()
            {
                if (Count <= 0) throw new InvalidOperationException("Пустой воксель.");
                return new Point3D(
                    (float)(sx / Count),
                    (float)(sy / Count),
                    (float)(sz / Count),
                    (byte)(sr / Count),
                    (byte)(sg / Count),
                    (byte)(sb / Count),
                    selectedClass,
                    returnInfo);
            }
        }

        public static void ExportToBinary(List<Point3D> points, string outputPath)
        {
            const int ChunkSize = 65536;
            byte[] buffer = new byte[ChunkSize * 16];
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 2 * 1024 * 1024);
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

        public static OctreeMetadata BuildAndExportOctreeLOD(List<Point3D> points, string outputDir, string epochPrefix, int maxPointsPerNode = 60000)
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

            var rootSample = SampleForViewer(points, maxPointsPerNode);

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

            var octantCounts = new int[8];
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                int octIdx = (p.X >= midX ? 1 : 0) | ((p.Y >= midY ? 1 : 0) << 1) | ((p.Z >= midZ ? 1 : 0) << 2);
                octantCounts[octIdx]++;
            }

            var octantSamples = new List<Point3D>[8];
            var seenByOctant = new int[8];
            for (int o = 0; o < 8; o++)
            {
                octantSamples[o] = new List<Point3D>(Math.Min(octantCounts[o], maxPointsPerNode));
            }

            for (int i = 0; i < points.Count; i++)
            {
                var point = points[i];
                int octant = (point.X >= midX ? 1 : 0)
                    | ((point.Y >= midY ? 1 : 0) << 1)
                    | ((point.Z >= midZ ? 1 : 0) << 2);
                int sequence = seenByOctant[octant]++;
                double keepRatio = Math.Min(1.0, maxPointsPerNode / (double)octantCounts[octant]);
                if (octantSamples[octant].Count < maxPointsPerNode
                    && KeepViewerSample(point, sequence, keepRatio))
                {
                    octantSamples[octant].Add(point);
                }
            }

            for (int o = 0; o < 8; o++)
            {
                if (octantSamples[o].Count == 0) continue;
                string nodeBin = $"{epochPrefix}_node_{o}.bin";
                ExportToBinary(octantSamples[o], Path.Combine(outputDir, nodeBin));
                meta.Nodes.Add(new OctreeNodeMetadata
                {
                    Id = $"node_{o}",
                    BinFile = nodeBin,
                    Level = 1,
                    PointCount = octantSamples[o].Count,
                    MinBounds = new[] { (o & 1) != 0 ? midX : minX, (o & 2) != 0 ? midY : minY, (o & 4) != 0 ? midZ : minZ },
                    MaxBounds = new[] { (o & 1) != 0 ? maxX : midX, (o & 2) != 0 ? maxY : midY, (o & 4) != 0 ? maxZ : midZ }
                });
            }

            return meta;
        }

        private static List<Point3D> SampleForViewer(List<Point3D> points, int maxPoints)
        {
            int target = Math.Max(1, maxPoints);
            var sample = new List<Point3D>(Math.Min(points.Count, target));
            double keepRatio = Math.Min(1.0, target / (double)points.Count);
            for (int i = 0; i < points.Count && sample.Count < target; i++)
            {
                if (KeepViewerSample(points[i], i, keepRatio)) sample.Add(points[i]);
            }
            return sample;
        }

        private static bool KeepViewerSample(Point3D point, int sequence, double keepRatio)
        {
            if (keepRatio >= 1.0) return true;
            unchecked
            {
                uint hash = 2166136261;
                hash = (hash ^ (uint)MathF.Round(point.X * 1000)) * 16777619;
                hash = (hash ^ (uint)MathF.Round(point.Y * 1000)) * 16777619;
                hash = (hash ^ (uint)MathF.Round(point.Z * 1000)) * 16777619;
                hash = (hash ^ (uint)sequence) * 16777619;
                return hash / (double)uint.MaxValue < keepRatio;
            }
        }
    }
}
