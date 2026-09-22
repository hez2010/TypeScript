using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using TypeScript.Compiler.Storage;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Protocol;

// Independent version-8 source-file encoding. No reference packet is accepted
// as input: strings, extended payloads, MessagePack imports and topology are
// all produced from the C# syntax store.
public static class SliceEncoder
{
    public static byte[] Encode<TStore>(SliceFile<TStore> file, CancellationToken cancellation = default) where TStore : INodeStore
    {
        using var profile = Diagnostics.NativeProfile.Enter("TypeScript.Encode");
        var positions = new PositionMap(file.Text);
        var strings = new ArrayBufferWriter<byte>();
        var offsets = new List<uint>();
        var extended = new ArrayBufferWriter<byte>();
        var structured = new ArrayBufferWriter<byte>();
        var records = new List<NodeRecord> { default };
        var indices = new Dictionary<NodeId, uint>();
        var previousSibling = new Dictionary<uint, int>();
        var stack = new Stack<(NodeId Node, uint Parent)>();
        stack.Push((file.Root, 0));
        while (stack.TryPop(out var frame))
        {
            Diagnostics.NativeProfile.Poll();
            cancellation.ThrowIfCancellationRequested();
            uint index = checked((uint)records.Count);
            indices.Add(frame.Node, index);
            if (previousSibling.TryGetValue(frame.Parent, out int previous))
                records[previous] = records[previous] with { Next = index };
            previousSibling[frame.Parent] = (int)index;
            NodeHeader header = file.Store.Header(frame.Node);
            uint data = Data(frame.Node), flags = header.Flags;
            if (header.Kind == SyntaxKind.NodeList)
                flags = file.Store.Get<NodeListData>(frame.Node).TrailingComma ? 1u : 0;
            records.Add(
                new(header.Kind, positions.Utf8ToUtf16(header.Pos), positions.Utf8ToUtf16(header.End), 0, frame.Parent, data, flags));
            for (int i = SliceSchema.ChildSlots(file.Store, frame.Node) - 1; i >= 0; i--)
            {
                NodeId child = SliceSchema.ChildAt(file.Store, frame.Node, i);
                if (!child.IsNull)
                    stack.Push((child, index));
            }
        }
        uint importsOffset = uint.MaxValue;
        if (file.Imports.Count != 0)
        {
            importsOffset = checked((uint)structured.WrittenCount);
            MessagePackArray(structured, file.Imports.Count);
            foreach (NodeId import in file.Imports)
                MessagePackUInt(structured, indices[import]);
        }
        // SourceFile is always first, with its 76-byte extended record at zero.
        byte[] extendedBytes = extended.WrittenSpan.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(extendedBytes.AsSpan(32), importsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(
            extendedBytes.AsSpan(44),
            file.ExternalModuleIndicator.IsNull ? 0 : indices[file.ExternalModuleIndicator]);
        int stringDataOffset = checked(AstPacket.HeaderSize + offsets.Count * 4);
        int extendedOffset = checked(stringDataOffset + strings.WrittenCount);
        int structuredOffset = checked(extendedOffset + extendedBytes.Length);
        int nodeOffset = checked(structuredOffset + structured.WrittenCount);
        byte[] result = new byte[checked(nodeOffset + records.Count * NodeRecord.Size)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 8u << 24);
        BinaryPrimitives.WriteUInt128LittleEndian(result.AsSpan(4), XxHash128.HashToUInt128(file.Text));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), AstPacket.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28), (uint)stringDataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32), (uint)extendedOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36), (uint)structuredOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40), (uint)nodeOffset);
        for (int i = 0; i < offsets.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(AstPacket.HeaderSize + i * 4), offsets[i]);
        strings.WrittenSpan.CopyTo(result.AsSpan(stringDataOffset));
        extendedBytes.CopyTo(result.AsSpan(extendedOffset));
        structured.WrittenSpan.CopyTo(result.AsSpan(structuredOffset));
        for (int i = 0; i < records.Count; i++)
            records[i].Write(result.AsSpan(nodeOffset + i * NodeRecord.Size));
        return result;

        uint AddString(ReadOnlySpan<byte> text)
        {
            uint index = checked((uint)offsets.Count);
            if (index > 0xFFFFFF)
                throw new InvalidDataException("AST string table exceeds the protocol limit");
            offsets.Add(checked((uint)strings.WrittenCount));
            strings.Write(text);
            offsets.Add(checked((uint)strings.WrittenCount));
            return index;
        }
        uint Data(NodeId id)
        {
            SyntaxKind kind = file.Store.Header(id).Kind;
            if (kind == SyntaxKind.NodeList)
                return checked((uint)file.Store.Get<NodeListData>(id).Nodes.Length);
            if (kind == SyntaxKind.Identifier)
                return 0x40000000 | AddString(Wtf8.Encode(file.Store.Get<IdentifierData>(id).Text));
            if (kind == SyntaxKind.SourceFile)
            {
                uint text = AddString(file.Text), name = AddString(Wtf8.Encode(file.Name));
                foreach (uint field in new uint[]
                {
                    text,
                    name,
                    name,
                    0,
                    3,
                    uint.MaxValue,
                    uint.MaxValue,
                    uint.MaxValue,
                    uint.MaxValue,
                    uint.MaxValue,
                    uint.MaxValue,
                    0,
                    text,
                    uint.MaxValue,
                    uint.MaxValue,
                    uint.MaxValue,
                    uint.MaxValue,
                    uint.MaxValue,
                    uint.MaxValue
                })
                    UInt32(extended, field);
                return 0x80000000;
            }
            if (kind is SyntaxKind.StringLiteral or SyntaxKind.NumericLiteral)
            {
                uint offset = checked((uint)extended.WrittenCount);
                if (offset > 0xFFFFFF)
                    throw new InvalidDataException("AST extended data exceeds the protocol limit");
                string text;
                uint flags;
                if (kind == SyntaxKind.StringLiteral)
                {
                    var payload = file.Store.Get<StringLiteralData>(id);
                    text = payload.Text;
                    flags = payload.TokenFlags;
                }
                else
                {
                    var payload = file.Store.Get<NumericLiteralData>(id);
                    text = payload.Text;
                    flags = payload.TokenFlags;
                }
                UInt32(extended, AddString(Wtf8.Encode(text)));
                UInt32(extended, flags);
                return 0x80000000 | offset;
            }
            uint data = 0;
            int count = SliceSchema.ChildSlots(file.Store, id);
            for (int i = 0; i < count; i++)
                if (!SliceSchema.ChildAt(file.Store, id, i).IsNull)
                    data |= 1u << i;
            if (kind == SyntaxKind.ImportClause && file.Store.Get<ImportClauseData>(id).PhaseModifier == SyntaxKind.TypeKeyword)
                data |= 1u << 24;
            if (kind == SyntaxKind.ImportSpecifier && file.Store.Get<ImportSpecifierData>(id).IsTypeOnly)
                data |= 1u << 24;
            if (kind == SyntaxKind.VariableDeclarationList)
                data |= (file.Store.Header(id).Flags & 3) << 24;
            if (kind == SyntaxKind.PrefixUnaryExpression)
                data |= (uint)file.Store.Get<PrefixUnaryExpressionData>(id).Operator << 24;
            return data;
        }
    }

    private static void UInt32(IBufferWriter<byte> buffer, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.GetSpan(4), value);
        buffer.Advance(4);
    }

    private static void MessagePackArray(IBufferWriter<byte> buffer, int length)
    {
        Span<byte> target = buffer.GetSpan(5);
        if (length < 16)
        {
            target[0] = (byte)(0x90 | length);
            buffer.Advance(1);
        }
        else if (length <= ushort.MaxValue)
        {
            target[0] = 0xDC;
            BinaryPrimitives.WriteUInt16BigEndian(target[1..], (ushort)length);
            buffer.Advance(3);
        }
        else
        {
            target[0] = 0xDD;
            BinaryPrimitives.WriteUInt32BigEndian(target[1..], (uint)length);
            buffer.Advance(5);
        }
    }

    private static void MessagePackUInt(IBufferWriter<byte> buffer, uint value)
    {
        Span<byte> target = buffer.GetSpan(5);
        if (value <= 127)
        {
            target[0] = (byte)value;
            buffer.Advance(1);
        }
        else if (value <= byte.MaxValue)
        {
            target[0] = 0xCC;
            target[1] = (byte)value;
            buffer.Advance(2);
        }
        else if (value <= ushort.MaxValue)
        {
            target[0] = 0xCD;
            BinaryPrimitives.WriteUInt16BigEndian(target[1..], (ushort)value);
            buffer.Advance(3);
        }
        else
        {
            target[0] = 0xCE;
            BinaryPrimitives.WriteUInt32BigEndian(target[1..], value);
            buffer.Advance(5);
        }
    }
}
