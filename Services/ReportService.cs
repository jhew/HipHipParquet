using HipHipParquet.Models;
using System.Text;

namespace HipHipParquet.Services;

/// <summary>
/// Generates self-contained HTML Quality reports with inline CSS and SVG visualizations.
/// </summary>
public class ReportService
{
    public string GenerateHtmlReport(FileProfile profile, List<NarrativeItem> findings, FileComparison? comparison = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"<title>Quality Report — {Escape(profile.FileName)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(GetCss());
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // Header
        sb.AppendLine("<div class=\"header\">");
        sb.AppendLine("<h1>📊 Data Quality Report</h1>");
        sb.AppendLine($"<p class=\"subtitle\">{Escape(profile.FileName)} &mdash; Generated {profile.AnalyzedAt:MMMM dd, yyyy h:mm tt}</p>");
        sb.AppendLine("</div>");

        // File summary card
        sb.AppendLine("<div class=\"card\">");
        sb.AppendLine("<h2>File Summary</h2>");
        sb.AppendLine("<div class=\"stats-grid\">");
        sb.AppendLine($"<div class=\"stat\"><span class=\"stat-value\">{profile.RowCount:N0}</span><span class=\"stat-label\">Rows</span></div>");
        sb.AppendLine($"<div class=\"stat\"><span class=\"stat-value\">{profile.ColumnCount}</span><span class=\"stat-label\">Columns</span></div>");
        sb.AppendLine($"<div class=\"stat\"><span class=\"stat-value\">{profile.FileSizeFormatted}</span><span class=\"stat-label\">File Size</span></div>");
        sb.AppendLine($"<div class=\"stat\"><span class=\"stat-value\">{FileFormatDetector.GetFormatDisplayName(profile.SourceFormat)}</span><span class=\"stat-label\">Format</span></div>");
        sb.AppendLine($"<div class=\"stat\"><span class=\"stat-value\">{profile.OverallCompleteness:F1}%</span><span class=\"stat-label\">Completeness</span></div>");
        sb.AppendLine($"<div class=\"stat\"><span class=\"stat-value\">{profile.ColumnsWithNulls}</span><span class=\"stat-label\">Columns with Nulls</span></div>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        // Quality score
        sb.AppendLine("<div class=\"card\">");
        sb.AppendLine("<h2>Data Quality Score</h2>");
        sb.AppendLine("<div class=\"score-section\">");
        sb.AppendLine(GenerateScoreGaugeSvg(profile.OverallScore));
        sb.AppendLine("<div class=\"score-components\">");
        GenerateScoreBar(sb, "Completeness", profile.OverallScore.Completeness, 25);
        GenerateScoreBar(sb, "Uniqueness", profile.OverallScore.Uniqueness, 25);
        GenerateScoreBar(sb, "Validity", profile.OverallScore.Validity, 25);
        GenerateScoreBar(sb, "Distribution", profile.OverallScore.Distribution, 25);
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
        sb.AppendLine(GenerateScoringKey());
        sb.AppendLine("</div>");

        // Findings
        if (findings.Count > 0)
        {
            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine($"<h2>Findings ({findings.Count})</h2>");
            foreach (var finding in findings)
            {
                var severityClass = finding.Severity.ToString().ToLower();
                sb.AppendLine($"<div class=\"finding {severityClass}\">");
                sb.AppendLine($"<span class=\"finding-icon\">{finding.Icon}</span>");
                sb.AppendLine($"<div><strong>{Escape(finding.Title)}</strong><br/><span class=\"finding-desc\">{Escape(finding.Description)}</span></div>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");
        }

        // Column profiles
        sb.AppendLine("<div class=\"card\">");
        sb.AppendLine($"<h2>Column Profiles ({profile.Columns.Count})</h2>");
        sb.AppendLine("<table class=\"columns-table\">");
        sb.AppendLine("<thead><tr>");
        sb.AppendLine("<th>Column</th><th>Type</th><th>Score</th><th>C</th><th>U</th><th>V</th><th>D</th><th>Nulls</th><th>Distinct</th><th>Min</th><th>Max</th><th>Mean</th><th>Distribution</th>");
        sb.AppendLine("</tr></thead>");
        sb.AppendLine("<tbody>");

        foreach (var col in profile.Columns)
        {
            var scoreClass = col.Score.Total >= 80 ? "score-good" : col.Score.Total >= 60 ? "score-warn" : "score-bad";
            sb.AppendLine("<tr>");
            sb.AppendLine($"<td><strong>{Escape(col.Name)}</strong></td>");
            sb.AppendLine($"<td class=\"type\">{Escape(col.DuckDbType)}</td>");
            sb.AppendLine($"<td><span class=\"score-badge {scoreClass}\">{col.Score.Total:F0}</span></td>");
            sb.AppendLine($"<td class=\"dim-score\"><div class=\"dim-bar\" style=\"width:{col.Score.Completeness / 25 * 100:F0}%;background:#4CAF50\"></div>{col.Score.Completeness:F0}</td>");
            sb.AppendLine($"<td class=\"dim-score\"><div class=\"dim-bar\" style=\"width:{col.Score.Uniqueness / 25 * 100:F0}%;background:#2196F3\"></div>{col.Score.Uniqueness:F0}</td>");
            sb.AppendLine($"<td class=\"dim-score\"><div class=\"dim-bar\" style=\"width:{col.Score.Validity / 25 * 100:F0}%;background:#FF9800\"></div>{col.Score.Validity:F0}</td>");
            sb.AppendLine($"<td class=\"dim-score\"><div class=\"dim-bar\" style=\"width:{col.Score.Distribution / 25 * 100:F0}%;background:#9C27B0\"></div>{col.Score.Distribution:F0}</td>");
            sb.AppendLine($"<td>{col.NullPercentage:F1}%</td>");
            sb.AppendLine($"<td>{col.DistinctCount:N0}</td>");
            sb.AppendLine($"<td>{FormatStat(col)}</td>");
            sb.AppendLine($"<td>{FormatStatMax(col)}</td>");
            sb.AppendLine($"<td>{FormatStatMean(col)}</td>");
            sb.AppendLine($"<td>{GenerateColumnVisualization(col)}</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody></table>");
        sb.AppendLine("</div>");

        // Comparison section
        if (comparison != null)
        {
            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine($"<h2>🔄 File Comparison</h2>");
            sb.AppendLine($"<p>Comparing <strong>{Escape(comparison.BaselineProfile.FileName)}</strong> vs <strong>{Escape(comparison.ComparisonProfile.FileName)}</strong></p>");
            sb.AppendLine("<div class=\"stats-grid\">");
            sb.AppendLine($"<div class=\"stat\"><span class=\"stat-value\">{comparison.RowCountDelta:+#,0;-#,0;0}</span><span class=\"stat-label\">Row Delta ({comparison.RowCountDeltaPercent:+0.0;-0.0;0.0}%)</span></div>");
            sb.AppendLine($"<div class=\"stat\"><span class=\"stat-value\">{comparison.SchemaChanges.Count}</span><span class=\"stat-label\">Schema Changes</span></div>");
            sb.AppendLine($"<div class=\"stat\"><span class=\"stat-value\">{comparison.DriftScore:F1}</span><span class=\"stat-label\">Drift Score</span></div>");
            sb.AppendLine("</div>");

            if (comparison.SchemaChanges.Count > 0)
            {
                sb.AppendLine("<h3>Schema Changes</h3>");
                sb.AppendLine("<table class=\"columns-table\"><thead><tr><th>Column</th><th>Change</th><th>Details</th></tr></thead><tbody>");
                foreach (var change in comparison.SchemaChanges)
                {
                    var detail = change.ChangeType switch
                    {
                        SchemaChangeType.Added => $"Added ({change.NewType})",
                        SchemaChangeType.Removed => $"Removed ({change.OldType})",
                        SchemaChangeType.TypeChanged => $"{change.OldType} → {change.NewType}",
                        _ => ""
                    };
                    sb.AppendLine($"<tr><td>{Escape(change.ColumnName)}</td><td>{change.ChangeType}</td><td>{Escape(detail)}</td></tr>");
                }
                sb.AppendLine("</tbody></table>");
            }

            sb.AppendLine("</div>");
        }

        // Footer
        sb.AppendLine("<div class=\"footer\">");
        sb.AppendLine($"<p>Generated by <strong>Hip Hip Parquet</strong> &mdash; {profile.AnalyzedAt:yyyy-MM-dd HH:mm:ss}</p>");
        sb.AppendLine("</div>");

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string GenerateScoreGaugeSvg(QualityScore score)
    {
        var cx = 80; var cy = 80; var r = 55;
        var color = score.Color;

        // Both arcs use the EXACT same path — the green one is clipped via stroke-dasharray
        // so there is zero misalignment.
        var arcLength = Math.PI * r; // semicircle circumference
        var filledLength = (score.Total / 100.0) * arcLength;
        var arcPath = $"M{cx - r},{cy} A{r},{r} 0 0,1 {cx + r},{cy}";

        return $@"<svg width=""160"" height=""120"" viewBox=""0 0 160 120"" style=""display:block;margin:0 auto;"">
  <path d=""{arcPath}"" fill=""none"" stroke=""#E0E0E0"" stroke-width=""10"" stroke-linecap=""round""/>
  <path d=""{arcPath}"" fill=""none"" stroke=""{color}"" stroke-width=""10"" stroke-linecap=""round"" stroke-dasharray=""{filledLength:F2} {arcLength:F2}""/>
  <text x=""{cx}"" y=""{cy - 8}"" text-anchor=""middle"" font-size=""26"" font-weight=""bold"" fill=""{color}"">{score.Total:F0}</text>
  <text x=""{cx}"" y=""{cy + 10}"" text-anchor=""middle"" font-size=""11"" fill=""#888"">{score.Grade}</text>
</svg>";
    }

    private static void GenerateScoreBar(StringBuilder sb, string label, double value, double max)
    {
        var pct = value / max * 100;
        sb.AppendLine($"<div class=\"score-bar\"><span class=\"score-bar-label\">{label}</span><div class=\"score-bar-track\"><div class=\"score-bar-fill\" style=\"width:{pct:F0}%\"></div></div><span class=\"score-bar-value\">{value:F1}/{max}</span></div>");
    }

    private static string GenerateScoringKey()
    {
        return @"
<div class=""scoring-key"">
  <h3>📖 Scoring Key</h3>
  <p class=""scoring-key-intro"">Each dimension is scored 0–25. Total score = sum of all four (0–100).</p>
  <div class=""scoring-key-grid"">
    <div class=""scoring-key-item"">
      <span class=""scoring-key-term"">Completeness</span>
      <span class=""scoring-key-desc"">Measures how few null/missing values exist. 0% nulls = 25/25.</span>
    </div>
    <div class=""scoring-key-item"">
      <span class=""scoring-key-term"">Uniqueness</span>
      <span class=""scoring-key-desc"">Measures value diversity. Higher distinct-value ratios score higher; constant columns score low.</span>
    </div>
    <div class=""scoring-key-item"">
      <span class=""scoring-key-term"">Validity</span>
      <span class=""scoring-key-desc"">Measures type consistency and value correctness. Penalizes empty strings, non-nullable columns with nulls, and suspicious patterns.</span>
    </div>
    <div class=""scoring-key-item"">
      <span class=""scoring-key-term"">Distribution</span>
      <span class=""scoring-key-desc"">Measures data spread. Penalizes high outlier rates, extreme skew, and single-value dominance.</span>
    </div>
  </div>
  <div class=""scoring-key-grades"">
    <span class=""grade-badge score-good"">🟢 80–100 Good</span>
    <span class=""grade-badge score-warn"">🟡 60–79 Fair</span>
    <span class=""grade-badge score-bad"">🔴 &lt;60 Needs Review</span>
  </div>
</div>";
    }

    private static string GenerateColumnVisualization(ColumnProfile col)
    {
        // Numeric columns with histogram data → sparkline bar chart
        if (col.Histogram.Count > 0)
        {
            var maxCount = col.Histogram.Max(b => b.Count);
            if (maxCount == 0) return "<span class=\"type\">—</span>";

            var width = 80;
            var height = 24;
            var barWidth = (double)width / col.Histogram.Count;

            var sb = new StringBuilder();
            sb.Append($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");

            for (int i = 0; i < col.Histogram.Count; i++)
            {
                var barHeight = (double)col.Histogram[i].Count / maxCount * height;
                var x = i * barWidth;
                var y = height - barHeight;
                sb.Append($"<rect x=\"{x:F1}\" y=\"{y:F1}\" width=\"{Math.Max(1, barWidth - 1):F1}\" height=\"{barHeight:F1}\" fill=\"#512BD4\" opacity=\"0.7\" rx=\"1\"/>");
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        // All column types with top values → horizontal frequency bars
        if (col.TopValues.Count > 0)
        {
            var maxPct = col.TopValues.Max(v => v.Percentage);
            if (maxPct <= 0) return "<span class=\"type\">—</span>";

            var sb = new StringBuilder();
            sb.Append("<div class=\"freq-bars\">");
            foreach (var tv in col.TopValues.Take(4))
            {
                var w = tv.Percentage / maxPct * 100;
                var label = tv.Value.Length > 12 ? tv.Value[..12] + "…" : tv.Value;
                sb.Append($"<div class=\"freq-row\"><span class=\"freq-label\">{Escape(label)}</span>" +
                    $"<div class=\"freq-track\"><div class=\"freq-fill\" style=\"width:{w:F0}%\"></div></div>" +
                    $"<span class=\"freq-pct\">{tv.Percentage:F0}%</span></div>");
            }
            sb.Append("</div>");
            return sb.ToString();
        }

        return "<span class=\"type\">—</span>";
    }

    private static string FormatStat(ColumnProfile col) => col.Category switch
    {
        ColumnCategory.Numeric => col.Min.HasValue ? $"{col.Min.Value:G6}" : "—",
        ColumnCategory.String => col.MinLength.HasValue ? $"len {col.MinLength}" : "—",
        ColumnCategory.DateTime => col.MinDate ?? "—",
        ColumnCategory.Boolean => col.TrueCount.HasValue ? $"T:{col.TrueCount}" : "—",
        _ => "—"
    };

    private static string FormatStatMax(ColumnProfile col) => col.Category switch
    {
        ColumnCategory.Numeric => col.Max.HasValue ? $"{col.Max.Value:G6}" : "—",
        ColumnCategory.String => col.MaxLength.HasValue ? $"len {col.MaxLength}" : "—",
        ColumnCategory.DateTime => col.MaxDate ?? "—",
        ColumnCategory.Boolean => col.FalseCount.HasValue ? $"F:{col.FalseCount}" : "—",
        _ => "—"
    };

    private static string FormatStatMean(ColumnProfile col) => col.Category switch
    {
        ColumnCategory.Numeric => col.Mean.HasValue ? $"{col.Mean.Value:G6}" : "—",
        ColumnCategory.String => col.AvgLength.HasValue ? $"avg {col.AvgLength.Value:F1}" : "—",
        _ => "—"
    };

    private static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text);

    private static string GetCss() => @"
* { margin: 0; padding: 0; box-sizing: border-box; }
body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #f5f5f5; color: #333; padding: 20px; max-width: 1100px; margin: 0 auto; }
.header { background: linear-gradient(135deg, #512BD4, #7B5FE0); color: white; padding: 24px; border-radius: 10px; margin-bottom: 20px; }
.header h1 { font-size: 24px; }
.header .subtitle { opacity: 0.85; margin-top: 4px; font-size: 13px; }
.card { background: white; border-radius: 10px; padding: 20px; margin-bottom: 16px; box-shadow: 0 1px 3px rgba(0,0,0,0.08); }
.card h2 { font-size: 16px; margin-bottom: 16px; color: #333; }
.card h3 { font-size: 14px; margin: 12px 0 8px; }
.stats-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 12px; }
.stat { text-align: center; padding: 12px; background: #f8f8f8; border-radius: 8px; }
.stat-value { display: block; font-size: 22px; font-weight: bold; color: #512BD4; }
.stat-label { display: block; font-size: 11px; color: #888; margin-top: 2px; }
.score-section { display: flex; align-items: center; gap: 24px; flex-wrap: wrap; overflow: visible; min-height: 120px; }
.score-components { flex: 1; min-width: 200px; max-width: 420px; }
.score-bar { display: flex; align-items: center; margin-bottom: 6px; }
.score-bar-label { width: 90px; font-size: 12px; color: #666; }
.score-bar-track { flex: 1; height: 8px; background: #e8e8e8; border-radius: 4px; overflow: hidden; }
.score-bar-fill { height: 100%; background: #512BD4; border-radius: 4px; transition: width 0.5s; }
.score-bar-value { width: 55px; text-align: right; font-size: 11px; color: #888; margin-left: 8px; }
.finding { display: flex; gap: 10px; padding: 10px; border-radius: 6px; margin-bottom: 6px; background: #f8f8f8; align-items: flex-start; }
.finding-icon { font-size: 16px; }
.finding-desc { font-size: 12px; color: #666; }
.finding.critical { background: #FFF3F3; border-left: 3px solid #F44336; }
.finding.warning { background: #FFF8E1; border-left: 3px solid #FF9800; }
.finding.info { background: #F1F8E9; border-left: 3px solid #4CAF50; }
.columns-table { width: 100%; border-collapse: collapse; font-size: 12px; }
.columns-table th { text-align: left; padding: 8px 10px; background: #f8f8f8; border-bottom: 2px solid #e0e0e0; font-size: 11px; color: #666; text-transform: uppercase; }
.columns-table td { padding: 8px 10px; border-bottom: 1px solid #f0f0f0; }
.columns-table tr:hover { background: #fafafa; }
.dim-score { position: relative; min-width: 36px; text-align: center; font-size: 11px; color: #555; }
.dim-bar { position: absolute; bottom: 0; left: 0; height: 3px; border-radius: 2px; opacity: 0.7; }
.freq-bars { display: flex; flex-direction: column; gap: 2px; min-width: 100px; }
.freq-row { display: flex; align-items: center; gap: 4px; }
.freq-label { font-size: 9px; color: #666; width: 55px; text-align: right; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.freq-track { flex: 1; height: 6px; background: #eee; border-radius: 3px; overflow: hidden; min-width: 40px; }
.freq-fill { height: 100%; background: #7B5FE0; border-radius: 3px; }
.freq-pct { font-size: 9px; color: #999; width: 28px; text-align: right; }
.type { color: #888; font-size: 11px; }
.score-badge { display: inline-block; padding: 2px 8px; border-radius: 10px; font-weight: bold; font-size: 11px; }
.score-good { background: #E8F5E9; color: #2E7D32; }
.score-warn { background: #FFF3E0; color: #E65100; }
.score-bad { background: #FFEBEE; color: #C62828; }
.footer { text-align: center; padding: 16px; color: #999; font-size: 12px; }
.scoring-key { background: #F5F5F5; border: 1px solid #E0E0E0; border-radius: 8px; padding: 16px 20px; margin-top: 16px; }
.scoring-key h3 { margin: 0 0 8px 0; font-size: 15px; }
.scoring-key-intro { margin: 0 0 12px 0; font-size: 13px; color: #666; }
.scoring-key-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 10px 20px; margin-bottom: 12px; }
.scoring-key-item { display: flex; flex-direction: column; }
.scoring-key-term { font-weight: 600; font-size: 13px; color: #333; }
.scoring-key-desc { font-size: 12px; color: #666; margin-top: 2px; }
.scoring-key-grades { display: flex; gap: 12px; flex-wrap: wrap; }
.grade-badge { padding: 4px 10px; border-radius: 12px; font-size: 12px; font-weight: 500; }
.grade-badge.score-good { background: #E8F5E9; color: #2E7D32; }
.grade-badge.score-warn { background: #FFF3E0; color: #E65100; }
.grade-badge.score-bad { background: #FFEBEE; color: #C62828; }
@media print { body { background: white; } .card { box-shadow: none; border: 1px solid #e0e0e0; } }
";
}
