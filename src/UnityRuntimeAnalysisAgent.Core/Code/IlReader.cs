using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Data;

namespace UnityRuntimeAnalysisAgent.Core.Code;

/// <summary>One decoded IL instruction: its offset, opcode and raw operand.</summary>
public sealed class IlInstruction
{
    internal IlInstruction(int offset, OpCode opCode, int size, object? operand)
    {
        Offset = offset;
        OpCode = opCode;
        Size = size;
        Operand = operand;
    }

    /// <summary>Byte offset in the method body.</summary>
    public int Offset { get; }

    /// <summary>The opcode.</summary>
    public OpCode OpCode { get; }

    /// <summary>Bytes taken, operand included.</summary>
    public int Size { get; }

    /// <summary>The raw operand: a metadata token (<c>int</c>) for member/type/string/signature operands, the absolute
    /// target offset (<c>int</c>) or targets (<c>int[]</c>) for branches, or the literal number.</summary>
    public object? Operand { get; }

    /// <summary>Whether <see cref="Operand"/> is a metadata token.</summary>
    public bool HasToken => OpCode.OperandType is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineType
        or OperandType.InlineTok or OperandType.InlineString or OperandType.InlineSig;
}

/// <summary>
/// Decodes method bodies from <c>MethodBody.GetILAsByteArray()</c> with opcode tables built from the public fields of
/// <c>System.Reflection.Emit.OpCodes</c>, and resolves tokens through the method's module with its generic context.
/// Metadata only: nothing it does runs game code.
/// </summary>
public static class IlReader
{
    private static readonly OpCode?[] OneByte = new OpCode?[256];
    private static readonly OpCode?[] TwoByte = new OpCode?[256];

    static IlReader()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode op)
            {
                (op.Size == 1 ? OneByte : TwoByte)[op.Value & 0xFF] = op;
            }
        }
    }

    /// <summary>The IL bytes of a method, or null when it has no body (abstract, extern, runtime-implemented).</summary>
    public static byte[]? Body(MethodBase method)
    {
        try
        {
            return method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>SHA-256 (lowercase hex) of the IL bytes: per-method code identity, the same as static tools compute over
    /// the raw body bytes.</summary>
    public static string Hash(byte[] il)
    {
        using var sha = SHA256.Create();
        return AssemblyCatalog.Hex(sha.ComputeHash(il));
    }

    /// <summary>Decodes IL bytes (<see cref="InvalidDataException"/> on an unknown opcode or a truncated body).</summary>
    public static List<IlInstruction> Decode(byte[] il)
    {
        var instructions = new List<IlInstruction>();
        var pos = 0;
        while (pos < il.Length)
        {
            var start = pos;
            OpCode? op = il[pos] == 0xFE && pos + 1 < il.Length ? TwoByte[il[pos + 1]] : OneByte[il[pos]];
            if (op is null)
            {
                throw new InvalidDataException($"Unknown opcode 0x{il[pos]:x2} at IL_{start:x4}.");
            }

            pos += op.Value.Size;
            object? operand;
            try
            {
                operand = ReadOperand(il, ref pos, op.Value.OperandType, start);
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException)
            {
                throw new InvalidDataException($"The body ends inside the operand of {op.Value.Name} at IL_{start:x4}.");
            }

            if (pos > il.Length)
            {
                throw new InvalidDataException($"The body ends inside the operand of {op.Value.Name} at IL_{start:x4}.");
            }

            instructions.Add(new IlInstruction(start, op.Value, pos - start, operand));
        }

        return instructions;
    }

    /// <summary>Resolves a token operand to a member, type, string or signature bytes (throws when it can't).</summary>
    public static object Resolve(IlInstruction instruction, MethodBase method)
    {
        var token = (int)instruction.Operand!;
        var module = method.Module;
        var typeArgs = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArgs = method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericArguments() : null;
        return instruction.OpCode.OperandType switch
        {
            OperandType.InlineString => module.ResolveString(token),
            OperandType.InlineSig => module.ResolveSignature(token),
            OperandType.InlineField => module.ResolveField(token, typeArgs, methodArgs)!,
            OperandType.InlineMethod => module.ResolveMethod(token, typeArgs, methodArgs)!,
            OperandType.InlineType => module.ResolveType(token, typeArgs, methodArgs),
            _ => module.ResolveMember(token, typeArgs, methodArgs)!,
        };
    }

    /// <summary>An operand as JSON: <c>{field|method|type: anchor}</c>, <c>{string}</c>, <c>{target}</c>,
    /// <c>{targets}</c>, a number, or <c>{token, error}</c> when it can't be resolved.</summary>
    public static JsonValue? Operand(IlInstruction instruction, MethodBase method, bool resolve)
    {
        var operand = instruction.Operand;
        if (operand is null)
        {
            return null;
        }

        switch (instruction.OpCode.OperandType)
        {
            case OperandType.InlineBrTarget:
            case OperandType.ShortInlineBrTarget:
                return new JsonObject { { "target", JsonValue.From((int)operand) } };
            case OperandType.InlineSwitch:
                return new JsonObject { { "targets", new JsonArray(((int[])operand).Select(t => (JsonValue?)JsonValue.From(t))) } };
            case OperandType.ShortInlineR:
            case OperandType.InlineR:
                var real = Convert.ToDouble(operand, CultureInfo.InvariantCulture);
                return double.IsNaN(real) || double.IsInfinity(real)
                    ? JsonValue.From(real.ToString(CultureInfo.InvariantCulture))
                    : operand is float f ? JsonNumber.FromRawText(f.ToString("R", CultureInfo.InvariantCulture)) : new JsonNumber(real);
        }

        if (!instruction.HasToken)
        {
            return new JsonNumber(Convert.ToInt64(operand, CultureInfo.InvariantCulture));
        }

        var token = (int)operand;
        if (!resolve)
        {
            return new JsonObject { { "token", JsonValue.From(token) } };
        }

        try
        {
            return Resolve(instruction, method) switch
            {
                string s => new JsonObject { { "string", JsonValue.From(s) } },
                byte[] signature => new JsonObject { { "token", JsonValue.From(token) }, { "signature", JsonValue.From(AssemblyCatalog.Hex(signature)) } },
                Type type => new JsonObject { { "type", AnchorWriter.TypeRef(type) } },
                FieldInfo field => new JsonObject { { "field", MemberAnchor(field) } },
                MethodBase callee => new JsonObject { { "method", MemberAnchor(callee) } },
                var other => new JsonObject { { "token", JsonValue.From(token) }, { "error", JsonValue.From($"unexpected operand {other}") } },
            };
        }
        catch (Exception e)
        {
            return new JsonObject { { "token", JsonValue.From(token) }, { "error", JsonValue.From(e.GetBaseException().Message) } };
        }
    }

    /// <summary>A member's anchor, or <c>{name, error}</c> when it has no definition to anchor (e.g. array methods).</summary>
    public static JsonValue MemberAnchor(MemberInfo member)
    {
        if (member.DeclaringType is { } declaring && !AnchorWriter.CanAnchor(declaring))
        {
            return new JsonObject { { "name", JsonValue.From(AnchorWriter.MemberName(member)) }, { "error", JsonValue.From("a runtime-provided member of an array or pointer type has no definition") } };
        }

        return AnchorWriter.ToJson(AnchorWriter.ForMember(member));
    }

    /// <summary>An ILDasm-like listing.</summary>
    public static string Text(MethodBase method, IReadOnlyList<IlInstruction> instructions, bool resolve)
    {
        var text = new StringBuilder();
        text.Append("// ").Append(AnchorWriter.MemberName(method)).Append('\n');
        foreach (var instruction in instructions)
        {
            text.Append("IL_").Append(instruction.Offset.ToString("x4", CultureInfo.InvariantCulture)).Append(": ").Append(instruction.OpCode.Name);
            var operand = OperandText(instruction, method, resolve);
            if (operand.Length > 0)
            {
                text.Append(' ').Append(operand);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static string OperandText(IlInstruction instruction, MethodBase method, bool resolve)
    {
        var operand = instruction.Operand;
        switch (instruction.OpCode.OperandType)
        {
            case OperandType.InlineNone:
                return string.Empty;
            case OperandType.InlineBrTarget:
            case OperandType.ShortInlineBrTarget:
                return "IL_" + ((int)operand!).ToString("x4", CultureInfo.InvariantCulture);
            case OperandType.InlineSwitch:
                return "(" + string.Join(", ", ((int[])operand!).Select(t => "IL_" + t.ToString("x4", CultureInfo.InvariantCulture))) + ")";
        }

        if (!instruction.HasToken)
        {
            return Convert.ToString(operand, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (!resolve)
        {
            return "0x" + ((int)operand!).ToString("x8", CultureInfo.InvariantCulture);
        }

        try
        {
            return Resolve(instruction, method) switch
            {
                string s => new JsonString(s).ToString(),
                byte[] => "signature 0x" + ((int)operand!).ToString("x8", CultureInfo.InvariantCulture),
                Type type => AnchorWriter.TypeName(type),
                MemberInfo member => AnchorWriter.MemberName(member),
                var other => other.ToString() ?? string.Empty,
            };
        }
        catch (Exception e)
        {
            return $"0x{(int)operand!:x8} /* {e.GetBaseException().Message} */";
        }
    }

    private static object? ReadOperand(byte[] il, ref int pos, OperandType type, int start)
    {
        switch (type)
        {
            case OperandType.InlineNone:
                return null;
            case OperandType.ShortInlineBrTarget:
                var shortOffset = (sbyte)il[pos];
                pos += 1;
                return pos + shortOffset;
            case OperandType.InlineBrTarget:
                var offset = BitConverter.ToInt32(il, pos);
                pos += 4;
                return pos + offset;
            case OperandType.ShortInlineI:
                return type == OperandType.ShortInlineI && il[start] == 0x1F ? (object)(sbyte)il[pos++] : il[pos++]; // ldc.i4.s is signed; unaligned. is not
            case OperandType.ShortInlineVar:
                return il[pos++];
            case OperandType.InlineVar:
                var index = BitConverter.ToUInt16(il, pos);
                pos += 2;
                return index;
            case OperandType.InlineI:
                var i4 = BitConverter.ToInt32(il, pos);
                pos += 4;
                return i4;
            case OperandType.InlineI8:
                var i8 = BitConverter.ToInt64(il, pos);
                pos += 8;
                return i8;
            case OperandType.ShortInlineR:
                var r4 = BitConverter.ToSingle(il, pos);
                pos += 4;
                return r4;
            case OperandType.InlineR:
                var r8 = BitConverter.ToDouble(il, pos);
                pos += 8;
                return r8;
            case OperandType.InlineSwitch:
                var count = BitConverter.ToInt32(il, pos);
                pos += 4;
                var end = pos + (count * 4);
                var targets = new int[count];
                for (var i = 0; i < count; i++)
                {
                    targets[i] = end + BitConverter.ToInt32(il, pos + (i * 4));
                }

                pos = end;
                return targets;
            default: // token operands
                var token = BitConverter.ToInt32(il, pos);
                pos += 4;
                return token;
        }
    }
}
