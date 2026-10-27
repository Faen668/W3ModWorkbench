// Part of the built-in compiler: a C# port of the .ws-to-rsblob compiler written by WebSpam for Aelto's tw3-cahirb (MIT).
// https://github.com/Aelto/tw3-cahirb/pull/1

using System.IO;
using System.Text;

namespace W3ModWorkbench.Core.Scripts.Standalone;

/// <summary>
/// One instruction on its way into a blob. Which of <see cref="A"/>, <see cref="B"/>, <see cref="C"/>, <see cref="Text"/>
/// and <see cref="Float"/> an opcode uses is listed with <see cref="Emit.Write"/>.
/// </summary>
public sealed class Ins(Op op)
{
    public Op Op { get; } = op;
    public int A, B, C;
    public string? Text;
    public float Float;

    /// <summary>Where the instruction starts in the game's in-memory layout, which jumps and skips are measured in.</summary>
    public int Offset;

    /// <summary>How big it is in memory: one byte for the opcode, then each operand at the size the game gives it.</summary>
    public int RuntimeSize => 1 + Op switch
    {
        Op.IntConst or Op.FloatConst or Op.NameConst => 4,
        Op.ShortConst => 2,
        Op.ByteConst => 1,
        Op.StringConst => 4 + Encoding.UTF8.GetByteCount(Text ?? ""),
        Op.Line => 5,
        Op.LocalVar or Op.ParamVar => 8,
        Op.ObjectVar or Op.BindableVar or Op.DefaultVar or Op.StructMember => 8,
        Op.Switch => 8 + 2,
        Op.SwitchLabel => 4,
        Op.Jump or Op.JumpIfFalse or Op.Skip => 2,
        Op.Conditional => 4,
        Op.Constructor => 1 + 8,
        Op.FinalFunc => 2 + 2 + 8,
        Op.VirtualFunc or Op.VirtualParentFunc => 2 + 2 + 4,
        Op.EntryFunc => 2 + 4,
        Op.Context => 2,
        Op.TestEqual or Op.TestNotEqual or Op.New => 8,
        Op.SavePoint => 2 + 4,
        Op.SaveValue => 4,
        Op.IntToEnum or Op.EnumToString or Op.EnumToInt or Op.DynamicCast => 8,
        >= Op.ArrayClear and <= Op.ArrayElement => 8,
        MapOp.Clear or MapOp.Size or MapOp.Contains or MapOp.Erase or MapOp.Element or MapOp.TryGet or MapOp.Key => 8,
        _ => 0,
    };
}

/// <summary>The map operations. Each takes a type, the map, then its arguments.</summary>
public static class MapOp
{
    public const Op Clear = Op.MapClear, Size = Op.MapSize, Contains = Op.MapContains, Erase = Op.MapErase, Element = Op.MapElement, TryGet = Op.MapTryGet, Key = Op.MapKey;
}

/// <summary>A function ref in an instruction: an import, the function itself, or one of the engine's operators.</summary>
public static class FuncRef
{
    public static int Import(int tableIndex) => tableIndex + 1;
    public const int Current = -1;
    public static int Operator(int operatorIndex) => -operatorIndex - 2;
}

/// <summary>Writes a blob's bytes the way the game reads them.</summary>
public sealed class BlobWriter
{
    private readonly MemoryStream _bytes = new();

    public int Length => (int)_bytes.Length;
    public byte[] ToArray() => _bytes.ToArray();

    public void U8(int value) => _bytes.WriteByte((byte)value);
    public void U16(int value) { U8(value); U8(value >> 8); }
    public void U32(uint value) { U16((int)(value & 0xFFFF)); U16((int)(value >> 16)); }
    public void F32(float value) => U32(BitConverter.SingleToUInt32Bits(value));
    public void Bytes(ReadOnlySpan<byte> data) => _bytes.Write(data);

    /// <summary>
    /// The game's variable-length integer: a sign bit and 6 bits in the first byte, 7 more bits for each byte after it.
    /// Zero is written as a negative zero, which is how the compiler wrote it.
    /// </summary>
    public void Compressed(int value) => PackedInt.Write(_bytes, value < 0 ? (uint)(-(long)value) : (uint)value, top: value <= 0);

    public void String(string value)
    {
        var data = Encoding.UTF8.GetBytes(value);
        Compressed(-data.Length);
        Bytes(data);
    }
}

public static class Emit
{
    /// <summary>
    /// Writes one instruction. Operands by opcode — A, B, C: IntConst A; ShortConst A; FloatConst Float; StringConst Text;
    /// NameConst A name; ByteConst A; Line A line, B set; Local/ParamVar A name; ObjectVar, BindableVar, DefaultVar,
    /// StructMember A name, B type; Switch A type, B skip; SwitchLabel A next, B skip; Jump, JumpIfFalse, Skip, Context A;
    /// Conditional A select, B skip; Constructor A count, B type; FinalFunc A skip, B line, C function; VirtualFunc and
    /// VirtualParentFunc A skip, B line, C name; EntryFunc A skip, C name; every op taking just a type A type.
    /// </summary>
    public static void Write(BlobWriter w, Ins ins)
    {
        w.U8((byte)ins.Op);
        switch (ins.Op)
        {
            case Op.IntConst: w.Compressed(ins.A); break;
            case Op.ShortConst: w.U16(ins.A); break;
            case Op.FloatConst: w.F32(ins.Float); break;
            case Op.StringConst:
            {
                var data = Encoding.UTF8.GetBytes(ins.Text ?? "");
                w.Compressed(data.Length);
                w.Bytes(data);
                break;
            }
            case Op.NameConst: w.Compressed(ins.A); break;
            case Op.ByteConst: w.U8(ins.A); break;
            case Op.Line: w.Compressed(ins.A); w.U8(ins.B); break;
            case Op.LocalVar or Op.ParamVar: w.Compressed(ins.A); break;
            case Op.ObjectVar or Op.BindableVar or Op.DefaultVar or Op.StructMember:
                w.Compressed(ins.A);
                w.Compressed(ins.B);
                break;
            case Op.Switch: w.Compressed(ins.A); w.U16(ins.B); break;
            case Op.SwitchLabel: w.U16(ins.A); w.U16(ins.B); break;
            case Op.Jump or Op.JumpIfFalse or Op.Skip or Op.Context: w.U16(ins.A); break;
            case Op.Conditional: w.U16(ins.A); w.U16(ins.B); break;
            case Op.Constructor: w.U8(ins.A); w.Compressed(ins.B); break;
            case Op.FinalFunc: w.U16(ins.A); w.U16(ins.B); w.Compressed(ins.C); break;
            case Op.VirtualFunc or Op.VirtualParentFunc: w.U16(ins.A); w.U16(ins.B); w.Compressed(ins.C); break;
            case Op.EntryFunc: w.U16(ins.A); w.Compressed(ins.C); break;
            case Op.TestEqual or Op.TestNotEqual or Op.New or Op.IntToEnum or Op.EnumToString or Op.EnumToInt or Op.DynamicCast:
                w.Compressed(ins.A);
                break;
            case >= Op.ArrayClear and <= Op.ArrayElement: w.Compressed(ins.A); break;
            case MapOp.Clear or MapOp.Size or MapOp.Contains or MapOp.Erase or MapOp.Element or MapOp.TryGet or MapOp.Key: w.Compressed(ins.A); break;
            case Op.SavePoint: w.U16(ins.A); w.Compressed(ins.C); break;
            case Op.SaveValue: w.Compressed(ins.A); break;
        }
    }

    /// <summary>A function's code: its in-memory size, then the instructions.</summary>
    public static byte[] Function(IReadOnlyList<Ins> code)
    {
        var w = new BlobWriter();
        w.U32((uint)code.Sum(i => i.RuntimeSize));
        foreach (var ins in code) Write(w, ins);
        return w.ToArray();
    }
}
