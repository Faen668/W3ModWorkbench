using System.Net;
using System.Reflection;
using System.Text;

namespace W3ModWorkbench.Core.Inspector;

/// <summary>Writes an <see cref="InspectionReport"/> as a page or as Markdown, to keep or to send to the other modder or a site's moderators.</summary>
public static class ReportWriter
{
    private const int MaxMatches = 500;
    private const int MaxEvidence = 150;

    private static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    private const string Caution = "Similarity is evidence, not proof. Mods for the same game share the game's own code, common frameworks (such as a mod menu "
        + "library) and ordinary patterns. Weigh the matches below, particularly distinctive comments, renamed functions and identical files, and compare dates with the authors.";

    private static string Date(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "n/a";

    private static string Pct(double? v) => v is null ? "n/a" : $"{v * 100:0}%";

    public static string Html(InspectionReport r)
    {
        var s = new StringBuilder();
        string E(string? t) => WebUtility.HtmlEncode(t ?? "");
        string color = r.Verdict switch
        {
            VerdictLevel.Extensive => "#d13438",
            VerdictLevel.Significant => "#e8590c",
            VerdictLevel.Some => "#c28a00",
            VerdictLevel.Minor => "#2f7dd1",
            _ => "#2e9e55",
        };

        s.Append($$$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Source Inspector report</title>
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
:root{--bg:#f6f7f9;--card:#fff;--text:#1b1f24;--muted:#5b6570;--line:#e1e5ea;--accent:{{{color}}}}
@media (prefers-color-scheme:dark){:root{--bg:#14171b;--card:#1d2127;--text:#e8ebef;--muted:#9aa4af;--line:#2d333b}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
main{max-width:1100px;margin:0 auto;padding:28px 20px 60px}h1{font-size:22px;margin:0}h2{font-size:16px;margin:28px 0 10px}
.sub{color:var(--muted);margin:2px 0 18px}.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px 18px;margin-bottom:14px}
.verdict{display:flex;gap:22px;align-items:center;flex-wrap:wrap}.big{font-size:44px;font-weight:600;color:var(--accent);line-height:1}
.title{font-size:18px;font-weight:600}.bar{height:8px;background:var(--line);border-radius:4px;overflow:hidden;margin:4px 0 10px}
.bar>i{display:block;height:100%;background:var(--accent)}.grid{display:grid;grid-template-columns:1fr 1fr;gap:14px}
table{width:100%;border-collapse:collapse;font-size:13px}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}
th{color:var(--muted);font-weight:600}td.num{white-space:nowrap}.tag{font-weight:600}.mono{font-family:Consolas,monospace;font-size:12px;word-break:break-all}
.note{color:var(--muted);font-size:12.5px}ul{margin:6px 0 0;padding-left:20px}li{margin:3px 0}
@media (max-width:760px){.grid{grid-template-columns:1fr}}
</style></head><body><main>
""");
        s.Append($"<h1>Source Inspector report</h1><div class=\"sub\">W3 Mod Workbench {E(Version)} · {r.Created:yyyy-MM-dd HH:mm}</div>");

        s.Append("<div class=\"grid\">");
        foreach (var (label, scan) in new[] { ("Original", r.Original), ("Suspect", r.Suspect) })
            s.Append($"<div class=\"card\"><b>{label}</b><div class=\"mono\">{E(scan.Path)}</div><div class=\"note\">{E(scan.Summary)}<br>Files dated {Date(scan.Oldest)} to {Date(scan.Newest)}</div></div>");
        s.Append("</div>");

        s.Append($"<div class=\"card verdict\"><div class=\"big\">{r.Headline * 100:0}%</div><div><div class=\"title\">{E(r.VerdictTitle)}</div><div>{E(r.VerdictText)}</div></div></div>");
        s.Append("<div class=\"card\">");
        s.Append($"<div>Original found in the suspect <b>{r.OriginalInSuspect * 100:0}%</b></div><div class=\"bar\"><i style=\"width:{r.OriginalInSuspect * 100:0}%\"></i></div>");
        s.Append($"<div>Suspect made of the original <b>{r.SuspectInOriginal * 100:0}%</b></div><div class=\"bar\"><i style=\"width:{r.SuspectInOriginal * 100:0}%\"></i></div>");
        s.Append("</div>");

        s.Append("<h2>Key findings</h2><div class=\"card\"><ul>");
        foreach (string f in r.Findings) s.Append($"<li>{E(f)}</li>");
        s.Append("</ul></div>");

        s.Append("<h2>By kind of content</h2><div class=\"card\"><table><tr><th>Content</th><th>Original files</th><th>Suspect files</th><th>Original in suspect</th><th>Suspect in original</th><th>Detail</th></tr>");
        foreach (var c in r.Categories)
            s.Append($"<tr><td>{E(c.Name)}</td><td class=\"num\">{c.OriginalFiles:N0}</td><td class=\"num\">{c.SuspectFiles:N0}</td><td class=\"num\">{Pct(c.OriginalInSuspect)}</td><td class=\"num\">{Pct(c.SuspectInOriginal)}</td><td class=\"note\">{E(c.Detail)}</td></tr>");
        s.Append("</table></div>");

        if (r.Style.Count > 0)
        {
            s.Append($"<h2>Coding style: {r.StyleSimilarity * 100:0}% alike</h2><div class=\"card\"><div class=\"note\">{E(r.StyleNote)}</div><table><tr><th>Habit</th><th>Original</th><th>Suspect</th><th>Alike</th></tr>");
            foreach (var f in r.Style)
                s.Append($"<tr><td>{E(f.Name)} <span class=\"note\">{E(f.Habit)}</span></td><td>{E(f.Original)}</td><td>{E(f.Suspect)}</td><td class=\"num\">{f.Similarity * 100:0}%</td></tr>");
            s.Append("</table></div>");
        }

        s.Append($"<h2>Matching files ({Math.Min(r.Matches.Count, MaxMatches):N0} of {r.Matches.Count:N0})</h2><div class=\"card\"><table><tr><th>Match</th><th>Type</th><th>Original</th><th>Suspect</th><th>Original in suspect</th><th>Suspect in original</th><th>Note</th></tr>");
        foreach (var m in r.Matches.Take(MaxMatches))
            s.Append($"<tr><td class=\"tag\">{E(m.LevelText)}</td><td>{E(m.KindText)}</td><td class=\"mono\">{E(m.Original.Origin)}</td><td class=\"mono\">{E(m.Suspect.Origin)}</td><td class=\"num\">{m.OriginalInSuspect * 100:0}%</td><td class=\"num\">{m.SuspectInOriginal * 100:0}%</td><td class=\"note\">{E(m.Note)}{(m.SamePath ? " · same path" : "")}</td></tr>");
        s.Append("</table></div>");

        foreach (var group in r.Evidence.GroupBy(e => e.Category))
        {
            s.Append($"<h2>{E(group.Key)} ({group.Count():N0})</h2><div class=\"card\"><table><tr><th>Item</th><th>In the original</th><th>In the suspect</th></tr>");
            foreach (var e in group.Take(MaxEvidence))
                s.Append($"<tr><td>{E(e.Text)}</td><td class=\"mono\">{E(e.InOriginal)}</td><td class=\"mono\">{E(e.InSuspect)}</td></tr>");
            s.Append("</table></div>");
        }

        if (r.Original.Warnings.Count + r.Suspect.Warnings.Count > 0)
        {
            s.Append("<h2>Files that could not be read</h2><div class=\"card\"><ul>");
            foreach (string w in r.Original.Warnings.Concat(r.Suspect.Warnings)) s.Append($"<li class=\"mono\">{E(w)}</li>");
            s.Append("</ul></div>");
        }

        s.Append($"<div class=\"note\">{E(Caution)}{(r.GameCodeIgnored ? $" The game's own scripts ({r.GameScriptsRead:N0} files) were set aside." : " The game's own scripts were not set aside, so shared game code counts as a match.")}</div>");
        s.Append("</main></body></html>");
        return s.ToString();
    }

    public static string Markdown(InspectionReport r)
    {
        var s = new StringBuilder();
        static string Cell(string? t) => (t ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

        s.AppendLine("# Source Inspector report").AppendLine();
        s.AppendLine($"Made with W3 Mod Workbench {Version} on {r.Created:yyyy-MM-dd HH:mm}.").AppendLine();
        s.AppendLine("| | Path | Contents | File dates |").AppendLine("|---|---|---|---|");
        s.AppendLine($"| **Original** | `{r.Original.Path}` | {Cell(r.Original.Summary)} | {Date(r.Original.Oldest)} to {Date(r.Original.Newest)} |");
        s.AppendLine($"| **Suspect** | `{r.Suspect.Path}` | {Cell(r.Suspect.Summary)} | {Date(r.Suspect.Oldest)} to {Date(r.Suspect.Newest)} |").AppendLine();

        s.AppendLine($"## {r.VerdictTitle}: {r.Headline * 100:0}%").AppendLine();
        s.AppendLine(r.VerdictText).AppendLine();
        s.AppendLine($"- Original found in the suspect: **{r.OriginalInSuspect * 100:0}%**");
        s.AppendLine($"- Suspect made of the original: **{r.SuspectInOriginal * 100:0}%**").AppendLine();

        s.AppendLine("## Key findings").AppendLine();
        foreach (string f in r.Findings) s.AppendLine($"- {f}");
        s.AppendLine();

        s.AppendLine("## By kind of content").AppendLine();
        s.AppendLine("| Content | Original files | Suspect files | Original in suspect | Suspect in original | Detail |").AppendLine("|---|---:|---:|---:|---:|---|");
        foreach (var c in r.Categories)
            s.AppendLine($"| {c.Name} | {c.OriginalFiles:N0} | {c.SuspectFiles:N0} | {Pct(c.OriginalInSuspect)} | {Pct(c.SuspectInOriginal)} | {Cell(c.Detail)} |");
        s.AppendLine();

        if (r.Style.Count > 0)
        {
            s.AppendLine($"## Coding style: {r.StyleSimilarity * 100:0}% alike").AppendLine().AppendLine(r.StyleNote).AppendLine();
            s.AppendLine("| Habit | Original | Suspect | Alike |").AppendLine("|---|---|---|---:|");
            foreach (var f in r.Style) s.AppendLine($"| {f.Name} | {f.Original} | {f.Suspect} | {f.Similarity * 100:0}% |");
            s.AppendLine();
        }

        s.AppendLine($"## Matching files ({Math.Min(r.Matches.Count, MaxMatches):N0} of {r.Matches.Count:N0})").AppendLine();
        s.AppendLine("| Match | Type | Original | Suspect | Orig. in susp. | Susp. in orig. | Note |").AppendLine("|---|---|---|---|---:|---:|---|");
        foreach (var m in r.Matches.Take(MaxMatches))
            s.AppendLine($"| {m.LevelText} | {m.KindText} | `{Cell(m.Original.Origin)}` | `{Cell(m.Suspect.Origin)}` | {m.OriginalInSuspect * 100:0}% | {m.SuspectInOriginal * 100:0}% | {Cell(m.Note)}{(m.SamePath ? " · same path" : "")} |");
        s.AppendLine();

        foreach (var group in r.Evidence.GroupBy(e => e.Category))
        {
            s.AppendLine($"## {group.Key} ({group.Count():N0})").AppendLine();
            s.AppendLine("| Item | In the original | In the suspect |").AppendLine("|---|---|---|");
            foreach (var e in group.Take(MaxEvidence)) s.AppendLine($"| {Cell(e.Text)} | `{Cell(e.InOriginal)}` | `{Cell(e.InSuspect)}` |");
            s.AppendLine();
        }

        s.AppendLine("---").AppendLine().AppendLine($"*{Caution}*");
        return s.ToString();
    }
}
