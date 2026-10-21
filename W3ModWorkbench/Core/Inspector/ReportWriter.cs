using System.Net;
using System.Reflection;
using System.Text;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Inspector;

/// <summary>Writes an <see cref="InspectionReport"/> as a page or as Markdown, to keep or to send to the other modder or a site's moderators.</summary>
public static class ReportWriter
{
    private const int MaxMatches = 500;
    private const int MaxEvidence = 150;

    private static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    private static string Caution => L.T("ReportWriter.Caution");

    private static string Date(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? L.T("ReportWriter.NotAvailable");

    private static string Dates(DateTime? from, DateTime? to) => L.T("ReportWriter.DateRange", Date(from), Date(to));

    private static string Pct(double? v) => v is null ? L.T("ReportWriter.NotAvailable") : $"{v * 100:0}%";

    private static string GameCode(InspectionReport r) =>
        r.GameCodeIgnored ? L.T("ReportWriter.GameSetAside", r.GameScriptsRead) : L.T("ReportWriter.GameNotSetAside");

    public static string Html(InspectionReport r)
    {
        var s = new StringBuilder();
        string E(string? t) => WebUtility.HtmlEncode(t ?? "");
        string T(string key, params object?[] args) => E(L.T(key, args));
        string color = r.Verdict switch
        {
            VerdictLevel.Extensive => "#d13438",
            VerdictLevel.Significant => "#e8590c",
            VerdictLevel.Some => "#c28a00",
            VerdictLevel.Minor => "#2f7dd1",
            _ => "#2e9e55",
        };

        // loc-ignore-start: the page's markup and style; its words are T(…) and the report's own
        s.Append($$$"""
<!doctype html><html lang="{{{E(Loc.Instance.Language)}}}"><head><meta charset="utf-8"><title>{{{T("ReportWriter.Title")}}}</title>
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
        s.Append($"<h1>{T("ReportWriter.Title")}</h1><div class=\"sub\">W3 Mod Workbench {E(Version)} · {r.Created:yyyy-MM-dd HH:mm}</div>");

        s.Append("<div class=\"grid\">");
        foreach (var (label, scan) in new[] { (L.T("ReportWriter.Original"), r.Original), (L.T("ReportWriter.Suspect"), r.Suspect) })
            s.Append($"<div class=\"card\"><b>{E(label)}</b><div class=\"mono\">{E(scan.Path)}</div><div class=\"note\">{E(scan.Summary)}<br>{T("ReportWriter.FilesDated", Dates(scan.Oldest, scan.Newest))}</div></div>");
        s.Append("</div>");

        s.Append($"<div class=\"card verdict\"><div class=\"big\">{r.Headline * 100:0}%</div><div><div class=\"title\">{E(r.VerdictTitle)}</div><div>{E(r.VerdictText)}</div></div></div>");
        s.Append("<div class=\"card\">");
        s.Append($"<div>{T("ReportWriter.OriginalInSuspectLong")} <b>{r.OriginalInSuspect * 100:0}%</b></div><div class=\"bar\"><i style=\"width:{r.OriginalInSuspect * 100:0}%\"></i></div>");
        s.Append($"<div>{T("ReportWriter.SuspectOfOriginalLong")} <b>{r.SuspectInOriginal * 100:0}%</b></div><div class=\"bar\"><i style=\"width:{r.SuspectInOriginal * 100:0}%\"></i></div>");
        s.Append("</div>");

        s.Append($"<h2>{T("ReportWriter.KeyFindings")}</h2><div class=\"card\"><ul>");
        foreach (string f in r.Findings) s.Append($"<li>{E(f)}</li>");
        s.Append("</ul></div>");

        s.Append($"<h2>{T("ReportWriter.ByKind")}</h2><div class=\"card\"><table><tr><th>{T("ReportWriter.Content")}</th><th>{T("ReportWriter.OriginalFiles")}</th><th>{T("ReportWriter.SuspectFiles")}</th><th>{T("ReportWriter.OriginalInSuspect")}</th><th>{T("ReportWriter.SuspectInOriginal")}</th><th>{T("ReportWriter.Detail")}</th></tr>");
        foreach (var c in r.Categories)
            s.Append($"<tr><td>{E(c.Name)}</td><td class=\"num\">{c.OriginalFiles:N0}</td><td class=\"num\">{c.SuspectFiles:N0}</td><td class=\"num\">{Pct(c.OriginalInSuspect)}</td><td class=\"num\">{Pct(c.SuspectInOriginal)}</td><td class=\"note\">{E(c.Detail)}</td></tr>");
        s.Append("</table></div>");

        if (r.Style.Count > 0)
        {
            s.Append($"<h2>{T("ReportWriter.CodingStyle", r.StyleSimilarity * 100)}</h2><div class=\"card\"><div class=\"note\">{E(r.StyleNote)}</div><table><tr><th>{T("ReportWriter.Habit")}</th><th>{T("ReportWriter.Original")}</th><th>{T("ReportWriter.Suspect")}</th><th>{T("ReportWriter.Alike")}</th></tr>");
            foreach (var f in r.Style)
                s.Append($"<tr><td>{E(f.Name)} <span class=\"note\">{E(f.Habit)}</span></td><td>{E(f.Original)}</td><td>{E(f.Suspect)}</td><td class=\"num\">{f.Similarity * 100:0}%</td></tr>");
            s.Append("</table></div>");
        }

        s.Append($"<h2>{T("ReportWriter.MatchingFiles", Math.Min(r.Matches.Count, MaxMatches), r.Matches.Count)}</h2><div class=\"card\"><table><tr><th>{T("ReportWriter.Match")}</th><th>{T("ReportWriter.Type")}</th><th>{T("ReportWriter.Original")}</th><th>{T("ReportWriter.Suspect")}</th><th>{T("ReportWriter.OriginalInSuspect")}</th><th>{T("ReportWriter.SuspectInOriginal")}</th><th>{T("ReportWriter.Note")}</th></tr>");
        foreach (var m in r.Matches.Take(MaxMatches))
            s.Append($"<tr><td class=\"tag\">{E(m.LevelText)}</td><td>{E(m.KindText)}</td><td class=\"mono\">{E(m.Original.Origin)}</td><td class=\"mono\">{E(m.Suspect.Origin)}</td><td class=\"num\">{m.OriginalInSuspect * 100:0}%</td><td class=\"num\">{m.SuspectInOriginal * 100:0}%</td><td class=\"note\">{E(m.Note)}{(m.SamePath ? T("ReportWriter.SamePath") : "")}</td></tr>");
        s.Append("</table></div>");

        foreach (var group in r.Evidence.GroupBy(e => e.Category))
        {
            s.Append($"<h2>{E(group.Key)} ({group.Count():N0})</h2><div class=\"card\"><table><tr><th>{T("ReportWriter.Item")}</th><th>{T("ReportWriter.InOriginal")}</th><th>{T("ReportWriter.InSuspect")}</th></tr>");
            foreach (var e in group.Take(MaxEvidence))
                s.Append($"<tr><td>{E(e.Text)}</td><td class=\"mono\">{E(e.InOriginal)}</td><td class=\"mono\">{E(e.InSuspect)}</td></tr>");
            s.Append("</table></div>");
        }

        if (r.Original.Warnings.Count + r.Suspect.Warnings.Count > 0)
        {
            s.Append($"<h2>{T("ReportWriter.Unreadable")}</h2><div class=\"card\"><ul>");
            foreach (string w in r.Original.Warnings.Concat(r.Suspect.Warnings)) s.Append($"<li class=\"mono\">{E(w)}</li>");
            s.Append("</ul></div>");
        }

        s.Append($"<div class=\"note\">{E(Caution)}{E(GameCode(r))}</div>");
        s.Append("</main></body></html>");
        // loc-ignore-end
        return s.ToString();
    }

    public static string Markdown(InspectionReport r)
    {
        var s = new StringBuilder();
        static string Cell(string? t) => (t ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        static string Row(params string[] cells) => "| " + string.Join(" | ", cells) + " |";

        s.AppendLine("# " + L.T("ReportWriter.Title")).AppendLine();
        s.AppendLine(L.T("ReportWriter.MadeWith", Version, r.Created)).AppendLine();
        s.AppendLine("| " + Row(L.T("ReportWriter.Path"), L.T("ReportWriter.Contents"), L.T("ReportWriter.FileDates"))).AppendLine("|---|---|---|---|");
        s.AppendLine(Row($"**{L.T("ReportWriter.Original")}**", $"`{r.Original.Path}`", Cell(r.Original.Summary), Dates(r.Original.Oldest, r.Original.Newest)));
        s.AppendLine(Row($"**{L.T("ReportWriter.Suspect")}**", $"`{r.Suspect.Path}`", Cell(r.Suspect.Summary), Dates(r.Suspect.Oldest, r.Suspect.Newest))).AppendLine();

        s.AppendLine($"## {r.VerdictTitle}: {r.Headline * 100:0}%").AppendLine();
        s.AppendLine(r.VerdictText).AppendLine();
        s.AppendLine($"- {L.T("ReportWriter.OriginalInSuspectLong")}: **{r.OriginalInSuspect * 100:0}%**");
        s.AppendLine($"- {L.T("ReportWriter.SuspectOfOriginalLong")}: **{r.SuspectInOriginal * 100:0}%**").AppendLine();

        s.AppendLine("## " + L.T("ReportWriter.KeyFindings")).AppendLine();
        foreach (string f in r.Findings) s.AppendLine($"- {f}");
        s.AppendLine();

        s.AppendLine("## " + L.T("ReportWriter.ByKind")).AppendLine();
        s.AppendLine(Row(L.T("ReportWriter.Content"), L.T("ReportWriter.OriginalFiles"), L.T("ReportWriter.SuspectFiles"), L.T("ReportWriter.OriginalInSuspect"),
            L.T("ReportWriter.SuspectInOriginal"), L.T("ReportWriter.Detail"))).AppendLine("|---|---:|---:|---:|---:|---|");
        foreach (var c in r.Categories)
            s.AppendLine($"| {c.Name} | {c.OriginalFiles:N0} | {c.SuspectFiles:N0} | {Pct(c.OriginalInSuspect)} | {Pct(c.SuspectInOriginal)} | {Cell(c.Detail)} |");
        s.AppendLine();

        if (r.Style.Count > 0)
        {
            s.AppendLine("## " + L.T("ReportWriter.CodingStyle", r.StyleSimilarity * 100)).AppendLine().AppendLine(r.StyleNote).AppendLine();
            s.AppendLine(Row(L.T("ReportWriter.Habit"), L.T("ReportWriter.Original"), L.T("ReportWriter.Suspect"), L.T("ReportWriter.Alike"))).AppendLine("|---|---|---|---:|");
            foreach (var f in r.Style) s.AppendLine($"| {f.Name} | {f.Original} | {f.Suspect} | {f.Similarity * 100:0}% |");
            s.AppendLine();
        }

        s.AppendLine("## " + L.T("ReportWriter.MatchingFiles", Math.Min(r.Matches.Count, MaxMatches), r.Matches.Count)).AppendLine();
        s.AppendLine(Row(L.T("ReportWriter.Match"), L.T("ReportWriter.Type"), L.T("ReportWriter.Original"), L.T("ReportWriter.Suspect"), L.T("ReportWriter.OriginalInSuspectShort"),
            L.T("ReportWriter.SuspectInOriginalShort"), L.T("ReportWriter.Note"))).AppendLine("|---|---|---|---|---:|---:|---|");
        foreach (var m in r.Matches.Take(MaxMatches))
            s.AppendLine($"| {m.LevelText} | {m.KindText} | `{Cell(m.Original.Origin)}` | `{Cell(m.Suspect.Origin)}` | {m.OriginalInSuspect * 100:0}% | {m.SuspectInOriginal * 100:0}% | {Cell(m.Note)}{(m.SamePath ? L.T("ReportWriter.SamePath") : "")} |");
        s.AppendLine();

        foreach (var group in r.Evidence.GroupBy(e => e.Category))
        {
            s.AppendLine($"## {group.Key} ({group.Count():N0})").AppendLine();
            s.AppendLine(Row(L.T("ReportWriter.Item"), L.T("ReportWriter.InOriginal"), L.T("ReportWriter.InSuspect"))).AppendLine("|---|---|---|");
            foreach (var e in group.Take(MaxEvidence)) s.AppendLine($"| {Cell(e.Text)} | `{Cell(e.InOriginal)}` | `{Cell(e.InSuspect)}` |");
            s.AppendLine();
        }

        s.AppendLine("---").AppendLine().AppendLine($"*{Caution}*");
        return s.ToString();
    }
}
