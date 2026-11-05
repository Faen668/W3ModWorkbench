namespace W3ModWorkbench.Core.Scripts.Standalone;

// loc-ignore-start: wcc_lite's own words and the language's own tokens, which are never translated

/// <summary>What wcc_lite compiles of a function, kept apart from the file it's in: the code, and each local's initialiser.</summary>
public sealed class WccFunctionCode
{
    public required List<WccToken> Body { get; init; }
    public required List<List<WccToken>> Initializers { get; init; }
}

/// <summary>
/// WitcherScript's syntax as wcc_lite has it, with its messages and line numbers: every file against the file grammar,
/// then, when the functions are compiled, each function against the code grammar. The grammars are written out here
/// from how wcc_lite parses; token names show in its messages, so they're the same as its own.
/// </summary>
public static class WccSyntax
{
    /// <summary>The stack each of wcc_lite's two parsers has. Going past it is its "memory exhausted".</summary>
    private const int FileStack = 200, CodeStack = 400;

    private const string FileTokens = """
        TOKEN_IDENT TOKEN_INTEGER TOKEN_FLOAT TOKEN_NAME TOKEN_STRING TOKEN_BOOL_TRUE TOKEN_BOOL_FALSE TOKEN_NULL
        TOKEN_IMPORT TOKEN_ABSTRACT TOKEN_CONST TOKEN_EXTENDS TOKEN_CLASS TOKEN_STATE TOKEN_STATEMACHINE TOKEN_ENUM
        TOKEN_STRUCT TOKEN_FUNCTION TOKEN_DEF TOKEN_EDITABLE TOKEN_FINAL TOKEN_OUT TOKEN_OPTIONAL TOKEN_LOCAL
        TOKEN_INLINED TOKEN_ARRAY TOKEN_MAP TOKEN_SET TOKEN_DEFAULTS TOKEN_DEFAULT TOKEN_HINT TOKEN_ENTRY TOKEN_ENTER
        TOKEN_LATENT TOKEN_VAR TOKEN_AUTOBIND TOKEN_ANY TOKEN_SINGLE TOKEN_EXEC TOKEN_TIMER TOKEN_CLEANUP TOKEN_SCENE
        TOKEN_SAVED TOKEN_QUEST TOKEN_REWARD TOKEN_EXT_REPLACE TOKEN_EXT_ADD TOKEN_EXT_WRAP TOKEN_EXT_ADD_FIELD
        TOKEN_TYPE_BYTE TOKEN_TYPE_BOOL TOKEN_TYPE_INT TOKEN_TYPE_FLOAT TOKEN_TYPE_STRING TOKEN_TYPE_NAME
        TOKEN_TYPE_VOID TOKEN_CLASS_TYPE TOKEN_ENUM_TYPE TOKEN_OP_IADD TOKEN_OP_ISUB TOKEN_OP_IMUL TOKEN_OP_IDIV
        TOKEN_OP_IAND TOKEN_OP_IOR TOKEN_OP_LOGIC_OR TOKEN_OP_LOGIC_AND TOKEN_OP_EQUAL TOKEN_OP_NOTEQUAL
        TOKEN_OP_GREQ TOKEN_OP_LEEQ TOKEN_NEW TOKEN_DELETE TOKEN_IN TOKEN_IF TOKEN_ELSE TOKEN_SWITCH TOKEN_CASE
        TOKEN_FOR TOKEN_WHILE TOKEN_DO TOKEN_RETURN TOKEN_BREAK TOKEN_CONTINUE TOKEN_THIS TOKEN_SUPER TOKEN_PARENT
        TOKEN_VIRTUAL_PARENT TOKEN_PRIVATE TOKEN_PROTECTED TOKEN_PUBLIC TOKEN_EVENT TOKEN_SAVEPOINT TOKEN_GET_GAME
        TOKEN_GET_PLAYER TOKEN_GET_CAMERA TOKEN_GET_SOUND TOKEN_GET_DEBUG TOKEN_GET_TIMER TOKEN_GET_INPUT
        TOKEN_GET_TELEMETRY '@' '(' ')' ':' '{' '}' '=' ',' '-' ';' '<' '>' '[' ']'
        """;

    private const string FileRules = """
        programFile: fileDeclarationList
        fileDeclarationList: fileDeclarationList fileDeclaration | %empty
        fileDeclaration: structDecl optionalSemicolon | enumDecl optionalSemicolon | classDecl optionalSemicolon | stateDecl optionalSemicolon | functionDecl optionalSemicolon | extaddfieldDecl optionalSemicolon
        importFlag: TOKEN_IMPORT | %empty
        accessFlag: TOKEN_PRIVATE | TOKEN_PROTECTED | TOKEN_PUBLIC | %empty
        classFlags: classFlag classFlags | %empty
        classFlag: TOKEN_ABSTRACT | TOKEN_STATEMACHINE | TOKEN_IMPORT
        functionFlagFinal: TOKEN_FINAL | %empty
        functionFlagLatent: TOKEN_LATENT | %empty
        functionFlagType: TOKEN_EXEC | TOKEN_TIMER | TOKEN_SCENE | TOKEN_QUEST | TOKEN_REWARD | TOKEN_CLEANUP | TOKEN_ENTRY | %empty
        varFlagConst: TOKEN_CONST | %empty
        varFlagEditable: TOKEN_EDITABLE | %empty
        varFlagInlined: TOKEN_INLINED | %empty
        varFlagSaved: TOKEN_SAVED | %empty
        extaddfieldDecl: extaddfieldHeader
        extaddfieldHeader: '@' TOKEN_EXT_ADD_FIELD '(' ident ')' varFlags TOKEN_VAR ident ':' fullType
        stateDecl: stateHeader '{' classElemList '}'
        stateHeader: classFlags TOKEN_STATE ident TOKEN_IN ident | classFlags TOKEN_STATE ident TOKEN_IN ident TOKEN_EXTENDS ident
        classDecl: classHeader '{' classElemList '}'
        classHeader: classFlags TOKEN_CLASS ident classExtends
        classElemList: classElemList classElement | %empty
        classElement: varDecl semicolon | autobindDecl semicolon | functionDecl | defaultsDecl | hintDecl
        classExtends: TOKEN_EXTENDS ident | %empty
        varCommonFlags: importFlag accessFlag
        varFlags: varCommonFlags varFlagConst varFlagEditable varFlagInlined varFlagSaved
        varDecl: varFlags TOKEN_VAR functionParamIdentList ':' fullType
        autobindDecl: autobindFlags TOKEN_AUTOBIND ident ':' ident '=' autobindBinding
        autobindFlags: varCommonFlags autobindOptionalFlag
        autobindOptionalFlag: TOKEN_OPTIONAL | %empty
        autobindBinding: name | string | TOKEN_ANY | TOKEN_SINGLE
        structDecl: structInfo '{' structElemList '}'
        structInfo: importFlag TOKEN_STRUCT ident
        structElemList: structElement structElemList | %empty
        structElement: varDecl semicolon | defaultsDecl | hintDecl
        enumDecl: TOKEN_ENUM enumInfo '{' enumNameList optionalComma '}'
        enumInfo: ident
        enumNameList: enumNameList ',' enumName | enumName
        enumName: ident | ident '=' integer | ident '=' '-' integer
        defaultsDecl: TOKEN_DEFAULTS '{' defaultsValList '}' | TOKEN_DEFAULT defaultVal
        defaultsValList: defaultVal defaultsValList | %empty
        defaultVal: ident '=' scriptUnnamedValue semicolon | ident '=' '-' integer semicolon | ident '=' '-' float semicolon
        hintDecl: TOKEN_HINT hintVal
        hintVal: ident '=' string semicolon
        scriptValue: scriptNamedValue | scriptUnnamedValue
        scriptNamedValue: ident '=' scriptUnnamedValue
        scriptUnnamedValue: basicValue | scriptCompoundValue | scriptObjectValue
        scriptCompoundValue: scriptCompoundValueHeader scriptCompoundValueListDef '}'
        scriptCompoundValueHeader: '{'
        scriptCompoundValueListDef: scriptCompoundValueList | %empty
        scriptCompoundValueList: scriptCompoundValueList ',' scriptValue | scriptValue
        scriptObjectValue: scriptObjectValueHeader scriptObjectValueListDef ')'
        scriptObjectValueHeader: '(' | ident '('
        scriptObjectValueListDef: scriptObjectValueList | %empty
        scriptObjectValueList: scriptObjectValueList ',' scriptNamedValue | scriptNamedValue
        functionDecl: functionHeader
        functionHeader: functionInnerHeader '(' functionParams ')' functionRetValue functionTail
        functionRetValue: ':' fullType | %empty
        functionInnerHeader: '@' TOKEN_EXT_ADD '(' ident ')' importFlag accessFlag functionFlagFinal functionFlagLatent functionFlagType TOKEN_FUNCTION ident | importFlag accessFlag functionFlagFinal functionFlagLatent functionFlagType TOKEN_FUNCTION ident | '@' TOKEN_EXT_ADD '(' ident ')' TOKEN_EVENT ident | TOKEN_EVENT ident | '@' TOKEN_EXT_REPLACE '(' ident ')' TOKEN_FUNCTION ident | '@' TOKEN_EXT_REPLACE TOKEN_FUNCTION ident | '@' TOKEN_EXT_WRAP '(' ident ')' TOKEN_FUNCTION ident | '@' TOKEN_EXT_WRAP TOKEN_FUNCTION ident
        functionParams: functionParamList | %empty
        functionParamList: functionParam ',' functionParamList | functionParam
        functionParam: functionParamFlagList functionParamIdentList ':' fullType
        functionParamIdentList: ident ',' functionParamIdentList | ident
        functionParamFlagList: functionParamFlag functionParamFlagList | %empty
        functionParamFlag: TOKEN_OUT | TOKEN_OPTIONAL
        functionTail: '{' functionVarsDecl functionBody '}' | semicolon
        functionBody: %empty
        functionVarsDecl: functionVarsDecl functionVarsHeader functionVarDecl ';' | functionVarsDecl functionVarsHeader functionVarSingleDecl '=' functionVarInitializer ';' | %empty
        functionVarsHeader: TOKEN_VAR
        functionVarDecl: functionVarSingleDecl ',' functionVarDecl | functionVarSingleDecl
        functionVarSingleDecl: functionParamIdentList ':' fullType
        functionVarInitializer: %empty
        fullType: basicType | arrayType | mapType | setType | staticArrayType | ident
        basicType: TOKEN_TYPE_STRING | TOKEN_TYPE_BYTE | TOKEN_TYPE_BOOL | TOKEN_TYPE_INT | TOKEN_TYPE_FLOAT | TOKEN_TYPE_NAME | TOKEN_TYPE_VOID
        arrayType: TOKEN_ARRAY '<' fullType '>'
        mapType: TOKEN_MAP '<' fullType ',' fullType '>'
        setType: TOKEN_SET '<' fullType '>'
        staticArrayType: fullType '[' integer ']'
        basicValue: integer | float | bool | string | name | ident | null
        integer: TOKEN_INTEGER
        float: TOKEN_FLOAT
        name: TOKEN_NAME
        string: TOKEN_STRING
        bool: TOKEN_BOOL_TRUE | TOKEN_BOOL_FALSE
        ident: TOKEN_IDENT
        null: TOKEN_NULL
        semicolon: ';'
        optionalSemicolon: ';' | %empty
        optionalComma: ',' | %empty
        """;

    private const string CodeTokens = """
        TOKEN_IDENT TOKEN_INTEGER TOKEN_FLOAT TOKEN_NAME TOKEN_STRING TOKEN_BOOL_TRUE TOKEN_BOOL_FALSE TOKEN_NULL
        TOKEN_IMPORT TOKEN_ABSTRACT TOKEN_CONST TOKEN_EXTENDS TOKEN_CLASS TOKEN_STATE TOKEN_STATEMACHINE TOKEN_ENUM
        TOKEN_STRUCT TOKEN_FUNCTION TOKEN_DEF TOKEN_EDITABLE TOKEN_FINAL TOKEN_OUT TOKEN_OPTIONAL TOKEN_LOCAL
        TOKEN_INLINED TOKEN_ARRAY TOKEN_MAP TOKEN_SET TOKEN_DEFAULTS TOKEN_DEFAULT TOKEN_HINT TOKEN_ENTRY TOKEN_ENTER
        TOKEN_LATENT TOKEN_VAR TOKEN_AUTOBIND TOKEN_ANY TOKEN_SINGLE TOKEN_EXEC TOKEN_TIMER TOKEN_CLEANUP TOKEN_SCENE
        TOKEN_SAVED TOKEN_QUEST TOKEN_REWARD TOKEN_EXT_REPLACE TOKEN_EXT_ADD TOKEN_EXT_WRAP TOKEN_EXT_ADD_FIELD
        TOKEN_TYPE_BYTE TOKEN_TYPE_BOOL TOKEN_TYPE_INT TOKEN_TYPE_FLOAT TOKEN_TYPE_STRING TOKEN_TYPE_NAME
        TOKEN_TYPE_VOID TOKEN_CLASS_TYPE TOKEN_ENUM_TYPE TOKEN_OP_IADD TOKEN_OP_ISUB TOKEN_OP_IMUL TOKEN_OP_IDIV
        TOKEN_OP_IAND TOKEN_OP_IOR TOKEN_OP_LOGIC_OR TOKEN_OP_LOGIC_AND TOKEN_OP_EQUAL TOKEN_OP_NOTEQUAL
        TOKEN_OP_GREQ TOKEN_OP_LEEQ TOKEN_NEW TOKEN_DELETE TOKEN_IN TOKEN_IF TOKEN_ELSE TOKEN_SWITCH TOKEN_CASE
        TOKEN_FOR TOKEN_WHILE TOKEN_DO TOKEN_RETURN TOKEN_BREAK TOKEN_CONTINUE TOKEN_THIS TOKEN_SUPER TOKEN_PARENT
        TOKEN_VIRTUAL_PARENT TOKEN_PRIVATE TOKEN_PROTECTED TOKEN_PUBLIC TOKEN_EVENT TOKEN_SAVEPOINT TOKEN_GET_GAME
        TOKEN_GET_PLAYER TOKEN_GET_CAMERA TOKEN_GET_SOUND TOKEN_GET_DEBUG TOKEN_GET_TIMER TOKEN_GET_INPUT
        TOKEN_GET_TELEMETRY ';' '{' '}' '(' ')' ':' ',' '=' '?' '|' '^' '&' '<' '>' '+' '-' '*' '/' '%' '!' '~' '['
        ']' '.'
        """;

    private const string CodeRules = """
        functionCode: statementList
        statementList: statement statementList | %empty
        statement: compoundStmt | emptyStmt | expressionStmt | jumpStmt | selectionStmt | loopStmt | deleteStmt | switchStmt | savepointStmt | labeledStmt | enterStmt | error ';'
        compoundStmt: '{' statementList '}'
        emptyStmt: ';'
        expressionStmt: expression ';'
        selectionStmt: TOKEN_IF '(' expression ')' statement | TOKEN_IF '(' expression ')' statement TOKEN_ELSE statement
        switchStmt: switchHeader statement
        switchHeader: TOKEN_SWITCH '(' expression ')'
        labeledStmt: TOKEN_CASE expression ':' statement | TOKEN_DEFAULT ':' statement
        loopStmt: TOKEN_FOR '(' optionalList ';' optionalConditional ';' optionalList forBegin statement | TOKEN_WHILE '(' assignment whileBegin statement | dowhileBegin statement TOKEN_WHILE '(' assignment ')'
        whileBegin: ')'
        dowhileBegin: TOKEN_DO
        forBegin: ')'
        deleteStmt: TOKEN_DELETE expression ';'
        enterStmt: TOKEN_ENTER expression ';'
        jumpStmt: TOKEN_RETURN optional ';' | TOKEN_BREAK ';' | TOKEN_CONTINUE ';'
        savepointStmt: TOKEN_SAVEPOINT '(' name ',' savepointParamList ')' | TOKEN_SAVEPOINT '(' name ')'
        optional: %empty | expression
        optionalList: %empty | expressionList
        optionalConditional: %empty | conditional
        expressionList: expression | expression ',' expressionList
        expression: assignment
        assignment: conditional | conditional '=' assignment | conditional assignmentOperator assignment
        assignmentOperator: TOKEN_OP_IADD | TOKEN_OP_ISUB | TOKEN_OP_IMUL | TOKEN_OP_IDIV | TOKEN_OP_IAND | TOKEN_OP_IOR
        conditional: logicalOr | logicalOr '?' expression ':' conditional
        logicalOr: logicalAnd | logicalOr TOKEN_OP_LOGIC_OR logicalAnd
        logicalAnd: inclusiveOr | logicalAnd TOKEN_OP_LOGIC_AND inclusiveOr
        inclusiveOr: exclusiveOr | inclusiveOr '|' exclusiveOr
        exclusiveOr: and | exclusiveOr '^' and
        and: equality | and '&' equality
        equality: relational | equality equalityOperator relational
        equalityOperator: TOKEN_OP_EQUAL | TOKEN_OP_NOTEQUAL
        relational: additive | relational relationalOperator additive
        relationalOperator: '<' | '>' | TOKEN_OP_GREQ | TOKEN_OP_LEEQ
        additive: multiplicative | additive '+' multiplicative | additive '-' multiplicative
        multiplicative: cast | multiplicative '*' cast | multiplicative '/' cast | multiplicative '%' cast
        cast: unary | '(' basicType ')' cast
        unary: postfix | '-' postfix | '!' postfix | '~' postfix | allocation
        allocation: TOKEN_NEW ident TOKEN_IN postfix | TOKEN_NEW ident
        postfix: primary | postfix '[' expression ']' | postfix '(' funcParamList ')' | postfix '.' baseIdent
        funcParamList: funcList
        funcList: func | func ',' funcList
        func: expression | %empty
        savepointParamList: savepointParam | savepointParam ',' savepointParamList
        savepointParam: ident | %empty
        primary: baseIdent | baseConst | TOKEN_THIS | '(' expression ')'
        baseIdent: ident | globalVariable | TOKEN_DEFAULT | TOKEN_SUPER | TOKEN_PARENT | TOKEN_VIRTUAL_PARENT
        globalVariable: TOKEN_GET_GAME | TOKEN_GET_PLAYER | TOKEN_GET_CAMERA | TOKEN_GET_SOUND | TOKEN_GET_DEBUG | TOKEN_GET_TIMER | TOKEN_GET_INPUT | TOKEN_GET_TELEMETRY
        baseConst: integerConst | floatConst | boolConst | nameConst | stringConst | nullConst
        integerConst: integer
        floatConst: float
        boolConst: bool
        nameConst: name
        stringConst: string
        basicType: TOKEN_TYPE_STRING | TOKEN_TYPE_BYTE | TOKEN_TYPE_BOOL | TOKEN_TYPE_INT | TOKEN_TYPE_FLOAT | TOKEN_TYPE_NAME | TOKEN_CLASS_TYPE | TOKEN_ENUM_TYPE
        integer: TOKEN_INTEGER
        float: TOKEN_FLOAT
        name: TOKEN_NAME
        string: TOKEN_STRING
        bool: TOKEN_BOOL_TRUE | TOKEN_BOOL_FALSE
        ident: TOKEN_IDENT
        nullConst: TOKEN_NULL
        """;

    internal static readonly Lazy<LalrGrammar> FileGrammar = new(() => new LalrGrammar(FileTokens, FileRules));
    internal static readonly Lazy<LalrGrammar> CodeGrammar = new(() => new LalrGrammar(CodeTokens, CodeRules));

    /// <summary>
    /// Checks a file's declarations against the file grammar. A function's code isn't looked into yet: its tokens are
    /// set aside, in the file's order, for <see cref="CheckCode"/>. wcc_lite stops at a file's first syntax error.
    /// </summary>
    public static List<WccFunctionCode> CheckFile(List<WccToken> tokens, List<WccMessage> messages)
    {
        var grammar = FileGrammar.Value;
        int next = 0;
        var functions = new List<WccFunctionCode>();
        var initializers = new List<List<WccToken>>();
        var run = new WccParserRun(grammar, FileStack, messages)
        {
            Next = () => next < tokens.Count ? tokens[next++] : null,
        };
        int bodyRule = grammar.RuleNamed("functionBody", ""), initRule = grammar.RuleNamed("functionVarInitializer", ""),
            integerRule = grammar.RuleNamed("integer", "TOKEN_INTEGER");
        run.OnReduce = rule =>
        {
            if (rule == integerRule) CheckInteger(run);
            else if (rule == bodyRule)
            {
                // the token just read goes back, unless it's a bracket: so a function whose code starts with a block
                // statement ends at that block's closing bracket, as it does in wcc_lite
                if (next > 0 && next <= tokens.Count && tokens[next - 1].Terminal != "'{'") next--;
                var body = new List<WccToken>();
                int depth = 1;
                while (next < tokens.Count)
                {
                    var token = tokens[next++];
                    if (token.Terminal == "'{'") depth++;
                    else if (token.Terminal == "'}'" && --depth == 0)
                    {
                        next--;
                        break;
                    }
                    body.Add(token);
                }
                functions.Add(new WccFunctionCode { Body = body, Initializers = initializers });
                initializers = [];
                run.DropLookahead();
            }
            else if (rule == initRule)
            {
                // from the = through the first semicolon, whatever is in between
                if (next > 0 && tokens[next - 1].Terminal != "'='") next--;
                next = Math.Max(next - 1, 0);
                var initializer = new List<WccToken>();
                while (next < tokens.Count)
                {
                    var token = tokens[next++];
                    initializer.Add(token);
                    if (token.Terminal == "';'")
                    {
                        next--;
                        break;
                    }
                }
                initializers.Add(initializer);
                run.DropLookahead();
            }
        };
        run.Parse();
        return functions;
    }

    /// <summary>
    /// Checks a function's code against the code grammar. A function with no code has its initialisers ignored
    /// altogether, by wcc_lite too. Initialisers are read without knowing the types, so <c>(CClass)x</c> isn't a cast
    /// there; in the code, a word in brackets alone that names a class or enum is one.
    /// </summary>
    /// <param name="castType">The name wcc_lite shows for a word that's a cast's type (a class is <c>handle:C</c>), or null for any other word.</param>
    public static List<WccMessage> CheckCode(WccFunctionCode function, Func<string, string?> castType)
    {
        var messages = new List<WccMessage>();
        if (function.Body.Count == 0) return messages;
        foreach (var initializer in function.Initializers)
        {
            int line = initializer.Count > 0 ? initializer[0].Line : 1;
            var tokens = new List<WccToken>(initializer.Count + 1) { new("TOKEN_IDENT", "", line) };
            tokens.AddRange(initializer);
            RunCode(tokens, messages);
        }
        var body = new List<WccToken>(function.Body);
        for (int i = 1; i + 1 < body.Count; i++)
            if (body[i].Terminal == "TOKEN_IDENT" && body[i - 1].Terminal == "'('" && body[i + 1].Terminal == "')'" && castType(body[i].Text) is { } shown)
                body[i] = body[i] with { Terminal = "TOKEN_CLASS_TYPE", Text = shown };
        RunCode(body, messages);
        return messages;
    }

    private static void RunCode(List<WccToken> tokens, List<WccMessage> messages)
    {
        var grammar = CodeGrammar.Value;
        int next = 0, integerRule = grammar.RuleNamed("integer", "TOKEN_INTEGER");
        var run = new WccParserRun(grammar, CodeStack, messages)
        {
            Next = () => next < tokens.Count ? tokens[next++] : null,
        };
        run.OnReduce = rule =>
        {
            if (rule == integerRule) CheckInteger(run);
        };
        run.Parse();
    }

    private static void CheckInteger(WccParserRun run)
    {
        if (run.Last is { } token && WccLexer.IntegerValue(token.Text) is null) run.Report("Integer Overflow"); // loc-ignore
    }
}
// loc-ignore-end
