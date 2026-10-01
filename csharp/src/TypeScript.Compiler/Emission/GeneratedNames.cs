using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

[Flags]
public enum GeneratedIdentifierFlags
{
    None, Auto, Loop, Unique, Node,
    KindMask = 7,
    ReservedInNestedScopes = 1 << 3,
    Optimistic = 1 << 4,
    FileLevel = 1 << 5,
    AllowNameSubstitution = 1 << 6
}

public readonly record struct AutoGenerateOptions(GeneratedIdentifierFlags Flags = 0, Utf8String Prefix = default, Utf8String Suffix = default);
public sealed record AutoGenerateInfo(long Id, GeneratedIdentifierFlags Flags, Utf8String Prefix, Utf8String Suffix, SyntaxNode? Node);

public sealed partial class EmitContext
{
    private static long nextAutoGenerateId;
    private readonly Dictionary<SyntaxNode, AutoGenerateInfo> generatedNames = new(ReferenceEqualityComparer.Instance);
    public AutoGenerateInfo? GetAutoGenerateInfo(SyntaxNode node) => generatedNames.GetValueOrDefault(node);
    public IdentifierNode NewTempVariable(AutoGenerateOptions options = default) => GeneratedIdentifier(GeneratedIdentifierFlags.Auto, default, null, options);
    public IdentifierNode NewLoopVariable(AutoGenerateOptions options = default) => GeneratedIdentifier(GeneratedIdentifierFlags.Loop, default, null, options);
    public IdentifierNode NewUniqueName(Utf8String text, AutoGenerateOptions options = default) => GeneratedIdentifier(GeneratedIdentifierFlags.Unique, text, null, options);
    public IdentifierNode NewGeneratedNameForNode(SyntaxNode node, AutoGenerateOptions options = default) =>
        GeneratedIdentifier(GeneratedIdentifierFlags.Node, default, node, OptimisticPrefix(options));

    public PrivateIdentifierNode NewUniquePrivateName(Utf8String text, AutoGenerateOptions options = default)
    {
        if (text.Length != 0 && text[0] != '#')
            throw new ArgumentException("Private identifiers must start with #", nameof(text));
        return (PrivateIdentifierNode)NewGeneratedIdentifier(GeneratedIdentifierFlags.Unique, text, null, options, true);
    }

    public PrivateIdentifierNode NewGeneratedPrivateNameForNode(SyntaxNode node, AutoGenerateOptions options = default) =>
        (PrivateIdentifierNode)NewGeneratedIdentifier(GeneratedIdentifierFlags.Node, default, node, OptimisticPrefix(options), true);

    private static AutoGenerateOptions OptimisticPrefix(AutoGenerateOptions options) => options.Prefix.Length != 0 || options.Suffix.Length != 0
        ? options with { Flags = options.Flags | GeneratedIdentifierFlags.Optimistic } : options;

    private IdentifierNode GeneratedIdentifier(GeneratedIdentifierFlags kind, Utf8String text, SyntaxNode? node, AutoGenerateOptions options) =>
        (IdentifierNode)NewGeneratedIdentifier(kind, text, node, options, false);

    private SyntaxNode NewGeneratedIdentifier(GeneratedIdentifierFlags kind, Utf8String text, SyntaxNode? node, AutoGenerateOptions options, bool privateName)
    {
        long id = Interlocked.Increment(ref nextAutoGenerateId);
        if (text.Length == 0)
            text = NameGenerator.Format(privateName, options.Prefix, node is IdentifierNode identifier ? identifier.Text
                : node is PrivateIdentifierNode privateIdentifier ? privateIdentifier.Text
                : Utf8String.Concat("(auto@"u8, Utf8String.Format(id), ")"u8), options.Suffix);
        SyntaxNode name = privateName ? Factory.NewPrivateIdentifier(text) : Factory.NewIdentifier(text);
        generatedNames.Add(name, new(id, kind | (options.Flags & ~GeneratedIdentifierFlags.KindMask), options.Prefix, options.Suffix, node));
        return name;
    }

    public SyntaxNode GetNodeForGeneratedName(SyntaxNode name)
    {
        var info = GetAutoGenerateInfo(name);
        if (info is null || (info.Flags & GeneratedIdentifierFlags.KindMask) != GeneratedIdentifierFlags.Node || info.Node is null)
            return name;
        var node = info.Node;
        var original = Original(node);
        while (original is not null)
        {
            node = original;
            if (node is IdentifierNode or PrivateIdentifierNode)
            {
                var other = GetAutoGenerateInfo(node);
                if (other is null || (other.Flags & GeneratedIdentifierFlags.KindMask) == GeneratedIdentifierFlags.Node && other.Id != info.Id)
                    break;
                if ((other.Flags & GeneratedIdentifierFlags.KindMask) == GeneratedIdentifierFlags.Node)
                {
                    original = other.Node;
                    continue;
                }
            }
            original = Original(node);
        }
        return node;
    }
}

public sealed class NameGenerator(EmitContext context, Func<Utf8String, bool, bool>? isFileLevelUnique = null, Func<SyntaxNode, Utf8String>? getText = null)
{
    private const uint LoopFlag = 0x10000000;
    private sealed class Scope(Scope? parent)
    {
        internal readonly Scope? Parent = parent;
        internal readonly Dictionary<Utf8String, uint> Counters = [];
        internal readonly HashSet<Utf8String> Reserved = [];
    }
    private Scope? scope, privateScope;
    private readonly HashSet<Utf8String> generated = [];
    private readonly Dictionary<long, Utf8String> generatedById = [];
    private readonly Dictionary<SyntaxNode, Utf8String> generatedByNode = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SyntaxNode, Utf8String> generatedPrivateByNode = new(ReferenceEqualityComparer.Instance);

    public void PushScope(bool reuseTempVariableScope = false)
    {
        privateScope = new(privateScope);
        if (!reuseTempVariableScope)
            scope = new(scope);
    }

    public void PopScope(bool reuseTempVariableScope = false)
    {
        privateScope = privateScope?.Parent;
        if (!reuseTempVariableScope)
            scope = scope?.Parent;
    }

    private Scope Current(bool privateName) => privateName ? privateScope ??= new(null) : scope ??= new(null);
    private static Utf8String Unhash(Utf8String text) => text.Length != 0 && text[0] == '#' ? text[1..] : text;
    public static Utf8String Format(bool privateName, Utf8String prefix, Utf8String name, Utf8String suffix) =>
        Utf8String.ConcatMany(privateName ? "#"u8 : default, Unhash(prefix), Unhash(name), Unhash(suffix));

    private Utf8String Text(SyntaxNode node) => getText?.Invoke(node) ?? node switch
    {
        IdentifierNode n => n.Text,
        PrivateIdentifierNode n => n.Text,
        StringLiteralNode n => n.Text,
        NumericLiteralNode n => n.Text,
        _ => default
    };

    public Utf8String GenerateName(SyntaxNode name)
    {
        var info = context.GetAutoGenerateInfo(name);
        if (info is null)
            return Text(name);
        bool privateName = name is PrivateIdentifierNode;
        var kind = info.Flags & GeneratedIdentifierFlags.KindMask;
        if (kind == GeneratedIdentifierFlags.Node)
            return GenerateNodeName(context.GetNodeForGeneratedName(name), privateName, info.Flags, info.Prefix, info.Suffix);
        if (generatedById.TryGetValue(info.Id, out var result))
            return result;
        result = kind switch
        {
            GeneratedIdentifierFlags.Auto or GeneratedIdentifierFlags.Loop => Temporary(kind == GeneratedIdentifierFlags.Loop,
                (info.Flags & GeneratedIdentifierFlags.ReservedInNestedScopes) != 0, privateName, info.Prefix, info.Suffix),
            GeneratedIdentifierFlags.Unique => Unique(name is IdentifierNode id ? id.Text : ((PrivateIdentifierNode)name).Text,
                info.Flags, privateName, info.Prefix, info.Suffix),
            _ => Text(name)
        };
        generatedById.Add(info.Id, result);
        return result;
    }

    private Utf8String GenerateNodeName(SyntaxNode node, bool privateName, GeneratedIdentifierFlags flags, Utf8String prefix, Utf8String suffix)
    {
        var cache = privateName ? generatedPrivateByNode : generatedByNode;
        if (cache.TryGetValue(node, out var result))
            return result;
        switch (node)
        {
            case IdentifierNode or PrivateIdentifierNode:
                result = Unique(Text(node), flags & ~GeneratedIdentifierFlags.FileLevel, privateName, prefix, suffix);
                break;
            case ModuleDeclarationNode or EnumDeclarationNode:
                CheckUnformatted(privateName, prefix, suffix);
                var name = Text(node.DeclarationName!);
                result = IsUniqueLocal(name, node) ? name : Unique(name, 0, false, default, default);
                break;
            case ImportDeclarationNode import:
                CheckUnformatted(privateName, prefix, suffix);
                result = ImportName(import.ModuleSpecifier);
                break;
            case ExportDeclarationNode export:
                CheckUnformatted(privateName, prefix, suffix);
                result = ImportName(export.ModuleSpecifier);
                break;
            case ClassDeclarationNode or FunctionDeclarationNode:
                CheckUnformatted(privateName, prefix, suffix);
                result = node.DeclarationName is { } declarationName ? GenerateNodeName(declarationName, false, flags, default, default)
                    : Unique("default"u8, 0, false, default, default);
                break;
            case ExportAssignmentNode:
                CheckUnformatted(privateName, prefix, suffix);
                result = Unique("default"u8, 0, false, default, default);
                break;
            case ClassExpressionNode:
                CheckUnformatted(privateName, prefix, suffix);
                result = Unique("class"u8, 0, false, default, default);
                break;
            case MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode:
                result = node.DeclarationName is IdentifierNode member ? GenerateNodeName(member, privateName, 0, prefix, suffix)
                    : Temporary(false, false, privateName, prefix, suffix);
                break;
            case ComputedPropertyNameNode:
                result = Temporary(false, true, privateName, prefix, suffix);
                break;
            default:
                result = Temporary(false, false, privateName, prefix, suffix);
                break;
        }
        cache.Add(node, result);
        return result;
    }

    private static void CheckUnformatted(bool privateName, Utf8String prefix, Utf8String suffix)
    {
        if (privateName || prefix.Length != 0 || suffix.Length != 0)
            throw new ArgumentException("This declaration cannot generate a private, prefixed, or suffixed name");
    }

    private static bool IsUniqueLocal(Utf8String name, SyntaxNode container)
    {
        foreach (var node in container.DescendantsAndSelf())
            if (node.BindingLocals?.TryGetValue(name, out var symbol) == true
                && (symbol.Flags & (SymbolFlags.Value | SymbolFlags.ExportValue | SymbolFlags.Alias)) != 0)
                return false;
        return true;
    }

    private Utf8String ImportName(SyntaxNode? module)
    {
        var name = "module"u8.ToArray();
        if (module is StringLiteralNode literal)
        {
            var input = CompilerPath.BaseName(literal.Text);
            var builder = new Utf8StringBuilder();
            for (int i = 0; i < input.Length; i++)
            {
                byte c = input[i];
                if (i == 0 && c is >= (byte)'0' and <= (byte)'9')
                    builder.Append((byte)'_');
                builder.Append(c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                    or >= (byte)'0' and <= (byte)'9' or (byte)'_' ? c : (byte)'_');
            }
            name = builder.ToUtf8String().Span.ToArray();
        }
        return Unique(new Utf8String(name), 0, false, default, default);
    }

    public Utf8String MakeFileLevelOptimisticUniqueName(Utf8String name) =>
        Unique(name, GeneratedIdentifierFlags.FileLevel | GeneratedIdentifierFlags.Optimistic, false, default, default);

    private Utf8String Unique(Utf8String name, GeneratedIdentifierFlags flags, bool privateName, Utf8String prefix, Utf8String suffix)
    {
        name = Unhash(name);
        bool fileLevel = (flags & GeneratedIdentifierFlags.FileLevel) != 0;
        bool reserved = (flags & GeneratedIdentifierFlags.ReservedInNestedScopes) != 0;
        if ((flags & GeneratedIdentifierFlags.Optimistic) != 0)
        {
            var fullName = Format(privateName, prefix, name, suffix);
            if (IsUnique(fullName, privateName, fileLevel))
            {
                Reserve(fullName, privateName, reserved, false);
                return fullName;
            }
        }
        if (name.Length != 0 && name[^1] != '_')
            name += "_"u8;
        for (long count = 1; ; count++)
        {
            var fullName = Format(privateName, prefix, name + count, suffix);
            if (!IsUnique(fullName, privateName, fileLevel))
                continue;
            Reserve(fullName, privateName, reserved, false);
            return fullName;
        }
    }

    private Utf8String Temporary(bool loop, bool reserved, bool privateName, Utf8String prefix, Utf8String suffix)
    {
        var current = Current(privateName);
        var key = prefix.Length == 0 && suffix.Length == 0 ? default : Format(privateName, prefix, default, suffix);
        uint flags = current.Counters.GetValueOrDefault(key);
        if (loop && (flags & LoopFlag) == 0)
        {
            var fullName = Format(privateName, prefix, "_i"u8, suffix);
            if (IsUnique(fullName, privateName))
            {
                current.Counters[key] = flags | LoopFlag;
                Reserve(fullName, privateName, reserved, true);
                return fullName;
            }
        }
        while (true)
        {
            uint count = flags++ & (LoopFlag - 1);
            if (count is 8 or 13)
                continue;
            Utf8String name = count < 26 ? new([(byte)'_', (byte)('a' + count)]) : Utf8String.Concat("_"u8, Utf8String.Format(count - 26));
            var fullName = Format(privateName, prefix, name, suffix);
            if (!IsUnique(fullName, privateName))
                continue;
            current.Counters[key] = flags;
            Reserve(fullName, privateName, reserved, true);
            return fullName;
        }
    }

    private void Reserve(Utf8String name, bool privateName, bool scoped, bool temporary)
    {
        if (privateName || scoped)
            Current(privateName).Reserved.Add(name);
        else if (!temporary)
            generated.Add(name);
    }

    private bool IsUnique(Utf8String name, bool privateName, bool fileLevelOnly = false)
    {
        if (isFileLevelUnique?.Invoke(name, privateName) == false)
            return false;
        if (fileLevelOnly && isFileLevelUnique is not null)
            return true;
        if (generated.Contains(name))
            return false;
        for (var current = privateName ? privateScope : scope; current is not null; current = current.Parent)
            if (current.Reserved.Contains(name))
                return false;
        return true;
    }
}
