using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>How the check tells text for people from code: shared with the tools that move text into the tables.</summary>
public static class Detection
{
    /// <summary>A string literal's or interpolated string's text, holes as {0}; null for anything else.</summary>
    public static string? TextOf(SyntaxNode node) => node switch
    {
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) && l.Parent is not InterpolationSyntax => l.Token.ValueText,
        InterpolatedStringExpressionSyntax i => string.Concat(i.Contents.Select(c => c is InterpolatedStringTextSyntax t ? t.TextToken.ValueText : "{0}")),
        _ => null,
    };

    public static List<string> PlaceholderNames(string text) =>
        [.. Regex.Matches(text, @"(?<!\{)\{([A-Za-z_][A-Za-z0-9_]*)(?:,-?\d+)?(?::[^{}]*)?\}(?!\})").Select(m => m.Groups[1].Value).Distinct()];

    /// <summary>Lines with a loc-ignore comment, and those between loc-ignore-start and loc-ignore-end.</summary>
    public static HashSet<int> IgnoredLines(string source)
    {
        var set = new HashSet<int>();
        var lines = source.Replace("\r", "").Split('\n');
        bool inside = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("loc-ignore-start", StringComparison.Ordinal)) inside = true;
            if (inside || lines[i].Contains("loc-ignore", StringComparison.Ordinal)) set.Add(i + 1);
            if (lines[i].Contains("loc-ignore-end", StringComparison.Ordinal)) inside = false;
        }
        return set;
    }

    /// <summary>The key when the literal is the first argument of L.T(…) or a Bind(…, key).</summary>
    public static string? KeyArgument(SyntaxNode node)
    {
        if (node is not LiteralExpressionSyntax literal || literal.Parent is not ArgumentSyntax argument
            || argument.Parent is not ArgumentListSyntax list || list.Parent is not InvocationExpressionSyntax call) return null;
        string callee = call.Expression.ToString();
        if ((callee is "L.T" or "Localisation.L.T") && list.Arguments.IndexOf(argument) == 0) return literal.Token.ValueText;
        if (callee.EndsWith(".Bind", StringComparison.Ordinal) && list.Arguments.IndexOf(argument) == 1) return literal.Token.ValueText;
        return null;
    }

    /// <summary>Where a string is code rather than text: an attribute, a comparison, a case, a resource key, a path…</summary>
    public static bool NotShown(SyntaxNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            switch (parent)
            {
                case AttributeSyntax:
                case CaseSwitchLabelSyntax or ConstantPatternSyntax:
                    return true;
                case SwitchExpressionArmSyntax arm when arm.Pattern.Span.Contains(node.Span):
                    return true;
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression):
                    return true;
                case ElementAccessExpressionSyntax e when e.ArgumentList.Span.Contains(node.Span):
                    return true;
                case InvocationExpressionSyntax call:
                {
                    string callee = call.Expression.ToString();
                    string name = callee[(callee.LastIndexOf('.') + 1)..];
                    if (Lists.NotShownCalls.Contains(name) || Lists.NotShownPrefixes.Any(p => callee.StartsWith(p, StringComparison.Ordinal))) return true;
                    return false; // the call's result may well be shown: stop looking
                }
                case ObjectCreationExpressionSyntax create when create.Type.ToString() is "Regex" or "Uri" or "XElement" or "XAttribute" or "XName" or "ProcessStartInfo" or "PropertyPath" or "FontFamily":
                    return true;
                case StatementSyntax or MemberDeclarationSyntax:
                    return false;
            }
        }
        return false;
    }

    /// <summary>Whether a string is put somewhere it's read: a Text, a title, a message.</summary>
    public static bool UiContext(SyntaxNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            switch (parent)
            {
                case AssignmentExpressionSyntax a:
                    string target = a.Left.ToString();
                    return Lists.UiNames.Any(n => target.EndsWith(n, StringComparison.Ordinal));
                case ArgumentSyntax { NameColon: { } colon } when Lists.UiNames.Any(n => colon.Name.Identifier.Text.Equals(n, StringComparison.OrdinalIgnoreCase)):
                    return true;
                case InvocationExpressionSyntax call:
                    string callee = call.Expression.ToString();
                    return Lists.UiCalls.Any(c => callee.EndsWith(c, StringComparison.Ordinal));
                case StatementSyntax or MemberDeclarationSyntax:
                    return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a string reads as text for people: words with spaces between them, or in a place text goes, a word. Not a
    /// file name, a path, a key or an identifier, an icon glyph or a format with no words.
    /// </summary>
    public static bool IsText(string text, bool uiContext)
    {
        string t = text.Trim();
        if (Lists.Names.Contains(t)) return false;
        if (t.Length < 2 || !t.Any(char.IsLetter)) return false;
        if (t.All(c => c is >= '\uE000' and <= '\uF8FF' || char.IsWhiteSpace(c) || char.IsPunctuation(c))) return false;
        if (Regex.IsMatch(t, @"^[\w\-.]+\.[A-Za-z0-9]{1,8}$")) return false; // a file name
        if (Regex.IsMatch(t, @"^(\*?\.\w+;?)+$")) return false; // a file pattern
        if (t.Contains("://", StringComparison.Ordinal)) return t.Contains(' ') && Regex.IsMatch(t, @"[A-Za-z]{2,} [A-Za-z]{2,} [A-Za-z]"); // a link, unless in a sentence
        if (Regex.IsMatch(t, @"^[A-Za-z]:\\|^\\\\|^%\w+%")) return false; // a path
        bool words = Regex.IsMatch(t, @"\p{L}[\p{L}'’]+[,.:;!?)]*\s+[(\p{L}]|\p{L}\s+\(?\p{L}{2,}|\p{L}{3,}:?\s+\{0\}|\{0\}\s+\p{L}{3,}");
        if (words) return true;
        // one word: only where text goes, and not an identifier-looking key (MyKey, my_key, my-key) or a code
        if (!uiContext) return false;
        return !Regex.IsMatch(t, @"^[a-z]+[A-Z_]\w*$|^[A-Z][a-z]+[A-Z]\w*$|^[\w]+[_\-][\w\-]+$|^\{0\}$");
    }

    /// <summary>Lists the detection uses.</summary>
    public static class Lists
    {
        /// <summary>Calls whose string arguments are names, keys, paths or patterns, not text.</summary>
        public static readonly HashSet<string> NotShownCalls = new(StringComparer.Ordinal)
        {
            "Combine", "GetFullPath", "GetFileName", "GetExtension", "ChangeExtension", "Exists", "ReadAllText", "ReadAllBytes", "WriteAllText",
            "EnumerateFiles", "GetFiles", "EnumerateDirectories", "GetDirectories", "CreateDirectory", "Delete", "Move", "Copy",
            "StartsWith", "EndsWith", "Contains", "IndexOf", "LastIndexOf", "Replace", "Split", "Trim", "TrimStart", "TrimEnd", "Equals", "Compare",
            "IsMatch", "Match", "Matches", "Escape",
            "GetManifestResourceStream", "FindResource", "TryFindResource", "SetResourceReference", "GetType", "GetMethod", "GetField",
            "GetProperty", "GetValue", "GetString", "GetProperty", "TryGetProperty", "GetValueOrDefault", "TryGetValue", "ContainsKey", "Remove",
            "Element", "Elements", "Attribute", "Attributes", "Descendants", "Parse", "TryParse", "ParseExact", "TryParseExact", "ToString",
            "WriteLine", "Write", "Assert", "Fail", "Start", "GetEnvironmentVariable", "OpenSubKey", "CreateSubKey", "SetValue", "DeleteSubKey",
            "nameof", "Navigate", "Find", "FirstOrDefault", "Lookup", "Resolve", "Open", "Register", "SetBinding", "Binding",
            "PadLeft", "PadRight",
        };

        public static readonly string[] NotShownPrefixes = ["Debug.", "Trace.", "Console.", "Regex.", "Path.", "File.", "Directory.", "Log."];

        /// <summary>Property and argument names text is given to.</summary>
        public static readonly string[] UiNames =
            ["Text", "Content", "Header", "ToolTip", "Title", "Caption", "Message", "message", "title", "Summary", "Hint", "Label", "Note", "Status", "Description", "primary", "secondary", "Filter"];

        /// <summary>Names that are the same in every language: the app's, the game's.</summary>
        public static readonly HashSet<string> Names = new(StringComparer.Ordinal) { "W3 Mod Workbench", "The Witcher 3" };

        /// <summary>Calls whose arguments are shown.</summary>
        public static readonly string[] UiCalls = ["ShowMessage", "Show", "SetStatus", "Status", "Confirm", "Ask", "AddItem", "MenuItem", "Item", "Note", "Hint"];
    }
}
