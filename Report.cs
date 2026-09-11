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
        public double BulkedVolume { get; set; }
        public double MaterialWeightTon { get; set; }
        public double FillVolume { get; set; }
        public double NetVolume { get; set; }
        public double AreaCut { get; set; }
        public double AreaFill { get; set; }
        public double MaxDepth { get; set; }
        public double RmseElevation { get; set; }
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
            sb.AppendLine("<title>Маркшейдерский протокол замера объемов выработки</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; margin: 30px; color: #111; background: #fff; line-height: 1.4; }");
            sb.AppendLine("@media print { body { margin: 10mm; } .no-print { display: none; } }");
            sb.AppendLine(".header { border-bottom: 2px solid #000; padding-bottom: 8px; margin-bottom: 15px; }");
            sb.AppendLine(".title { font-size: 18px; font-weight: bold; text-transform: uppercase; margin: 0; }");
            sb.AppendLine(".normative { font-size: 11px; color: #555; margin-top: 4px; }");
            sb.AppendLine(".meta-table { width: 100%; border-collapse: collapse; margin-top: 15px; margin-bottom: 20px; font-size: 12px; }");
            sb.AppendLine(".meta-table td { padding: 6px 10px; border: 1px solid #bbb; }");
            sb.AppendLine(".meta-table td.label { background: #f4f6f8; font-weight: bold; width: 25%; }");
            sb.AppendLine(".data-table { width: 100%; border-collapse: collapse; margin-top: 10px; font-size: 11px; }");
            sb.AppendLine(".data-table th { background: #eef2f5; border: 1px solid #777; padding: 7px; text-align: center; }");
            sb.AppendLine(".data-table td { border: 1px solid #777; padding: 7px; text-align: right; }");
            sb.AppendLine(".data-table td.left { text-align: left; }");
            sb.AppendLine(".highlight { font-weight: bold; color: #006622; }");
            sb.AppendLine(".signature-block { margin-top: 40px; display: flex; justify-content: space-between; font-size: 11px; }");
            sb.AppendLine(".sig-item { width: 220px; }");
            sb.AppendLine(".sig-line { border-bottom: 1px solid #000; margin-top: 35px; text-align: center; font-size: 10px; color: #666; }");
            sb.AppendLine(".btn-print { padding: 9px 18px; background: #0056b3; color: #fff; border: none; border-radius: 4px; cursor: pointer; font-weight: bold; margin-bottom: 15px; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            sb.AppendLine("<button class=\"btn-print no-print\" onclick=\"window.print()\">Экспорт в PDF / Печать протокола</button>");

            sb.AppendLine("<div class=\"header\">");
            sb.AppendLine("<div class=\"title\">ПРОТОКОЛ МАРКШЕЙДЕРСКОГО УЧЕТА ОБЪЕМОВ ЗЕМЛЯНЫХ РАБОТ</div>");
            sb.AppendLine("<div class=\"normative\">Составлен в соответствии с требованиями РД 07-603-03 (Инструкция по производству маркшейдерских работ) и СП 11-104-97</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<table class=\"meta-table\">");
            sb.AppendLine($"<tr><td class=\"label\">Объект недропользования:</td><td>{sceneType}</td><td class=\"label\">Дата/время замера:</td><td>{DateTime.Now.ToString("dd.MM.yyyy HH:mm")}</td></tr>");
            sb.AppendLine($"<tr><td class=\"label\">Система координат:</td><td>{config.CoordinateSystemInfo}</td><td class=\"label\">Опорный репер (Origin):</td><td>X: {config.OriginX:F2}, Y: {config.OriginY:F2}, Z: {config.OriginZ:F2}</td></tr>");
            sb.AppendLine($"<tr><td class=\"label\">Комплекс БПЛА / Сенсор:</td><td>DJI Matrice 350 RTK + Zenmuse L2</td><td class=\"label\">Плотность / Кр:</td><td>{config.MaterialDensity:F2} т/м³ | Кр = {config.BulkingFactor:F2}</td></tr>");
            sb.AppendLine("</table>");

            sb.AppendLine("<h4>Сводные маркшейдерские показатели динамики выработки:</h4>");
            sb.AppendLine("<table class=\"data-table\">");
            sb.AppendLine("<thead>");
            sb.AppendLine("<tr>");
            sb.AppendLine("<th>№</th>");
            sb.AppendLine("<th>Наименование стадии</th>");
            sb.AppendLine("<th>Выемка в целике, м³</th>");
            sb.AppendLine("<th>Объем в отвале (Кр), м³</th>");
            sb.AppendLine("<th>Масса, тонн</th>");
            sb.AppendLine("<th>Насыпь, м³</th>");
            sb.AppendLine("<th>Площадь, м²</th>");
            sb.AppendLine("<th>СКП (RMSE), м</th>");
            sb.AppendLine("</tr>");
            sb.AppendLine("</thead>");
            sb.AppendLine("<tbody>");

            foreach (var ep in epochs)
            {
                sb.AppendLine("<tr>");
                sb.AppendLine($"<td style=\"text-align:center;\">{ep.Id}</td>");
                sb.AppendLine($"<td class=\"left\"><b>{ep.Title}</b><br><small style=\"color:#555;\">{ep.FileName}</small></td>");
                sb.AppendLine($"<td class=\"highlight\">+{ep.CutVolume.ToString("N2", culture)}</td>");
                sb.AppendLine($"<td>{ep.BulkedVolume.ToString("N2", culture)}</td>");
                sb.AppendLine($"<td><b>{ep.MaterialWeightTon.ToString("N1", culture)}</b></td>");
                sb.AppendLine($"<td style=\"color:#b30000;\">-{ep.FillVolume.ToString("N2", culture)}</td>");
                sb.AppendLine($"<td>{ep.AreaCut.ToString("N1", culture)}</td>");
                sb.AppendLine($"<td style=\"text-align:center;\">±{ep.RmseElevation.ToString("F3", culture)}</td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");

            sb.AppendLine("<div class=\"signature-block\">");
            sb.AppendLine("<div class=\"sig-item\">Главный маркшейдер объекта:<div class=\"sig-line\">(подпись / Ф.И.О.)</div></div>");
            sb.AppendLine("<div class=\"sig-item\">Оператор комплекса БПЛА-LiDAR:<div class=\"sig-line\">(подпись / Ф.И.О.)</div></div>");
            sb.AppendLine("<div class=\"sig-item\">Представитель технадзора заказчика:<div class=\"sig-line\">(подпись / Ф.И.О.)</div></div>");
            sb.AppendLine("</div>");

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }

        public static void ExportVolumeGridCsv(string csvPath, float[] baseGrid, float[] currentGrid, float minX, float minY, float cellSize, int cols, int rows)
        {
            using var sw = new StreamWriter(csvPath, false, Encoding.UTF8);
            sw.WriteLine("CellX,CellY,BaseZ,CurrentZ,HeightDiff,CellVolume_M3,OperationType");

            double cellArea = cellSize * cellSize;

            for (int r = 0; r < rows; r++)
            {
                float y = minY + (r + 0.5f) * cellSize;
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    float bZ = baseGrid[idx];
                    float cZ = currentGrid[idx];

                    if (bZ > -9999f && cZ > -9999f)
                    {
                        float diff = bZ - cZ;
                        if (MathF.Abs(diff) > 0.03f)
                        {
                            float x = minX + (c + 0.5f) * cellSize;
                            double vol = Math.Abs(diff) * cellArea;
                            string op = diff > 0 ? "CUT" : "FILL";
                            sw.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:F3},{1:F3},{2:F3},{3:F3},{4:F3},{5:F3},{6}",
                                x, y, bZ, cZ, diff, vol, op));
                        }
                    }
                }
            }
        }
    }
}