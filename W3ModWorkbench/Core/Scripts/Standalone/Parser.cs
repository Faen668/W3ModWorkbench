using W3ModWorkbench.Localisation;
// Part of the built-in compiler: a C# port of the .ws-to-rsblob compiler written by WebSpam for Aelto's tw3-cahirb (MIT).
// https://github.com/Aelto/tw3-cahirb/pull/1

namespace W3ModWorkbench.Core.Scripts.Standalone;

/// <summary>
/// Reads WitcherScript into <see cref="Item"/>s. With <c>skipBodies</c> the function bodies and default
/// values aren't read, which is all a game script file is needed for: its declarations.
/// </summary>
public sealed class Parser
{
    private static readonly string[] AssignOps = ["=", "+=", "-=", "*=", "/=", "&=", "|="];

    private static readonly string[][] BinaryLevels =
    [
        ["||"], ["&&"], ["|"], ["^"], ["&"], ["==", "!="], ["<", "<=", ">", ">="], ["+", "-"], ["*", "/", "%"],
    ];

    private readonly List<Token> _tokens;
    private readonly bool _skipBodies;
    private int _pos;

    private Parser(List<Token> tokens, bool skipBodies)
    {
        _tokens = tokens;
        _skipBodies = skipBodies;
    }

    public static List<Item> ParseSource(string source, bool skipBodies) =>
        new Parser(Lexer.Tokenize(source, lenient: skipBodies), skipBodies).Items();

    private Token Cur => _tokens[_pos];
    private Token PeekAt(int offset) => _tokens[Math.Min(_pos + offset, _tokens.Count - 1)];
    private int PrevLine => _tokens[Math.Max(_pos - 1, 0)].Line;

    private Token Advance()
    {
        var token = _tokens[_pos];
        if (_pos < _tokens.Count - 1) _pos++;
        return token;
    }

    private ScriptSyntaxException Error(string message) =>
        new($"{message}, found '{(Cur.Kind == TokKind.Eof ? L.T("Parser.EndFile") : Cur.Text)}'", Cur.Line);

    private bool IsPunct(string p) => Cur.Kind == TokKind.Punct && Cur.Text == p;
    private bool IsKeyword(string k) => Cur.Kind == TokKind.Ident && Cur.Text == k;

    private bool EatPunct(string p)
    {
        if (!IsPunct(p)) return false;
        Advance();
        return true;
    }

    private bool EatKeyword(string k)
    {
        if (!IsKeyword(k)) return false;
        Advance();
        return true;
    }

    private void ExpectPunct(string p)
    {
        if (!EatPunct(p)) throw Error($"expected '{p}'");
    }

    private string Identifier()
    {
        if (Cur.Kind != TokKind.Ident) throw Error(L.T("Parser.ExpectedName"));
        return Advance().Text;
    }

    private List<Item> Items()
    {
        var items = new List<Item>();
        while (Cur.Kind != TokKind.Eof)
        {
            if (EatPunct(";")) continue;
            items.Add(ParseItem());
        }
        return items;
    }

    private Annotation? ParseAnnotation()
    {
        int line = Cur.Line;
        if (!EatPunct("@")) return null;
        var kind = Identifier() switch
        {
            "addField" => AnnotationKind.AddField,
            "addMethod" => AnnotationKind.AddMethod,
            "wrapMethod" => AnnotationKind.WrapMethod,
            "replaceMethod" => AnnotationKind.ReplaceMethod,
            var other => throw Error(L.T("Parser.UnknownAnnotation", other)),
        };
        string? cls = null;
        if (EatPunct("("))
        {
            if (!IsPunct(")")) cls = Identifier(); // `@wrapMethod()` is how a global function is wrapped
            ExpectPunct(")");
        }
        return new Annotation(kind, cls, line);
    }

    private Item ParseItem()
    {
        var annotation = ParseAnnotation();
        // what replaces or wraps a function takes the function's own modifiers, so none may be written; only checked in
        // code being compiled, as the declarations read from other mods' blobs are written with them
        if (!_skipBodies && annotation?.Kind is AnnotationKind.ReplaceMethod or AnnotationKind.WrapMethod
            && !(Cur.Kind == TokKind.Ident && Cur.Text == "function"))
            throw new ScriptSyntaxException(
                $"syntax error, unexpected {(Cur.Kind == TokKind.Ident ? "TOKEN_" + Cur.Text.ToUpperInvariant() : $"'{Cur.Text}'")}, expecting TOKEN_FUNCTION, near '{Cur.Text}'", Cur.Line); // loc-ignore
        var cls = new ClassDecl();
        var fn = new FunctionSpecifiers();
        var field = new FieldSpecifiers();
        while (true)
        {
            if (Cur.Kind != TokKind.Ident) throw Error(L.T("Parser.ExpectedDeclaration"));
            string word = Cur.Text;
            switch (word)
            {
                case "import":
                    cls.IsImport = fn.IsImport = field.IsImport = true;
                    break;
                case "abstract": cls.IsAbstract = true; break;
                case "statemachine": cls.IsStateMachine = true; break;
                case "class":
                    Advance();
                    return new ClassItem(ClassBody(cls, Identifier()));
                case "struct":
                    Advance();
                    cls.IsStruct = true;
                    return new ClassItem(ClassBody(cls, Identifier()));
                case "state":
                    Advance();
                    return new ClassItem(State(cls));
                case "enum":
                    Advance();
                    return new EnumItem(ParseEnum());
                case "var":
                    var f = ParseField(field);
                    f.Annotation = annotation;
                    return new FieldItem(f);
                default:
                    if (ApplySpecifier(word, fn, field)) break;
                    if (word is "function" or "event")
                    {
                        var d = ParseFunction(fn);
                        d.Annotation = annotation;
                        return new FunctionItem(d);
                    }
                    throw Error(L.T("Parser.ExpectedDeclaration"));
            }
            Advance();
        }
    }

    private ClassDecl State(ClassDecl cls)
    {
        string name = Identifier();
        if (!EatKeyword("in")) throw Error("expected 'in'");
        cls.StateMachineOf = Identifier();
        return ClassBody(cls, name);
    }

    private ClassDecl ClassBody(ClassDecl cls, string name)
    {
        cls.Name = name;
        if (EatKeyword("extends")) cls.Parent = Identifier();
        ExpectPunct("{");
        while (!EatPunct("}"))
        {
            if (Cur.Kind == TokKind.Eof) throw Error("expected '}'");
            if (EatPunct(";")) continue;
            Member(cls);
        }
        return cls;
    }

    private void Member(ClassDecl cls)
    {
        var fn = new FunctionSpecifiers();
        var field = new FieldSpecifiers();
        while (true)
        {
            if (Cur.Kind != TokKind.Ident) throw Error(L.T("Parser.ExpectedMember"));
            string word = Cur.Text;
            switch (word)
            {
                case "import":
                    fn.IsImport = field.IsImport = true;
                    break;
                case "var":
                    cls.Fields.Add(ParseField(field));
                    return;
                case "autobind":
                {
                    Advance();
                    string name = Identifier();
                    ExpectPunct(":");
                    var type = ParseTypeRef();
                    string? binding = null;
                    if (EatPunct("="))
                    {
                        var t = Advance();
                        binding = t.Kind is TokKind.Str or TokKind.Name or TokKind.Ident ? t.Text : null;
                    }
                    ExpectPunct(";");
                    cls.Fields.Add(new FieldDecl { Names = [name], Type = type, Specifiers = field, IsBinding = true, Binding = binding });
                    return;
                }
                case "default":
                {
                    Advance();
                    string name = Identifier();
                    ExpectPunct("=");
                    if (_skipBodies)
                        SkipUntil(";");
                    else
                    {
                        var value = ParseExpr();
                        ExpectPunct(";");
                        cls.Defaults.Add(new DefaultAssignment(name, value));
                    }
                    return;
                }
                case "hint":
                    Advance();
                    SkipUntil(";");
                    return;
                case "defaults":
                    Advance();
                    SkipBraces();
                    return;
                default:
                    if (ApplySpecifier(word, fn, field)) break;
                    if (word is "function" or "event")
                    {
                        cls.Methods.Add(ParseFunction(fn));
                        return;
                    }
                    throw Error(L.T("Parser.ExpectedMember"));
            }
            Advance();
        }
    }

    private void SkipUntil(string punct)
    {
        int depth = 0;
        while (true)
        {
            if (Cur.Kind == TokKind.Eof) throw Error($"expected '{punct}'");
            if (Cur.Kind == TokKind.Punct)
            {
                switch (Cur.Text)
                {
                    case "(" or "{" or "[": depth++; break;
                    case ")" or "}" or "]": depth--; break;
                    default:
                        if (Cur.Text == punct && depth == 0)
                        {
                            Advance();
                            return;
                        }
                        break;
                }
            }
            Advance();
        }
    }

    private void SkipBraces()
    {
        ExpectPunct("{");
        int depth = 1;
        while (depth > 0)
        {
            var t = Advance();
            if (t.Kind == TokKind.Eof) throw Error(L.T("Parser.UnbalancedBraces"));
            if (t.Kind == TokKind.Punct)
            {
                if (t.Text == "{") depth++;
                else if (t.Text == "}") depth--;
            }
        }
    }

    private EnumDecl ParseEnum()
    {
        string name = Identifier();
        ExpectPunct("{");
        var members = new List<(string, long?)>();
        var lines = new List<int>();
        while (!EatPunct("}"))
        {
            lines.Add(Cur.Line);
            string member = Identifier();
            long? value = null;
            if (EatPunct("="))
            {
                bool negative = EatPunct("-");
                var t = Advance();
                if (t.Kind != TokKind.Int) throw Error(L.T("Parser.ExpectedEnumValue"));
                value = negative ? -t.Int : t.Int;
            }
            members.Add((member, value));
            if (!EatPunct(","))
            {
                ExpectPunct("}");
                break;
            }
        }
        return new EnumDecl { Name = name, Members = members, Lines = lines };
    }

    private FieldDecl ParseField(FieldSpecifiers specifiers)
    {
        if (!EatKeyword("var")) throw Error("expected 'var'");
        int line = Cur.Line;
        var names = new List<string> { Identifier() };
        while (EatPunct(",")) names.Add(Identifier());
        ExpectPunct(":");
        var type = ParseTypeRef();
        Expr? init = null;
        if (EatPunct("=")) init = ParseExpr();
        ExpectPunct(";");
        return new FieldDecl { Names = names, Type = type, Specifiers = specifiers, Initializer = init, Line = line };
    }

    private TypeRef ParseTypeRef()
    {
        string name = Identifier();
        var args = new List<TypeRef>();
        if (EatPunct("<"))
        {
            args.Add(ParseTypeRef());
            while (EatPunct(",")) args.Add(ParseTypeRef());
            // ">>" can't happen: the lexer never joins two of them
            ExpectPunct(">");
        }
        return new TypeRef(name, args);
    }

    private FunctionDecl ParseFunction(FunctionSpecifiers specs)
    {
        specs = specs.Copy();
        if (EatKeyword("event")) specs.IsEvent = true;
        else if (!EatKeyword("function")) throw Error("expected 'function'");

        int line = Cur.Line;
        string name = Identifier();
        ExpectPunct("(");
        var parms = new List<Param>();
        while (!EatPunct(")"))
        {
            bool isOut = false, isOptional = false;
            while (true)
            {
                if (EatKeyword("out")) isOut = true;
                else if (EatKeyword("optional")) isOptional = true;
                else break;
            }
            var names = new List<string> { Identifier() };
            while (EatPunct(",")) names.Add(Identifier());
            ExpectPunct(":");
            var type = ParseTypeRef();
            parms.AddRange(names.Select(n => new Param(n, type, isOut, isOptional)));
            if (!EatPunct(","))
            {
                ExpectPunct(")");
                break;
            }
        }

        TypeRef? returnType = EatPunct(":") ? ParseTypeRef() : null;

        List<Stmt>? body;
        bool usesWrapped = false;
        if (IsPunct("{"))
        {
            int start = _pos;
            if (_skipBodies)
            {
                SkipBraces();
                body = [];
            }
            else
                body = ParseBlock();
            for (int i = start; i < _pos && !usesWrapped; i++)
                usesWrapped = _tokens[i].Kind == TokKind.Ident && _tokens[i].Text == "wrappedMethod";
        }
        else
        {
            ExpectPunct(";");
            body = null;
        }
        return new FunctionDecl { Name = name, Specifiers = specs, Params = parms, ReturnType = returnType, Body = body, Line = line, UsesWrappedMethod = usesWrapped };
    }

    private List<Stmt> ParseBlock()
    {
        ExpectPunct("{");
        var list = new List<Stmt>();
        while (!EatPunct("}"))
        {
            if (Cur.Kind == TokKind.Eof) throw Error("expected '}'");
            list.Add(ParseStatement());
        }
        return list;
    }

    private Stmt ParseStatement()
    {
        int startLine = Cur.Line;
        StmtKind kind;

        if (IsPunct("{"))
            kind = new BlockStmt(ParseBlock());
        else if (IsPunct(";"))
        {
            Advance();
            kind = new EmptyStmt();
        }
        else if (Cur.Kind == TokKind.Ident)
        {
            switch (Cur.Text)
            {
                case "var":
                    kind = new VarStmt(ParseField(new FieldSpecifiers()));
                    break;
                case "if":
                {
                    Advance();
                    var cond = ParenExpr();
                    var then = ParseStatement();
                    Stmt? otherwise = EatKeyword("else") ? ParseStatement() : null;
                    return new Stmt(new IfStmt(cond, then, otherwise), startLine);
                }
                case "while":
                {
                    Advance();
                    var cond = ParenExpr();
                    int line = PrevLine;
                    return new Stmt(new WhileStmt(cond, ParseStatement()), line);
                }
                case "do":
                {
                    Advance();
                    var body = ParseStatement();
                    if (!EatKeyword("while")) throw Error("expected 'while'");
                    var cond = ParenExpr();
                    int line = PrevLine;
                    EatPunct(";");
                    return new Stmt(new DoWhileStmt(body, cond), line);
                }
                case "for":
                {
                    Advance();
                    ExpectPunct("(");
                    var init = OptionalExpr(";");
                    ExpectPunct(";");
                    var cond = OptionalExpr(";");
                    ExpectPunct(";");
                    var step = OptionalExpr(")");
                    ExpectPunct(")");
                    int line = PrevLine;
                    return new Stmt(new ForStmt(init, cond, step, ParseStatement()), line);
                }
                case "switch":
                {
                    Advance();
                    var value = ParenExpr();
                    int line = PrevLine;
                    return new Stmt(new SwitchStmt(value, SwitchItems()), line);
                }
                case "break":
                    Advance();
                    ExpectPunct(";");
                    kind = new BreakStmt();
                    break;
                case "continue":
                    Advance();
                    ExpectPunct(";");
                    kind = new ContinueStmt();
                    break;
                case "return":
                {
                    Advance();
                    var value = OptionalExpr(";");
                    ExpectPunct(";");
                    kind = new ReturnStmt(value);
                    break;
                }
                case "delete":
                {
                    Advance();
                    var value = ParseExpr();
                    ExpectPunct(";");
                    kind = new DeleteStmt(value);
                    break;
                }
                default:
                    kind = ExprStatement();
                    break;
            }
        }
        else
            kind = ExprStatement();

        int stmtLine = kind is BlockStmt ? startLine : PrevLine;
        return new Stmt(kind, stmtLine);
    }

    private StmtKind ExprStatement()
    {
        var expr = ParseExpr();
        ExpectPunct(";");
        return new ExprStmt(expr);
    }

    private List<SwitchItem> SwitchItems()
    {
        ExpectPunct("{");
        var items = new List<SwitchItem>();
        while (!EatPunct("}"))
        {
            if (Cur.Kind == TokKind.Eof) throw Error("expected '}'");
            if (EatKeyword("case"))
            {
                var value = ParseExpr();
                ExpectPunct(":");
                items.Add(new CaseItem(value));
            }
            else if (IsKeyword("default") && PeekAt(1) is { Kind: TokKind.Punct, Text: ":" })
            {
                Advance();
                Advance();
                items.Add(new DefaultItem());
            }
            else
                items.Add(new StmtItem(ParseStatement()));
        }
        return items;
    }

    private Expr ParenExpr()
    {
        ExpectPunct("(");
        var e = ParseExpr();
        ExpectPunct(")");
        return e;
    }

    private Expr? OptionalExpr(string terminator) => IsPunct(terminator) ? null : ParseExpr();

    public Expr ParseExpr()
    {
        var target = Ternary();
        string? op = AssignOps.FirstOrDefault(IsPunct);
        if (op is null) return target;
        Advance();
        var value = ParseExpr();
        return new Expr(new Assign(op, target, value), Cur.Line);
    }

    private Expr Ternary()
    {
        var cond = BinaryExpr(0);
        if (!EatPunct("?")) return cond;
        var then = ParseExpr();
        ExpectPunct(":");
        var otherwise = Ternary();
        return new Expr(new Ternary(cond, then, otherwise), Cur.Line);
    }

    private Expr BinaryExpr(int level)
    {
        if (level == BinaryLevels.Length) return UnaryExpr();
        var left = BinaryExpr(level + 1);
        while (true)
        {
            string? op = BinaryLevels[level].FirstOrDefault(IsPunct);
            if (op is null) return left;
            Advance();
            var right = BinaryExpr(level + 1);
            left = new Expr(new Binary(op, left, right), Cur.Line);
        }
    }

    private Expr UnaryExpr()
    {
        // a unary + changes nothing (so ++x reads as +(+x), as wcc_lite reads it)
        if (EatPunct("+")) return UnaryExpr();
        foreach (string op in new[] { "!", "-", "~" })
        {
            if (EatPunct(op))
            {
                var operand = UnaryExpr();
                return new Expr(new Unary(op, operand), Cur.Line);
            }
        }

        if (IsCast())
        {
            Advance();
            var type = ParseTypeRef();
            ExpectPunct(")");
            var operand = UnaryExpr();
            return new Expr(new Cast(type, operand), Cur.Line);
        }
        return Postfix();
    }

    private bool IsCast()
    {
        if (!IsPunct("(")) return false;
        if (PeekAt(1) is not { Kind: TokKind.Ident } name) return false;
        if (name.Text is "this" or "super" or "parent" or "NULL" or "true" or "false") return false;
        if (PeekAt(2) is not { Kind: TokKind.Punct, Text: ")" }) return false;
        var after = PeekAt(3);
        return after.Kind switch
        {
            TokKind.Ident or TokKind.Int or TokKind.Float or TokKind.Str or TokKind.Name => true,
            TokKind.Punct => after.Text == "(",
            _ => false,
        };
    }

    private Expr Postfix()
    {
        var expr = Primary();
        while (true)
        {
            if (EatPunct("."))
            {
                string member = Identifier();
                expr = new Expr(new Member(expr, member), PrevLine);
            }
            else if (EatPunct("("))
            {
                var args = Arguments();
                expr = new Expr(new Call(expr, args), PrevLine);
            }
            else if (EatPunct("["))
            {
                var idx = ParseExpr();
                ExpectPunct("]");
                expr = new Expr(new Index(expr, idx), Cur.Line);
            }
            else
                return expr;
        }
    }

    private List<Expr?> Arguments()
    {
        var args = new List<Expr?>();
        if (EatPunct(")")) return args;
        while (true)
        {
            args.Add(IsPunct(",") || IsPunct(")") ? null : ParseExpr());
            if (EatPunct(")")) return args;
            ExpectPunct(",");
            if (EatPunct(")")) return args; // `f(a, b, )` has two arguments
        }
    }

    private Expr Primary()
    {
        var token = Advance();
        int line = token.Line;
        ExprKind kind;
        switch (token.Kind)
        {
            case TokKind.Int: kind = new IntLit(token.Int); break;
            case TokKind.Float: kind = new FloatLit(token.Float, token.Text); break;
            case TokKind.Str: kind = new StrLit(token.Text); break;
            case TokKind.Name: kind = new NameLit(token.Text); break;
            case TokKind.Punct when token.Text == "(":
            {
                var inner = ParseExpr();
                ExpectPunct(")");
                return inner;
            }
            case TokKind.Ident:
                switch (token.Text)
                {
                    case "true": kind = new BoolLit(true); break;
                    case "false": kind = new BoolLit(false); break;
                    case "NULL": kind = new NullLit(); break;
                    case "this": kind = new ThisExpr(); break;
                    case "super": kind = new SuperExpr(); break;
                    case "parent": kind = new ParentExpr(); break;
                    case "virtual_parent": kind = new VirtualParentExpr(); break;
                    case "new":
                    {
                        var type = ParseTypeRef();
                        Expr? owner = EatKeyword("in") ? Postfix() : null;
                        kind = new New(type, owner);
                        break;
                    }
                    default: kind = new Ident(token.Text); break;
                }
                break;
            default:
                _pos--;
                throw Error(L.T("Parser.ExpectedExpression"));
        }
        return new Expr(kind, line);
    }

    private static bool ApplySpecifier(string word, FunctionSpecifiers fn, FieldSpecifiers field)
    {
        Visibility? v = word switch
        {
            "private" => Visibility.Private,
            "protected" => Visibility.Protected,
            "public" => Visibility.Public,
            _ => null,
        };
        if (v is { } visibility)
        {
            fn.Visibility = field.Visibility = visibility;
            return true;
        }
        switch (word)
        {
            case "final": fn.IsFinal = true; break;
            case "latent": fn.IsLatent = true; break;
            case "entry": fn.IsEntry = true; break;
            case "timer": fn.IsTimer = true; break;
            case "exec": fn.IsExec = true; break;
            case "quest": fn.IsQuest = true; break;
            case "storyscene": fn.IsStoryScene = true; break;
            case "reward": fn.IsReward = true; break;
            case "cleanup": fn.IsCleanup = true; break;
            case "native": break;
            case "optional": field.IsOptional = true; break;
            case "editable": field.IsEditable = true; break;
            case "saved": field.IsSaved = true; break;
            case "const": field.IsConst = true; break;
            case "inlined": field.IsInlined = true; break;
            default: return false;
        }
        return true;
    }
}
