using System.Text.RegularExpressions;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Inspector;

/// <summary>One habit of a coder, measured for both sources.</summary>
public sealed record StyleFeature(string Name, string Habit, string Original, string Suspect, double Similarity);

/// <summary>
/// A fingerprint of how a source's scripts are written, not what they do: indentation, where braces go, spacing,
/// naming habits, comments and function size. Names can be changed in a copy, but habits tend to come along.
/// </summary>
public sealed partial class StyleProfile
{
    /// <summary>Fewer lines than this and habits are noise.</summary>
    public const int MinLines = 150;

    public int Lines { get; private init; }
    public double TabIndent { get; private init; }
    public double IndentWidth { get; private init; }
    public double BraceOwnLine { get; private init; }
    public double ElseOwnLine { get; private init; }
    public double SpaceAfterKeyword { get; private init; }
    public double SpaceAroundOperators { get; private init; }
    public double SpaceAfterComma { get; private init; }
    public double CommentDensity { get; private init; }
    public double BlockComments { get; private init; }
    public double BlankLines { get; private init; }
    public double AverageLineLength { get; private init; }
    public double CamelLocals { get; private init; }
    public double PascalLocals { get; private init; }
    public double SnakeLocals { get; private init; }
    public double PrefixedLocals { get; private init; }
    public double ExplicitThis { get; private init; }
    public double AverageFunctionLength { get; private init; }
    public double CrlfLines { get; private init; }

    [GeneratedRegex(@"\b(?:if|for|while|switch)\s*\(")] private static partial Regex KeywordSpaced();
    [GeneratedRegex(@"\b(?:if|for|while|switch)\(")] private static partial Regex KeywordTight();
    [GeneratedRegex(@"\S ?[=!<>]=? ?\S|\S [+\-*/] \S")] private static partial Regex OperatorAny();
    [GeneratedRegex(@"[\w\)\]] (?:==|!=|<=|>=|=|\+=|-=|\*=|/=|<|>|&&|\|\|) [\w\(\-!]")] private static partial Regex OperatorSpaced();
    [GeneratedRegex(@"[\w\)\]](?:==|!=|<=|>=|\+=|-=|\*=|/=|&&|\|\|)[\w\(\-!]")] private static partial Regex OperatorTight();
    [GeneratedRegex(@",\s")] private static partial Regex CommaSpaced();
    [GeneratedRegex(@",\S")] private static partial Regex CommaTight();
    [GeneratedRegex(@"\bvar\s+([A-Za-z_][\w\s,]*?)\s*:")] private static partial Regex VarDeclaration();
    [GeneratedRegex(@"\bthis\.")] private static partial Regex ThisUse();

    public static StyleProfile? Compute(IEnumerable<string> texts)
    {
        int lines = 0, blank = 0, tabbed = 0, spaced = 0, braceOwn = 0, braceSame = 0, elseOwn = 0, elseSame = 0;
        int commentLines = 0, blockLines = 0, crlf = 0;
        long length = 0, indentSum = 0;
        int kwSpaced = 0, kwTight = 0, opSpaced = 0, opTight = 0, commaSpaced = 0, commaTight = 0, thisUses = 0;
        int camel = 0, pascal = 0, snake = 0, prefixed = 0, locals = 0;
        long functionLines = 0;
        int functions = 0;

        foreach (string text in texts)
        {
            crlf += text.Count(c => c == '\r');
            bool inBlock = false;
            foreach (string full in text.Split('\n'))
            {
                string line = full.TrimEnd('\r', ' ', '\t');
                lines++;
                if (line.Length == 0) { blank++; continue; }
                length += line.Length;

                string trimmed = line.TrimStart();
                if (inBlock || trimmed.StartsWith("/*"))
                {
                    blockLines++;
                    commentLines++;
                    inBlock = !trimmed.Contains("*/");
                    continue;
                }
                if (trimmed.StartsWith("//")) { commentLines++; continue; }

                if (line[0] == '\t') tabbed++;
                else if (line[0] == ' ')
                {
                    spaced++;
                    indentSum += line.Length - trimmed.Length;
                }

                if (trimmed == "{") braceOwn++;
                else if (trimmed.EndsWith('{') && trimmed.Length > 1) braceSame++;
                if (trimmed.StartsWith("else")) elseOwn++;
                else if (trimmed.StartsWith("} else")) elseSame++;

                // only code is measured for spacing, not the strings or comments in it
                string code = StripNoise(trimmed);
                kwSpaced += KeywordSpaced().Count(code);
                kwTight += KeywordTight().Count(code);
                opSpaced += OperatorSpaced().Count(code);
                opTight += OperatorTight().Count(code);
                commaSpaced += CommaSpaced().Count(code);
                commaTight += CommaTight().Count(code);
                thisUses += ThisUse().Count(code);

                if (trimmed.StartsWith("function ") || trimmed.Contains(" function ")) functions++;
                else if (functions > 0) functionLines++;

                foreach (Match m in VarDeclaration().Matches(code))
                    foreach (string raw in m.Groups[1].Value.Split(','))
                    {
                        string name = raw.Trim();
                        if (name.Length == 0 || name.Contains(' ')) continue;
                        locals++;
                        if (name.Contains('_') && name.Any(char.IsLower) && name.TrimStart('_').Contains('_')) snake++;
                        else if (char.IsUpper(name[0])) pascal++;
                        else if (name.Length > 1 && name.Skip(1).Any(char.IsUpper)) camel++;
                        if (name.StartsWith('_') || (name.Length > 2 && "bmfsiap".Contains(name[0]) && char.IsUpper(name[1]))
                            || name.StartsWith("the", StringComparison.Ordinal) && name.Length > 3 && char.IsUpper(name[3]))
                            prefixed++;
                    }
            }
        }

        if (lines < MinLines) return null;
        int code2 = Math.Max(1, lines - blank);
        static double Ratio(double a, double b) => a + b == 0 ? 0 : a / (a + b);

        return new StyleProfile
        {
            Lines = lines,
            TabIndent = Ratio(tabbed, spaced),
            IndentWidth = spaced == 0 ? 0 : (double)indentSum / spaced,
            BraceOwnLine = Ratio(braceOwn, braceSame),
            ElseOwnLine = Ratio(elseOwn, elseSame),
            SpaceAfterKeyword = Ratio(kwSpaced, kwTight),
            SpaceAroundOperators = Ratio(opSpaced, opTight),
            SpaceAfterComma = Ratio(commaSpaced, commaTight),
            CommentDensity = (double)commentLines / code2,
            BlockComments = Ratio(blockLines, commentLines - blockLines),
            BlankLines = (double)blank / lines,
            AverageLineLength = (double)length / code2,
            CamelLocals = Ratio(camel, locals - camel),
            PascalLocals = Ratio(pascal, locals - pascal),
            SnakeLocals = Ratio(snake, locals - snake),
            PrefixedLocals = Ratio(prefixed, locals - prefixed),
            ExplicitThis = Math.Min(1, (double)thisUses / code2 * 20),
            AverageFunctionLength = functions == 0 ? 0 : (double)functionLines / functions,
            CrlfLines = Ratio(crlf, Math.Max(0, lines - crlf)),
        };
    }

    private static string StripNoise(string line)
    {
        int comment = line.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0) line = line[..comment];
        return Regex.Replace(line, "\"[^\"]*\"|'[^']*'", "\"\"");
    }

    /// <summary>The two profiles side by side, habit by habit, with how alike each habit is, 0 to 1.</summary>
    public static List<StyleFeature> Compare(StyleProfile a, StyleProfile b)
    {
        var features = new List<StyleFeature>();
        void Add(string name, string habit, double x, double y, Func<double, string> show, double scale = 1)
            => features.Add(new StyleFeature(name, habit, show(x), show(y), Math.Clamp(1 - Math.Abs(x - y) / scale, 0, 1)));

        static string Pct(double v) => $"{v:P0}";
        static string Words(double v, string yes, string no) => v >= 0.65 ? yes : v <= 0.35 ? no : L.T("StyleProfile.Mixed");

        Add(L.T("StyleProfile.Indentation"), L.T("StyleProfile.TabsSpaces"), a.TabIndent, b.TabIndent, v => Words(v, L.T("StyleProfile.Tabs"), L.T("StyleProfile.Spaces")));
        if (a.TabIndent < 0.5 && b.TabIndent < 0.5)
            Add(L.T("StyleProfile.IndentWidth"), L.T("StyleProfile.SpacesPerLevel"), a.IndentWidth, b.IndentWidth, v => $"{v:0.#}", 4);
        Add(L.T("StyleProfile.Braces"), L.T("StyleProfile.TheirOwnLine"), a.BraceOwnLine, b.BraceOwnLine, v => Words(v, L.T("StyleProfile.OwnLine"), L.T("StyleProfile.SameLine")));
        Add("else", L.T("StyleProfile.OwnLine2"), a.ElseOwnLine, b.ElseOwnLine, v => Words(v, L.T("StyleProfile.OwnLine"), L.T("StyleProfile.AfterBrace")));
        Add(L.T("StyleProfile.KeywordSpacing"), L.T("StyleProfile.IfVsIf"), a.SpaceAfterKeyword, b.SpaceAfterKeyword, v => Words(v, L.T("StyleProfile.IfX"), "if(x)")); // loc-ignore: code
        Add(L.T("StyleProfile.OperatorSpacing"), L.T("StyleProfile.BVsB"), a.SpaceAroundOperators, b.SpaceAroundOperators, v => Words(v, L.T("StyleProfile.Spaced"), L.T("StyleProfile.Tight")));
        Add(L.T("StyleProfile.CommaSpacing"), L.T("StyleProfile.BVsB2"), a.SpaceAfterComma, b.SpaceAfterComma, v => Words(v, L.T("StyleProfile.Spaced"), L.T("StyleProfile.Tight")));
        Add(L.T("StyleProfile.Comments"), L.T("StyleProfile.CommentLinesPerLine"), a.CommentDensity, b.CommentDensity, Pct, 0.5);
        Add(L.T("StyleProfile.BlockComments"), L.T("StyleProfile.BlockOverLine"), a.BlockComments, b.BlockComments, Pct);
        Add(L.T("StyleProfile.BlankLines"), L.T("StyleProfile.ShareFile"), a.BlankLines, b.BlankLines, Pct, 0.3);
        Add(L.T("StyleProfile.LineLength"), L.T("StyleProfile.AverageCharacters"), a.AverageLineLength, b.AverageLineLength, v => $"{v:0}", 40);
        Add(L.T("StyleProfile.CamelCaseNames"), L.T("StyleProfile.LocalVariables"), a.CamelLocals, b.CamelLocals, Pct);
        Add(L.T("StyleProfile.PascalCaseNames"), L.T("StyleProfile.LocalVariables"), a.PascalLocals, b.PascalLocals, Pct);
        Add(L.T("StyleProfile.SnakeCaseNames"), L.T("StyleProfile.LocalVariables"), a.SnakeLocals, b.SnakeLocals, Pct);
        Add(L.T("StyleProfile.NamePrefixes"), L.T("StyleProfile.LikeB"), a.PrefixedLocals, b.PrefixedLocals, Pct);
        Add(L.T("StyleProfile.Explicit"), L.T("StyleProfile.UsedMembers"), a.ExplicitThis, b.ExplicitThis, Pct);
        Add(L.T("StyleProfile.FunctionLength"), L.T("StyleProfile.AverageLines"), a.AverageFunctionLength, b.AverageFunctionLength, v => $"{v:0}", 40);
        Add(L.T("StyleProfile.LineEndings"), L.T("StyleProfile.CrlfLF"), a.CrlfLines, b.CrlfLines, v => Words(v, "CRLF", "LF"));
        return features;
    }

    /// <summary>The habits' average likeness, 0 to 1. Habits both sources share with most coders pull it up, so it's weak on its own.</summary>
    public static double Similarity(IReadOnlyList<StyleFeature> features) => features.Count == 0 ? 0 : features.Average(f => f.Similarity);
}
