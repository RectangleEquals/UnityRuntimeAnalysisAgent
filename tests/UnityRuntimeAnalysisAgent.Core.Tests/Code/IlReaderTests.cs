using System.Reflection;
using System.Security.Cryptography;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Code;
using Zoo.Il;
using OperandType = System.Reflection.Emit.OperandType;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Code;

/// <summary>
/// The IL reader against dnlib, over every method body in the test assembly: same instructions, operands and exception
/// clauses, and the same IL hash as the raw body bytes in the file.
/// </summary>
public sealed class IlReaderTests : IDisposable
{
    private static readonly string ZooPath = typeof(Corpus).Assembly.Location;
    private readonly ModuleDefMD _dnlib = ModuleDefMD.Load(ZooPath);

    [Fact]
    public void Every_body_decodes_like_dnlib()
    {
        var operandTypes = new HashSet<OperandType>();
        var compared = 0;
        foreach (var method in XrefScanner.Methods(typeof(Corpus).Module))
        {
            var il = IlReader.Body(method);
            if (il is null)
            {
                continue;
            }

            var ours = IlReader.Decode(il);
            var theirs = ((MethodDef)_dnlib.ResolveToken(method.MetadataToken)).Body.Instructions;
            Assert.True(theirs.Count == ours.Count, $"{method}: {ours.Count} instructions, dnlib {theirs.Count}");
            for (var i = 0; i < ours.Count; i++)
            {
                var (mine, dn) = (ours[i], theirs[i]);
                var where = $"{method.DeclaringType}.{method} IL_{mine.Offset:x4}";
                Assert.True(dn.Offset == mine.Offset, where);
                Assert.True(dn.OpCode.Name == mine.OpCode.Name, $"{where}: {mine.OpCode.Name} vs {dn.OpCode.Name}");
                operandTypes.Add(mine.OpCode.OperandType);
                AssertSameOperand(mine, dn, method, where);
            }

            compared++;
        }

        Assert.True(compared > 100, $"only {compared} bodies");
        var every = Enum.GetValues<OperandType>().Where(t => (int)t != 6).ToHashSet(); // 6 = the obsolete InlinePhi, never emitted
        Assert.Empty(every.Except(operandTypes)); // the corpus uses every operand type
    }

    [Fact]
    public void Exception_clauses_and_locals_match()
    {
        var method = typeof(Corpus).GetMethod(nameof(Corpus.Exceptions))!;
        var body = method.GetMethodBody()!;
        var theirs = ((MethodDef)_dnlib.ResolveToken(method.MetadataToken)).Body.ExceptionHandlers;
        Assert.Equal(theirs.Count, body.ExceptionHandlingClauses.Count);
        for (var i = 0; i < theirs.Count; i++)
        {
            var (mine, dn) = (body.ExceptionHandlingClauses[i], theirs[i]);
            Assert.Equal((int)dn.TryStart.Offset, mine.TryOffset);
            Assert.Equal((int)dn.HandlerStart.Offset, mine.HandlerOffset);
        }

        Assert.Contains(body.ExceptionHandlingClauses, c => c.Flags == ExceptionHandlingClauseOptions.Filter);
        Assert.Contains(body.ExceptionHandlingClauses, c => c.Flags == ExceptionHandlingClauseOptions.Finally);

        var many = typeof(Corpus).GetMethod(nameof(Corpus.ManyLocals))!;
        Assert.Equal(((MethodDef)_dnlib.ResolveToken(many.MetadataToken)).Body.Variables.Count, many.GetMethodBody()!.LocalVariables.Count);
    }

    [Fact]
    public void Il_hashes_equal_the_raw_body_bytes_in_the_file()
    {
        var file = File.ReadAllBytes(ZooPath);
        foreach (var method in XrefScanner.Methods(typeof(Corpus).Module).Where(m => m.DeclaringType!.Namespace == "Zoo.Il"))
        {
            var il = IlReader.Body(method);
            if (il is null)
            {
                continue;
            }

            var def = (MethodDef)_dnlib.ResolveToken(method.MetadataToken);
            var offset = (int)_dnlib.Metadata.PEImage.ToFileOffset(def.RVA);
            var (headerSize, codeSize) = (file[offset] & 3) == 2 ? (1, file[offset] >> 2) : ((file[offset + 1] >> 4) * 4, BitConverter.ToInt32(file, offset + 4));
            var raw = file.AsSpan(offset + headerSize, codeSize).ToArray();
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(raw)), IlReader.Hash(il));
        }
    }

    [Fact]
    public void Operands_resolve_to_anchors_strings_targets_and_readable_text()
    {
        var method = typeof(Corpus).GetMethod(nameof(Corpus.Calls))!;
        var instructions = IlReader.Decode(IlReader.Body(method)!);
        var operands = instructions.Select(i => IlReader.Operand(i, method, resolve: true)).OfType<JsonObject>().ToList();
        var calls = operands.Where(o => o.ContainsKey("method")).Select(o => o["method"]!).OfType<JsonObject>().ToList();
        Assert.Contains(calls, c => ((JsonString)c["name"]!).Value.StartsWith("System.Collections.Generic.List`1<System.Int32>::Add", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.ContainsKey("error")); // the 2-D array Set has no definition: named, with the reason
        Assert.Contains(calls, c => c.ContainsKey("methodArgs")); // Box<string>.Map<int>

        var sw = typeof(Corpus).GetMethod(nameof(Corpus.Switch))!;
        var switchOperand = IlReader.Decode(IlReader.Body(sw)!).Select(i => IlReader.Operand(i, sw, true)).OfType<JsonObject>().Single(o => o.ContainsKey("targets"));
        Assert.Equal(5, ((JsonArray)switchOperand["targets"]!).Count);

        var pointer = typeof(Corpus).GetMethod(nameof(Corpus.FunctionPointer))!;
        Assert.Contains(IlReader.Decode(IlReader.Body(pointer)!).Select(i => IlReader.Operand(i, pointer, true)).OfType<JsonObject>(), o => o.ContainsKey("signature"));

        var text = IlReader.Text(typeof(Corpus).GetMethod(nameof(Corpus.Strings))!, IlReader.Decode(IlReader.Body(typeof(Corpus).GetMethod(nameof(Corpus.Strings))!)!), resolve: true);
        Assert.Contains("ldstr \"item_added\"", text);
        Assert.Contains("IL_0000:", text);
    }

    [Fact]
    public void Truncated_and_unknown_bodies_are_reported()
    {
        Assert.Throws<InvalidDataException>(() => IlReader.Decode([0x20, 0x01])); // ldc.i4 without its 4 bytes
        Assert.Throws<InvalidDataException>(() => IlReader.Decode([0xFE, 0xFF])); // no such two-byte opcode
        Assert.Throws<InvalidDataException>(() => IlReader.Decode([0xA6])); // unused one-byte slot
    }

    public void Dispose() => _dnlib.Dispose();

    private void AssertSameOperand(IlInstruction mine, Instruction dn, MethodBase method, string where)
    {
        switch (dn.Operand)
        {
            case null:
                Assert.Null(mine.Operand);
                break;
            case Instruction target:
                Assert.True((int)target.Offset == (int)mine.Operand!, where);
                break;
            case Instruction[] targets:
                Assert.Equal(targets.Select(t => (int)t.Offset), (int[])mine.Operand!);
                break;
            case Local local:
                Assert.True(local.Index == Convert.ToInt32(mine.Operand, System.Globalization.CultureInfo.InvariantCulture), where);
                break;
            case Parameter parameter:
                Assert.True(parameter.Index == Convert.ToInt32(mine.Operand, System.Globalization.CultureInfo.InvariantCulture), where);
                break;
            case string literal:
                Assert.Equal(literal, IlReader.Resolve(mine, method));
                break;
            case MethodSig:
                Assert.IsType<byte[]>(IlReader.Resolve(mine, method));
                break;
            case IMDTokenProvider token:
                Assert.True(token.MDToken.Raw == (uint)(int)mine.Operand!, where);
                var resolved = (MemberInfo)IlReader.Resolve(mine, method);
                Assert.True(((IFullName)token).Name == resolved.Name || token is TypeSpec || token is MethodSpec, $"{where}: {resolved.Name} vs {((IFullName)token).Name}");
                break;
            default:
                Assert.True(Equals(Convert.ToDouble(dn.Operand, System.Globalization.CultureInfo.InvariantCulture), Convert.ToDouble(mine.Operand, System.Globalization.CultureInfo.InvariantCulture)), $"{where}: {mine.Operand} vs {dn.Operand}");
                break;
        }
    }
}
