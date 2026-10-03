using System.IO;
using System.Net;
using System.Text;

namespace DroneDash_x64.Desktop.PvAnalysis;

public static class PvInspectionReportExporter
{
    public static string ExportHtml(
        string destinationFolder,
        PvDatasetResult dataset)
    {
        Directory.CreateDirectory(destinationFolder);
        var path = Path.Combine(destinationFolder, "pv-inspection-report.html");

        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"de\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.AppendLine("<title>DroneDash PV-Analyse</title>");
        html.AppendLine("<style>");
        html.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:32px;color:#20242a}");
        html.AppendLine("table{border-collapse:collapse;width:100%;font-size:13px}");
        html.AppendLine("th,td{border:1px solid #c9ced6;padding:6px 8px;text-align:left;vertical-align:top}");
        html.AppendLine("th{background:#eef2f6}.critical{font-weight:700}.warning{font-weight:600}");
        html.AppendLine(".note{padding:10px;border:1px solid #d7b35b;background:#fff7df;margin:14px 0}");
        html.AppendLine("</style></head><body>");
        html.AppendLine("<h1>DroneDash PV-Analyse</h1>");
        html.AppendLine($"<p>Erstellt: {WebUtility.HtmlEncode(DateTimeOffset.Now.ToString("G"))}</p>");
        html.AppendLine("<div class=\"note\">Thermische Anomalie-Kandidaten sind keine automatische elektrische Fehlerdiagnose. Ergebnisse müssen mit Anlagenzustand, Einstrahlung, Reflexion, Verschattung und Betriebsbedingungen fachlich verifiziert werden. Flächenangaben sind Pixel²; eine physische Fläche in m² erfordert eine belastbare GSD/Geometrie.</div>");

        html.AppendLine("<h2>Zusammenfassung</h2><ul>");
        html.AppendLine($"<li>Quelle: {H(dataset.SourceFolder)}</li>");
        html.AppendLine($"<li>Thermalbilder: {dataset.Summary.ThermalImageCount}</li>");
        html.AppendLine($"<li>Erfolgreich verarbeitet: {dataset.Summary.SuccessfullyProcessedCount}</li>");
        html.AppendLine($"<li>Bilder mit Kandidaten: {dataset.Summary.ImagesWithCandidates}</li>");
        html.AppendLine($"<li>Warnung: {dataset.Summary.WarningImageCount}</li>");
        html.AppendLine($"<li>Kritisch: {dataset.Summary.CriticalImageCount}</li>");
        html.AppendLine($"<li>Anomalie-Cluster: {dataset.Summary.CandidateCount}</li>");
        html.AppendLine($"<li>Maximales ΔT: {(dataset.Summary.HighestDeltaC.HasValue ? dataset.Summary.HighestDeltaC.Value.ToString("F2") + " °C" : "—")}</li>");
        html.AppendLine("</ul>");

        html.AppendLine("<h2>Analyseparameter</h2><ul>");
        html.AppendLine($"<li>Warnschwelle ΔT: {dataset.Settings.WarningDeltaC:F2} °C</li>");
        html.AppendLine($"<li>Kritische Schwelle ΔT: {dataset.Settings.CriticalDeltaC:F2} °C</li>");
        html.AppendLine($"<li>Lokaler Radius: {dataset.Settings.LocalWindowRadiusPixels} px</li>");
        html.AppendLine("<li>Robuste Referenz: lokaler Median (P50), P75-basierte adaptive Schwelle, Hysterese-Segmentierung</li>");
        html.AppendLine($"<li>Minimale Clustergröße: {dataset.Settings.MinimumClusterPixels} px</li>");
        html.AppendLine("</ul>");

        html.AppendLine("<h2>Bilder</h2>");
        html.AppendLine("<table><thead><tr><th>Datei</th><th>GPS</th><th>RTK</th><th>Max</th><th>max ΔT</th><th>Stufe</th><th>Route</th><th>Status</th></tr></thead><tbody>");
        foreach (var image in dataset.Images)
        {
            var css = image.Severity switch
            {
                PvAnomalySeverity.Critical => "critical",
                PvAnomalySeverity.Warning => "warning",
                _ => ""
            };

            html.AppendLine(
                $"<tr class=\"{css}\"><td>{H(image.FileName)}</td><td>{H(image.GpsText)}</td>" +
                $"<td>{H(image.RtkText)}</td><td>{H(image.MaxTemperatureText)}</td>" +
                $"<td>{H(image.HighestDeltaText)}</td><td>{H(image.Severity.ToString())}</td>" +
                $"<td>{H(image.RouteText)}</td><td>{H(image.StatusText)}</td></tr>");
        }
        html.AppendLine("</tbody></table>");

        html.AppendLine("<h2>Anomalie-Kandidaten</h2>");
        html.AppendLine("<table><thead><tr><th>Datei</th><th>#</th><th>Stufe</th><th>Fläche px²</th><th>Füllgrad</th><th>Peak</th><th>Median-Basis</th><th>P75</th><th>ΔT</th><th>Peak XY</th><th>Zentrum XY</th><th>Bounding Box</th></tr></thead><tbody>");
        foreach (var image in dataset.Images)
        {
            foreach (var candidate in image.Candidates)
            {
                html.AppendLine(
                    $"<tr><td>{H(image.FileName)}</td><td>{candidate.Index}</td><td>{H(candidate.Severity.ToString())}</td>" +
                    $"<td>{candidate.AreaPixels}</td><td>{candidate.FillRatio:P0}</td>" +
                    $"<td>{candidate.PeakTemperatureC:F2} °C</td><td>{candidate.LocalBaselineC:F2} °C</td>" +
                    $"<td>{candidate.LocalUpperQuartileC:F2} °C</td><td>{candidate.DeltaC:F2} °C</td>" +
                    $"<td>{H(candidate.PixelText)}</td><td>{H(candidate.CentroidText)}</td>" +
                    $"<td>{H(candidate.BoundingBoxText)}</td></tr>");
            }
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("</body></html>");

        File.WriteAllText(path, html.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string H(string? value) =>
        WebUtility.HtmlEncode(value ?? "");
}
