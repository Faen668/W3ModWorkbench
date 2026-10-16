namespace W3BundleExplorer.Core.Scripts;

/// <summary>
/// The script engine's built-in operators. Bytecode calls them like functions with a negative id:
/// id -2 is the first entry, -3 the second, and so on (-1 calls the original of a wrapped function).
/// Each type gets its own overload, so the same symbol appears many times.
/// The list and its order come from Aelto's tw3-cahirb project, and match every id seen in real mods.
/// </summary>
internal static class Operators
{
    private static readonly string[] Names =
    [
        "Add_Int32_Int32", "Subtract_Int32_Int32", "Multiply_Int32_Int32", "Divide_Int32_Int32", "Modulo_Int32_Int32",
        "Neg_Int32", "BitNot_Int32", "And_Int32_Int32", "Or_Int32_Int32", "Xor_Int32_Int32",
        "Equal_Int32_Int32", "NotEqual_Int32_Int32", "Less_Int32_Int32", "LessEqual_Int32_Int32",
        "Greater_Int32_Int32", "GreaterEqual_Int32_Int32",
        "AssignAdd_Int32_Int32", "AssignSubtract_Int32_Int32", "AssignMultiply_Int32_Int32", "AssignDivide_Int32_Int32",
        "AssignAnd_Int32_Int32", "AssignOr_Int32_Int32",
        "Add_Uint64_Uint64", "Subtract_Uint64_Uint64", "Multiply_Uint64_Uint64", "Divide_Uint64_Uint64",
        "Modulo_Uint64_Uint64", "And_Uint64_Uint64", "Or_Uint64_Uint64", "Xor_Uint64_Uint64",
        "Equal_Uint64_Uint64", "NotEqual_Uint64_Uint64", "Less_Uint64_Uint64", "LessEqual_Uint64_Uint64",
        "Greater_Uint64_Uint64", "GreaterEqual_Uint64_Uint64",
        "AssignAdd_Uint64_Uint64", "AssignSubtract_Uint64_Uint64", "AssignMultiply_Uint64_Uint64",
        "AssignDivide_Uint64_Uint64", "AssignAnd_Uint64_Uint64", "AssignOr_Uint64_Uint64",
        "Add_String_String", "AssignAdd_String_String",
        "Add_Float_Float", "Subtract_Float_Float", "Multiply_Float_Float", "Divide_Float_Float", "Modulo_Float_Float",
        "Neg_Float", "Equal_Float_Float", "NotEqual_Float_Float", "Less_Float_Float", "LessEqual_Float_Float",
        "Greater_Float_Float", "GreaterEqual_Float_Float",
        "AssignAdd_Float_Float", "AssignSubtract_Float_Float", "AssignMultiply_Float_Float", "AssignDivide_Float_Float",
        "Add_Uint8_Int32", "Subtract_Uint8_Int32", "Multiply_Uint8_Int32", "Divide_Uint8_Int32", "Modulo_Uint8_Int32",
        "And_Uint8_Int32", "Or_Uint8_Int32", "Xor_Uint8_Int32",
        "Equal_Uint8_Uint8", "NotEqual_Uint8_Uint8", "Less_Uint8_Uint8", "LessEqual_Uint8_Uint8",
        "Greater_Uint8_Uint8", "GreaterEqual_Uint8_Int32",
        "AssignAdd_Uint8_Int32", "AssignSubtract_Uint8_Int32", "AssignMultiply_Uint8_Int32", "AssignDivide_Uint8_Int32",
        "AssignAnd_Uint8_Int32", "AssignOr_Uint8_Int32",
        "LogicAnd_Bool_Bool", "LogicOr_Bool_Bool", "LogicNot_Bool",
        "Neg_Vector", "Add_Vector_Vector", "Subtract_Vector_Vector", "Multiply_Vector_Vector", "Divide_Vector_Vector",
        "AssignAdd_Vector_Vector", "AssignSubtract_Vector_Vector", "AssignMultiply_Vector_Vector", "AssignDivide_Vector_Vector",
        "Add_Vector_Float", "Subtract_Vector_Float", "Multiply_Vector_Float", "Divide_Vector_Float",
        "AssignAdd_Vector_Float", "AssignSubtract_Vector_Float", "AssignMultiply_Vector_Float", "AssignDivide_Vector_Float",
        "Multiply_Float_Vector", "Multiply_Matrix_Matrix", "AssignMultiply_Matrix_Matrix",
        "Add_EngineTime_EngineTime", "Add_EngineTime_Float", "AssignAdd_EngineTime_EngineTime", "AssignAdd_EngineTime_Float",
        "Subtract_EngineTime_EngineTime", "Subtract_EngineTime_Float",
        "AssignSubtract_EngineTime_EngineTime", "AssignSubtract_EngineTime_Float",
        "Multiply_EngineTime_Float", "AssignMultiply_EngineTime_Float", "Divide_EngineTime_Float",
        "AssignDivide_EngineTime_Float", "Modulo_EngineTime_Float",
        "Equal_EngineTime_EngineTime", "NotEqual_EngineTime_EngineTime", "Greater_EngineTime_EngineTime",
        "GreaterEqual_EngineTime_EngineTime", "Less_EngineTime_EngineTime", "LessEqual_EngineTime_EngineTime",
        "Greater_EngineTime_Float", "GreaterEqual_EngineTime_Float", "Less_EngineTime_Float", "LessEqual_EngineTime_Float",
        "LogicAnd_handle_IScriptable_handle_IScriptable",
        "Add_GameTime_GameTime", "Add_GameTime_Int32", "Subtract_GameTime_GameTime", "Subtract_GameTime_Int32",
        "Multiply_GameTime_Float", "Divide_GameTime_Float", "Neg_GameTime",
        "Equal_GameTime_GameTime", "NotEqual_GameTime_GameTime", "Less_GameTime_GameTime", "LessEqual_GameTime_GameTime",
        "Greater_GameTime_GameTime", "GreaterEqual_GameTime_GameTime",
        "AssignAdd_GameTime_GameTime", "AssignSubtract_GameTime_GameTime", "AssignAdd_GameTime_Int32",
        "AssignSubtract_GameTime_Int32", "AssignMultiply_GameTime_Float", "AssignDivide_GameTime_Float",
        "Equal_SItemUniqueId_SItemUniqueId", "NotEqual_SItemUniqueId_SItemUniqueId",
        "Add_SAbilityAttributeValue_SAbilityAttributeValue", "Subtract_SAbilityAttributeValue_SAbilityAttributeValue",
        "AssignAdd_SAbilityAttributeValue_SAbilityAttributeValue",
        "AssignSubtract_SAbilityAttributeValue_SAbilityAttributeValue",
        "Multiply_SAbilityAttributeValue_Float",
    ];

    private static readonly Dictionary<string, (string Symbol, int Prec)> Kinds = new()
    {
        ["Add"] = ("+", Precedence.Additive), ["Subtract"] = ("-", Precedence.Additive),
        ["Multiply"] = ("*", Precedence.Multiplicative), ["Divide"] = ("/", Precedence.Multiplicative),
        ["Modulo"] = ("%", Precedence.Multiplicative), ["Neg"] = ("-", Precedence.Unary), ["BitNot"] = ("~", Precedence.Unary),
        ["And"] = ("&", Precedence.BitAnd), ["Or"] = ("|", Precedence.BitOr), ["Xor"] = ("^", Precedence.BitXor),
        ["Equal"] = ("==", Precedence.Equality), ["NotEqual"] = ("!=", Precedence.Equality),
        ["Less"] = ("<", Precedence.Relational), ["LessEqual"] = ("<=", Precedence.Relational),
        ["Greater"] = (">", Precedence.Relational), ["GreaterEqual"] = (">=", Precedence.Relational),
        ["AssignAdd"] = ("+=", Precedence.Assign), ["AssignSubtract"] = ("-=", Precedence.Assign),
        ["AssignMultiply"] = ("*=", Precedence.Assign), ["AssignDivide"] = ("/=", Precedence.Assign),
        ["AssignAnd"] = ("&=", Precedence.Assign), ["AssignOr"] = ("|=", Precedence.Assign),
        ["LogicAnd"] = ("&&", Precedence.And), ["LogicOr"] = ("||", Precedence.Or), ["LogicNot"] = ("!", Precedence.Unary),
    };

    /// <summary>The symbol and precedence for operator id <paramref name="id"/> (-2 and below).</summary>
    public static bool TryGet(int id, out string symbol, out int prec)
    {
        int index = -id - 2;
        if (index >= 0 && index < Names.Length && Kinds.TryGetValue(Names[index][..Names[index].IndexOf('_')], out var kind))
        {
            (symbol, prec) = kind;
            return true;
        }
        (symbol, prec) = ("", 0);
        return false;
    }

    /// <summary>The engine's own name, e.g. "Add_Int32_Int32", for anything that cannot be shown as a symbol.</summary>
    public static string NameOf(int id)
    {
        int index = -id - 2;
        return index >= 0 && index < Names.Length ? Names[index] : $"operator{-id}";
    }
}
