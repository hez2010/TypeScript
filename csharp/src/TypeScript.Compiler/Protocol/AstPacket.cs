using System.Buffers.Binary;
using System.IO.Hashing;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Protocol;

public readonly record struct NodeRecord(SyntaxKind Kind, int Pos, int End, uint Next, uint Parent, uint Data, uint Flags)
{
    public const int Size = 28;

    public static NodeRecord Read(ReadOnlySpan<byte> bytes) => new(
            (SyntaxKind)BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]));

    public void Write(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)Kind);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], Pos);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], End);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], Next);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..], Parent);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[20..], Data);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[24..], Flags);
    }
}

// A lossless packet view for the representation experiment. It validates the
// section envelope and node links; it does not yet decode structured MessagePack
// metadata or replace the source-file AST encoder.
public sealed class AstPacket
{
    public const byte FormatVersion = 9;
    public const int HeaderSize = 44;
    private readonly byte[] bytes;
    public int NodesOffset { get; }
    public int NodeCount => (bytes.Length - NodesOffset) / NodeRecord.Size;

    public AstPacket(ReadOnlySpan<byte> source)
    {
        // The implementation puts version in the high byte of an LE uint32.
        // The Go format comment saying byte 0 is inconsistent with its writer.
        if (source.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(source) >> 24 != FormatVersion)
            throw new InvalidDataException("Expected UTF-8 AST packet version 9");
        int previous = HeaderSize;
        for (int i = 24; i < HeaderSize; i += 4)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(source[i..]);
            if (offset < previous || offset > source.Length)
                throw new InvalidDataException("Invalid AST section offsets");
            previous = (int)offset;
        }
        NodesOffset = previous;
        if ((source.Length - NodesOffset) % NodeRecord.Size != 0 || source.Length - NodesOffset < 2 * NodeRecord.Size)
            throw new InvalidDataException("Invalid AST node section");
        bytes = source.ToArray();
        if (GetNode(0) != default)
            throw new InvalidDataException("Expected null record");
        for (int i = 1; i < NodeCount; i++)
        {
            NodeRecord node = GetNode(i);
            // Source order guarantees earlier parents and forward siblings.
            if (node.Parent >= i || node.Next != 0 && (node.Next <= i || node.Next >= NodeCount))
                throw new InvalidDataException(
                    $"Invalid AST topology at {i}/{NodeCount}: parent={node.Parent}, next={node.Next}, kind={node.Kind}, offset={NodesOffset}");
        }
    }

    public NodeRecord GetNode(int index)
    {
        if ((uint)index >= (uint)NodeCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return NodeRecord.Read(bytes.AsSpan(NodesOffset + index * NodeRecord.Size, NodeRecord.Size));
    }

    public byte[] Reencode()
    {
        byte[] result = new byte[bytes.Length];
        bytes.AsSpan(0, NodesOffset).CopyTo(result);
        for (int i = 0; i < NodeCount; i++)
            GetNode(i).Write(result.AsSpan(NodesOffset + i * NodeRecord.Size));
        return result;
    }

    public bool HasContentHash(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(4, 16)) == XxHash128.HashToUInt128(source);
}
