namespace W3ModWorkbench.Core.Scripts.Standalone;

// The syntax tree of a WitcherScript file, as the standalone compiler reads it.
// Ported from the .ws-to-rsblob compiler in webspam's pull request to Aelto's tw3-cahirb.

public sealed record TypeRef(string Name, List<TypeRef> Args)
{
    public TypeRef(string name) : this(name, []) { }
}

public enum Visibility { Default, Public, Protected, Private }

public enum AnnotationKind { AddField, AddMethod, WrapMethod, ReplaceMethod }

public sealed record Annotation(AnnotationKind Kind, string? Class);

public sealed class FieldSpecifiers
{
    public Visibility Visibility;
    public bool IsImport, IsEditable, IsSaved, IsConst, IsInlined, IsOptional;
}

public sealed class FieldDecl
{
    public required List<string> Names { get; init; }
    public required TypeRef Type { get; init; }
    public required FieldSpecifiers Specifiers { get; init; }
    public Annotation? Annotation { get; set; }
    public Expr? Initializer { get; init; }
    /// <summary>An autobind field, which the game fills in itself when the object is made.</summary>
    public bool IsBinding { get; init; }
    /// <summary>What an autobind field is bound to: <c>single</c>, or the name of the component.</summary>
    public string? Binding { get; init; }
}

public sealed class FunctionSpecifiers
{
    public Visibility Visibility;
    public bool IsImport, IsFinal, IsLatent, IsEntry, IsEvent, IsTimer, IsExec, IsQuest, IsStoryScene, IsReward, IsCleanup;

    public FunctionSpecifiers Copy() => (FunctionSpecifiers)MemberwiseClone();
}

public sealed record Param(string Name, TypeRef Type, bool IsOut, bool IsOptional);

public sealed class FunctionDecl
{
    public required string Name { get; init; }
    public required FunctionSpecifiers Specifiers { get; init; }
    public required List<Param> Params { get; init; }
    public TypeRef? ReturnType { get; init; }
    public List<Stmt>? Body { get; init; }
    public Annotation? Annotation { get; set; }
}

public sealed record DefaultAssignment(string Field, Expr Value);

public sealed class ClassDecl
{
    public string Name = "";
    public string? Parent;
    public string? StateMachineOf;
    public bool IsImport, IsAbstract, IsStateMachine, IsStruct;
    public List<FieldDecl> Fields = [];
    public List<FunctionDecl> Methods = [];
    public List<DefaultAssignment> Defaults = [];
}

public sealed class EnumDecl
{
    public required string Name { get; init; }
    public required List<(string Name, long? Value)> Members { get; init; }
}

public abstract record Item;
public sealed record ClassItem(ClassDecl Decl) : Item;
public sealed record EnumItem(EnumDecl Decl) : Item;
public sealed record FunctionItem(FunctionDecl Decl) : Item;
public sealed record FieldItem(FieldDecl Decl) : Item;

public sealed record Stmt(StmtKind Kind, int Line);

public abstract record StmtKind;
public sealed record VarStmt(FieldDecl Decl) : StmtKind;
public sealed record ExprStmt(Expr Expr) : StmtKind;
public sealed record IfStmt(Expr Cond, Stmt Then, Stmt? Else) : StmtKind;
public sealed record WhileStmt(Expr Cond, Stmt Body) : StmtKind;
public sealed record DoWhileStmt(Stmt Body, Expr Cond) : StmtKind;
public sealed record ForStmt(Expr? Init, Expr? Cond, Expr? Step, Stmt Body) : StmtKind;
public sealed record SwitchStmt(Expr Value, List<SwitchItem> Items) : StmtKind;
public sealed record BreakStmt : StmtKind;
public sealed record ContinueStmt : StmtKind;
public sealed record ReturnStmt(Expr? Value) : StmtKind;
public sealed record DeleteStmt(Expr Value) : StmtKind;
public sealed record BlockStmt(List<Stmt> Statements) : StmtKind;
public sealed record EmptyStmt : StmtKind;

public abstract record SwitchItem;
public sealed record CaseItem(Expr Value) : SwitchItem;
public sealed record DefaultItem : SwitchItem;
public sealed record StmtItem(Stmt Stmt) : SwitchItem;

public sealed record Expr(ExprKind Kind, int Line);

public abstract record ExprKind;
public sealed record IntLit(long Value) : ExprKind;
public sealed record FloatLit(float Value, string Text) : ExprKind;
public sealed record StrLit(string Value) : ExprKind;
public sealed record NameLit(string Value) : ExprKind;
public sealed record BoolLit(bool Value) : ExprKind;
public sealed record NullLit : ExprKind;
public sealed record Ident(string Name) : ExprKind;
public sealed record ThisExpr : ExprKind;
public sealed record SuperExpr : ExprKind;
public sealed record ParentExpr : ExprKind;
public sealed record VirtualParentExpr : ExprKind;
public sealed record Member(Expr Object, string Name) : ExprKind;
public sealed record Call(Expr Callee, List<Expr?> Args) : ExprKind;
public sealed record Index(Expr Array, Expr Idx) : ExprKind;
public sealed record Unary(string Op, Expr Operand) : ExprKind;
public sealed record Binary(string Op, Expr Left, Expr Right) : ExprKind;
public sealed record Assign(string Op, Expr Target, Expr Value) : ExprKind;
public sealed record Ternary(Expr Cond, Expr Then, Expr Else) : ExprKind;
public sealed record Cast(TypeRef Type, Expr Operand) : ExprKind;
public sealed record New(TypeRef Type, Expr? Owner) : ExprKind;
