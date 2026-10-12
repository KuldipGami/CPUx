using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using CPUx.Core;
using CPUx.ViewModels;
using Microsoft.Win32;

namespace CPUx.Services;

/// <summary>Exports everything the app knows into a single HTML, JSON or text report.</summary>
public static class ReportExporter
{
    public static async Task ExportAsync(MainViewModel vm, string format)
    {
        format = format.ToLowerInvariant();
        var dlg = new SaveFileDialog
        {
            Title = "Export system report",
            FileName = $"CPUx-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}",
            Filter = format switch
            {
                "json" => "JSON report (*.json)|*.json",
                "txt" => "Text report (*.txt)|*.txt",
                _ => "HTML report (*.html)|*.html",
            },
            DefaultExt = format == "txt" ? ".txt" : format == "json" ? ".json" : ".html",
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            await WriteAsync(vm, dlg.FileName);
            var open = MessageBox.Show($"Report saved to\n{dlg.FileName}\n\nOpen it now?", "Export complete",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (open == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Export", ex);
            MessageBox.Show($"Couldn't save the report: {ex.Message}", "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Writes a report; the format follows the file extension (.html, .json or .txt).</summary>
    public static async Task WriteAsync(MainViewModel vm, string path)
    {
        if (vm.Devices.Groups.Count == 0)
        {
            try { await vm.Devices.LoadCommand.ExecuteAsync(null); } catch { }
        }

        var pages = vm.ReportPages()
            .Select(p => (p.Page, Sections: p.Sections.Where(s => s.Items.Count > 0).ToList()))
            .Where(p => p.Sections.Count > 0)
            .ToList();

        string content = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => Json(pages),
            ".txt" => Text(pages),
            _ => Html(pages),
        };
        // Plain text gets a BOM so older editors pick the right encoding.
        bool bom = Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(bom));
    }

    private static string Header =>
        $"CPUx system report · {Environment.MachineName} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

    private static string Text(List<(string Page, List<InfoSection> Sections)> pages)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        sb.AppendLine(new string('=', Header.Length));
        foreach (var (page, sections) in pages)
        {
            sb.AppendLine();
            sb.AppendLine(page.ToUpperInvariant());
            sb.AppendLine(new string('-', page.Length));
            foreach (var s in sections)
            {
                sb.Append(s.ToText());
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static string Json(List<(string Page, List<InfoSection> Sections)> pages)
    {
        var root = new Dictionary<string, object>
        {
            ["generator"] = "CPUx",
            ["computer"] = Environment.MachineName,
            ["generated"] = DateTime.Now.ToString("o"),
            ["pages"] = pages.ToDictionary(
                p => p.Page,
                p => p.Sections.Select(s => new
                {
                    title = s.Title,
                    items = s.Items.GroupBy(i => i.Label).ToDictionary(g => g.Key, g => g.Count() == 1 ? (object)g.First().Value : g.Select(x => x.Value).ToArray()),
                }).ToList()),
        };
        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static string Html(List<(string Page, List<InfoSection> Sections)> pages)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>CPUx report</title>
<style>
:root{--bg:#0d1017;--card:#171c25;--stroke:#222936;--text:#eceff4;--muted:#98a1b0;--accent:#4c8dff}
@media (prefers-color-scheme: light){:root{--bg:#eef1f6;--card:#fff;--stroke:#e2e6ee;--text:#111827;--muted:#545e6e;--accent:#2563eb}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}
header{padding:32px 32px 8px}h1{margin:0;font-size:26px}header p{margin:4px 0 0;color:var(--muted)}
nav{display:flex;flex-wrap:wrap;gap:8px;padding:12px 32px}nav a{color:var(--muted);text-decoration:none;padding:5px 12px;border-radius:8px;background:color-mix(in srgb,var(--text) 6%,transparent)}
nav a:hover{color:var(--text)}main{padding:8px 32px 48px}h2{margin:32px 0 12px;font-size:18px}
.grid{columns:440px;column-gap:16px}.card{break-inside:avoid;background:var(--card);border:1px solid var(--stroke);border-radius:12px;padding:16px 18px;margin:0 0 16px}
.card h3{margin:0 0 10px;font-size:14px}table{width:100%;border-collapse:collapse}td{padding:5px 0;border-bottom:1px solid var(--stroke);vertical-align:top}
tr:last-child td{border-bottom:0}td:first-child{color:var(--muted);width:42%;padding-right:12px}td:last-child{word-break:break-word}
@media (max-width:600px){header,nav,main{padding-left:16px;padding-right:16px}.grid{columns:1}}
</style></head><body>
""");
        sb.Append($"<header><h1>System report</h1><p>{E(Environment.MachineName)} · generated by CPUx on {DateTime.Now:dddd, d MMMM yyyy 'at' HH:mm}</p></header><nav>");
        foreach (var (page, _) in pages) sb.Append($"<a href=\"#{Id(page)}\">{E(page)}</a>");
        sb.Append("</nav><main>");
        foreach (var (page, sections) in pages)
        {
            sb.Append($"<h2 id=\"{Id(page)}\">{E(page)}</h2><div class=\"grid\">");
            foreach (var s in sections)
            {
                sb.Append($"<div class=\"card\"><h3>{E(s.Title)}</h3><table>");
                foreach (var i in s.Items) sb.Append($"<tr><td>{E(i.Label)}</td><td>{E(i.Value)}</td></tr>");
                sb.Append("</table></div>");
            }
            sb.Append("</div>");
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string Id(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
