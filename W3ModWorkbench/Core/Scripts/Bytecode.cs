using System.Text;

namespace W3ModWorkbench.Core.Scripts;

public enum Op : byte
{
    Nop = 0x00, Null = 0x01, IntOne = 0x02, IntZero = 0x03, IntConst = 0x04, ShortConst = 0x05, FloatConst = 0x06,
    StringConst = 0x07, NameConst = 0x08, ByteConst = 0x09, BoolTrue = 0x0A, BoolFalse = 0x0B, Line = 0x0C,
    Assign = 0x0D, Target = 0x0E, LocalVar = 0x0F, ParamVar = 0x10, ObjectVar = 0x11, BindableVar = 0x12,
    DefaultVar = 0x13,
    Switch = 0x14, SwitchLabel = 0x15, SwitchDefault = 0x16, Jump = 0x17, JumpIfFalse = 0x18, Skip = 0x19,
    Conditional = 0x1A, Constructor = 0x1B, FinalFunc = 0x1C, VirtualFunc = 0x1D, VirtualParentFunc = 0x1E,
    EntryFunc = 0x1F, ParamEnd = 0x20, Return = 0x21,
    StructMember = 0x22, Context = 0x23, TestEqual = 0x24, TestNotEqual = 0x25, New = 0x26, Delete = 0x27,
    This = 0x28, Parent = 0x29, SavePoint = 0x2A, SaveValue = 0x2B, SavePointEnd = 0x2C,
    ArrayClear = 0x2D, ArraySize = 0x2E, ArrayResize = 0x2F, ArrayFindFirst = 0x30, ArrayFindFirstFast = 0x31,
    ArrayFindLast = 0x32, ArrayFindLastFast = 0x33, ArrayContains = 0x34, ArrayContainsFast = 0x35,
    ArrayPushBack = 0x36, ArrayPopBack = 0x37, ArrayInsert = 0x38, ArrayRemove = 0x39, ArrayRemoveFast = 0x3A,
    ArrayGrow = 0x3B, ArrayErase = 0x3C, ArrayEraseFast = 0x3D, ArrayLast = 0x3E, ArrayElement = 0x3F,
    StaticArraySize = 0x40, StaticArrayFindFirst = 0x41, StaticArrayFindFirstFast = 0x42, StaticArrayFindLast = 0x43,
    StaticArrayFindLastFast = 0x44, StaticArrayContains = 0x45, StaticArrayContainsFast = 0x46,
    StaticArrayLast = 0x47, StaticArrayElement = 0x48,
    BoolToByte = 0x49, BoolToInt = 0x4A, BoolToFloat = 0x4B, BoolToString = 0x4C,
    ByteToBool = 0x4D, ByteToInt = 0x4E, ByteToFloat = 0x4F, ByteToString = 0x50,
    IntToBool = 0x51, IntToByte = 0x52, IntToFloat = 0x53, IntToString = 0x54, IntToEnum = 0x55,
    FloatToBool = 0x56, FloatToByte = 0x57, FloatToInt = 0x58, FloatToString = 0x59,
    NameToBool = 0x5A, NameToString = 0x5B, StringToBool = 0x5C, StringToByte = 0x5D, StringToInt = 0x5E,
    StringToFloat = 0x5F, ObjectToBool = 0x60, ObjectToString = 0x61, EnumToString = 0x62, EnumToInt = 0x63,
    DynamicCast = 0x64,
    TheGame = 0x65, ThePlayer = 0x66, TheCamera = 0x67, TheHud = 0x68, TheSound = 0x69, TheDebug = 0x6A, TheTimer = 0x6B,
    TheInput = 0x6C, TheTelemetry = 0x6D,
    // maps, which the Remastered's scripts use; each takes a type, then the map and its arguments (0x73 is never written)
    MapClear = 0x6E, MapSize = 0x6F, MapContains = 0x70, MapErase = 0x71, MapElement = 0x72, MapKey = 0x74,
}

/// <summary>One decoded instruction. <see cref="Rt"/> is its offset in the game's in-memory layout, which jumps use.</summary>
public sealed class Instruction
{
    public Op Op { get; init; }
    public int Rt { get; init; }
    public int RtEnd { get; set; }
    /// <summary>Integer operands in file order (names, types and functions are indices).</summary>
    public int[] Args { get; init; } = [];
    public string? Text { get; init; }
    public float Float { get; init; }

    public int Arg(int i) => Args[i];

    /// <summary>The in-memory target of a Jump, JumpIfFalse or Skip.</summary>
    public int JumpTarget => Rt + 3 + Args[0];
}

public static class Bytecode
{
    // Operand codes: v varint, n name, t type, f function (index + 1, or an operator id when <= 0),
    // h signed 16-bit offset, b byte, F float, s string.
    private static readonly Dictionary<Op, string> Operands = new()
    {
        [Op.IntConst] = "v", [Op.ShortConst] = "h", [Op.FloatConst] = "F", [Op.StringConst] = "s", [Op.NameConst] = "n",
        [Op.ByteConst] = "b", [Op.Line] = "vb", [Op.LocalVar] = "n", [Op.ParamVar] = "n", [Op.ObjectVar] = "nt",
        [Op.BindableVar] = "nt", [Op.DefaultVar] = "nt", [Op.Switch] = "th", [Op.SwitchLabel] = "hh", [Op.Jump] = "h",
        [Op.JumpIfFalse] = "h", [Op.Skip] = "h", [Op.Conditional] = "hh", [Op.Constructor] = "vt", [Op.FinalFunc] = "hhf",
        [Op.VirtualFunc] = "hhn", [Op.VirtualParentFunc] = "hhn", [Op.EntryFunc] = "hn",
        [Op.StructMember] = "nt", [Op.Context] = "h", [Op.TestEqual] = "t", [Op.TestNotEqual] = "t", [Op.New] = "t",
        [Op.SavePoint] = "hn", [Op.SaveValue] = "n",
        [Op.IntToEnum] = "t", [Op.EnumToString] = "t", [Op.EnumToInt] = "t", [Op.DynamicCast] = "t",
    };

    // In-memory operand sizes that differ from the defaults (names 4, types 8, functions 8, varints 4)
    private static readonly Dictionary<(Op, int), int> RuntimeSizes = new()
    {
        [(Op.ObjectVar, 0)] = 8, [(Op.ObjectVar, 1)] = 0,
        [(Op.BindableVar, 0)] = 8, [(Op.BindableVar, 1)] = 0,
        [(Op.DefaultVar, 0)] = 8, [(Op.DefaultVar, 1)] = 0,
        [(Op.StructMember, 0)] = 8, [(Op.StructMember, 1)] = 0,
        [(Op.LocalVar, 0)] = 8, [(Op.ParamVar, 0)] = 8,
        [(Op.Constructor, 0)] = 1,
    };

    static Bytecode()
    {
        for (int op = (int)Op.ArrayClear; op <= (int)Op.ArrayElement; op++)
            Operands[(Op)op] = "t";
        foreach (var op in new[] { Op.MapClear, Op.MapSize, Op.MapContains, Op.MapErase, Op.MapElement, Op.MapKey })
            Operands[op] = "t";
    }

    public static bool IsKnown(byte op) => Enum.IsDefined(typeof(Op), op);

    /// <summary>Decodes a function's code. Throws <see cref="NotSupportedException"/> on an unknown opcode.</summary>
    public static List<Instruction> Decode(byte[] code)
    {
        var list = new List<Instruction>();
        int p = 0, rt = 0;
        while (p < code.Length)
        {
            byte raw = code[p++];
            if (!IsKnown(raw))
                throw new NotSupportedException($"Unknown opcode 0x{raw:X2} at byte {p - 1}."); // loc-ignore
            var op = (Op)raw;
            string fmt = Operands.GetValueOrDefault(op, "");
            var args = new List<int>(fmt.Length);
            string? text = null;
            float f = 0;
            int size = 1;
            for (int k = 0; k < fmt.Length; k++)
            {
                char c = fmt[k];
                int operandSize;
                switch (c)
                {
                    case 'v': case 'n': case 't': case 'f':
                        args.Add(Vlq(code, ref p));
                        operandSize = c switch { 'n' => 4, 'v' => 4, _ => 8 };
                        break;
                    case 'h':
                        args.Add(BitConverter.ToInt16(code, p));
                        p += 2;
                        operandSize = 2;
                        break;
                    case 'b':
                        args.Add(code[p++]);
                        operandSize = 1;
                        break;
                    case 'F':
                        f = BitConverter.ToSingle(code, p);
                        p += 4;
                        operandSize = 4;
                        break;
                    default: // string
                        int length = Math.Abs(Vlq(code, ref p));
                        text = Encoding.UTF8.GetString(code, p, length);
                        p += length;
                        operandSize = 4 + length;
                        break;
                }
                size += RuntimeSizes.TryGetValue((op, k), out int s) ? s : operandSize;
            }
            list.Add(new Instruction { Op = op, Rt = rt, RtEnd = rt + size, Args = [.. args], Text = text, Float = f });
            rt += size;
        }
        return [.. list];
    }

    private static int Vlq(byte[] data, ref int p) => PackedInt.ReadSigned(data, ref p);
}
