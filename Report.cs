using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LidarProcessorMVP
{
    public class EpochReportData
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public double CutVolume { get; set; }
        public double FillVolume { get; set; }
        public double NetVolume { get; set; }
        public double AreaCut { get; set; }
        public double AreaFill { get; set; }
        public double MaxDepth { get; set; }
    }

    public static class ReportGenerator
    {
        public static void GenerateHtmlReport(string outputPath, string sceneType, ProjectConfig config, List<EpochReportData> epochs)
        {
            var sb = new StringBuilder();
            var culture = new CultureInfo("ru-RU");

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"ru\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"UTF-8\">");
            sb.AppendLine("<title>Маркшейдерский протокол замера объемов</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; margin: 30px; color: #222; background: #fff; }");
            sb.AppendLine("@media print { body { margin: 10mm; } .no-print { display: none; } }");
            sb.AppendLine(".header-block { border-bottom: 2px solid #000; padding-bottom: 12px; margin-bottom: 20px; }");
            sb.AppendLine(".title { font-size: 20px; font-weight: bold; text-transform: uppercase; margin: 0 0 6px 0; }");
            sb.AppendLine(".subtitle { font-size: 13px; color: #555; }");
            sb.AppendLine(".meta-table { width: 100%; border-collapse: collapse; margin-bottom: 25px; font-size: 12px; }");
            sb.AppendLine(".meta-table td { padding: 6px 10px; border: 1px solid #ccc; }");
            sb.AppendLine(".meta-table td.label { background: #f5f5f5; font-weight: bold; width: 25%; }");
            sb.AppendLine(".data-table { width: 100%; border-collapse: collapse; margin-top: 15px; font-size: 12px; }");
            sb.AppendLine(".data-table th { background: #e9ecef; border: 1px solid #999; padding: 8px; text-align: center; }");
            sb.AppendLine(".data-table td { border: 1px solid #999; padding: 8px; text-align: right; }");
            sb.AppendLine(".data-table td.left { text-align: left; }");
            sb.AppendLine(".highlight { font-weight: bold; color: #0b6623; }");
            sb.AppendLine(".signature-block { margin-top: 50px; display: flex; justify-content: space-between; font-size: 12px; }");
            sb.AppendLine(".sig-line { width: 220px; border-bottom: 1px solid #000; margin-top: 30px; text-align: center; font-size: 10px; color: #777; }");
            sb.AppendLine(".btn-print { padding: 10px 20px; background: #0066cc; color: #fff; border: none; border-radius: 4px; cursor: pointer; font-weight: bold; margin-bottom: 20px; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            sb.AppendLine("<button class=\"btn-print no-print\" onclick=\"window.print()\">Распечатать в PDF / Принтер</button>");

            sb.AppendLine("<div class=\"header-block\">");
            sb.AppendLine("<div class=\"title\">ПРОТОКОЛ МАРКШЕЙДЕРСКОГО ЗАМЕРА ОБЪЕМОВ ВЫРАБОТКИ</div>");
            sb.AppendLine("<div class=\"subtitle\">Автоматизированный мониторинг БПЛА-LiDAR (РД 07-603-03 / СП 11-104-97)</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<table class=\"meta-table\">");
            sb.AppendLine($"<tr><td class=\"label\">Объект контроля:</td><td>{sceneType}</td><td class=\"label\">Дата формирования:</td><td>{DateTime.Now.ToString("dd.MM.yyyy HH:mm")}</td></tr>");
            sb.AppendLine($"<tr><td class=\"label\">Опорный базис СК:</td><td>X: {config.OriginX:F3} м, Y: {config.OriginY:F3} м, Z: {config.OriginZ:F3} м</td><td class=\"label\">Система координат:</td><td>{config.CoordinateSystemInfo}</td></tr>");
            sb.AppendLine($"<tr><td class=\"label\">Аппаратура сканирования:</td><td>LiDAR Zenmuse L2 / RTK UAV</td><td class=\"label\">Метод расчета:</td><td>Цифровая модель рельефа (2.5D DEM + PMF)</td></tr>");
            sb.AppendLine("</table>");

            sb.AppendLine("<h3>Сводный баланс перемещения земляных масс по эпохам:</h3>");
            sb.AppendLine("<table class=\"data-table\">");
            sb.AppendLine("<thead>");
            sb.AppendLine("<tr>");
            sb.AppendLine("<th>№</th>");
            sb.AppendLine("<th>Наименование стадии</th>");
            sb.AppendLine("<th>Выемка (+Cut), м³</th>");
            sb.AppendLine("<th>Насыпь (-Fill), м³</th>");
            sb.AppendLine("<th>Чистый баланс, м³</th>");
            sb.AppendLine("<th>Площадь выемки, м²</th>");
            sb.AppendLine("<th>Макс. глубина, м</th>");
            sb.AppendLine("</tr>");
            sb.AppendLine("</thead>");
            sb.AppendLine("<tbody>");

            foreach (var ep in epochs)
            {
                sb.AppendLine("<tr>");
                sb.AppendLine($"<td style=\"text-align:center;\">{ep.Id}</td>");
                sb.AppendLine($"<td class=\"left\"><b>{ep.Title}</b><br><small style=\"color:#666;\">{ep.FileName}</small></td>");
                sb.AppendLine($"<td class=\"highlight\">+{ep.CutVolume.ToString("N2", culture)}</td>");
                sb.AppendLine($"<td style=\"color:#c00;\">-{ep.FillVolume.ToString("N2", culture)}</td>");
                sb.AppendLine($"<td><b>{ep.NetVolume.ToString("N2", culture)}</b></td>");
                sb.AppendLine($"<td>{ep.AreaCut.ToString("N1", culture)}</td>");
                sb.AppendLine($"<td>{ep.MaxDepth.ToString("F2", culture)}</td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");

            sb.AppendLine("<div class=\"signature-block\">");
            sb.AppendLine("<div>Главный маркшейдер участка:<div class=\"sig-line\">(подпись / Ф.И.О.)</div></div>");
            sb.AppendLine("<div>Оператор БПЛА-комплекса:<div class=\"sig-line\">(подпись / Ф.И.О.)</div></div>");
            sb.AppendLine("<div>Начальник производственной службы:<div class=\"sig-line\">(подпись / Ф.И.О.)</div></div>");
            sb.AppendLine("</div>");

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }

        public static void ExportVolumeGridCsv(string csvPath, float[] baseGrid, float[] currentGrid, float minX, float minZ, float cellSize, int cols, int rows)
        {
            using var sw = new StreamWriter(csvPath, false, Encoding.UTF8);
            sw.WriteLine("CellX,CellZ,BaseElevation,CurrentElevation,HeightDiff,CellVolume_M3,OperationType");

            double cellArea = cellSize * cellSize;

            for (int r = 0; r < rows; r++)
            {
                float z = minZ + (r + 0.5f) * cellSize;
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    float bH = baseGrid[idx];
                    float cH = currentGrid[idx];

                    if (bH > -9999f && cH > -9999f)
                    {
                        float diff = bH - cH;
                        if (MathF.Abs(diff) > 0.03f)
                        {
                            float x = minX + (c + 0.5f) * cellSize;
                            double vol = Math.Abs(diff) * cellArea;
                            string op = diff > 0 ? "CUT" : "FILL";
                            sw.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:F3},{1:F3},{2:F3},{3:F3},{4:F3},{5:F3},{6}",
                                x, z, bH, cH, diff, vol, op));
                        }
                    }
                }
            }
        }
    }
}