using TypeScript.Compiler.Text;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Binding;

public sealed class Symbol
{
    private static long nextId;
    private long id;

    public long Id
    {
        get
        {
            if (Volatile.Read(ref id) == 0)
                Interlocked.CompareExchange(ref id, Interlocked.Increment(ref nextId), 0);
            return Volatile.Read(ref id);
        }
    }

    public SymbolFlags Flags { get; internal set; }
    public CheckFlags CheckFlags { get; internal set; }
    public TextSlice Name { get; }
    public Symbol? Parent { get; internal set; }
    public Symbol? ExportSymbol { get; internal set; }
    public SyntaxNode? ValueDeclaration { get; internal set; }
    internal List<SyntaxNode> DeclarationList { get; } = [];
    public IReadOnlyList<SyntaxNode> Declarations { get; }

    internal Dictionary<TextSlice, Symbol> MemberTable
    {
        get
        {
            if (members is null)
            {
                members = new();
                membersView = members.AsReadOnly();
            }
            return members;
        }
    }

    internal Dictionary<TextSlice, Symbol> ExportTable
    {
        get
        {
            if (exports is null)
            {
                exports = new();
                exportsView = exports.AsReadOnly();
            }
            return exports;
        }
    }

    private Dictionary<TextSlice, Symbol>? members, exports;
    private IReadOnlyDictionary<TextSlice, Symbol>? membersView, exportsView;
    public IReadOnlyDictionary<TextSlice, Symbol> Members => membersView ?? Empty;
    public IReadOnlyDictionary<TextSlice, Symbol> Exports => exportsView ?? Empty;
    private static readonly IReadOnlyDictionary<TextSlice, Symbol> Empty = ReadOnlyDictionary<TextSlice, Symbol>.Empty;
    public SymbolFlags CombinedFlags => Flags | (ExportSymbol?.Flags ?? 0);

    internal Symbol(SymbolFlags flags, TextSlice name)
    {
        Flags = flags;
        Name = name;
        Declarations = DeclarationList.AsReadOnly();
    }

    // A noncharacter prefix is represented independently of the Go implementation's invalid UTF-8 byte.
    // Source names starting with this prefix are doubled by the binder so they cannot collide.
    // Escaped names at client/serialization boundaries retain the documented __ spelling.
    public const string InternalPrefix = "\uFDD0";

    public static TextSlice EscapeName(TextSlice name) => name.Span.StartsWith(InternalPrefix + InternalPrefix, StringComparison.Ordinal)
            ? name[InternalPrefix.Length..] : name.Span.StartsWith(InternalPrefix, StringComparison.Ordinal)
                ? TextSlice.Concat("__", name[InternalPrefix.Length..]) : name.Span.StartsWith("__", StringComparison.Ordinal) ? TextSlice.Concat("_", name) : name;
}

public sealed class FlowNode
{
    public FlowFlags Flags { get; internal set; }
    public SyntaxNode? Node { get; internal set; }
    public FlowNode? Antecedent { get; internal set; }
    internal List<FlowNode> AntecedentList { get; } = [];
    public IReadOnlyList<FlowNode> Antecedents { get; }
    public FlowNode? ReducedTarget { get; internal set; }
    public IReadOnlyList<FlowNode> ReducedAntecedents { get; internal set; } = [];
    public int ClauseStart { get; internal set; }
    public int ClauseEnd { get; internal set; }

    internal FlowNode(FlowFlags flags, SyntaxNode? node = null, FlowNode? antecedent = null)
    {
        Flags = flags;
        Node = node;
        Antecedent = antecedent;
        Antecedents = AntecedentList.AsReadOnly();
    }
}

public sealed class NodeBinding
{
    public Symbol? Symbol { get; internal set; }
    public Symbol? LocalSymbol { get; internal set; }
    public FlowNode? Flow { get; internal set; }
    public FlowNode? EndFlow { get; internal set; }
    public FlowNode? ReturnFlow { get; internal set; }
    public NodeFlags Flags { get; internal set; }

    internal Dictionary<TextSlice, Symbol> LocalTable
    {
        get
        {
            if (locals is null)
            {
                locals = new();
                localsView = locals.AsReadOnly();
            }
            return locals;
        }
    }

    private Dictionary<TextSlice, Symbol>? locals;
    private IReadOnlyDictionary<TextSlice, Symbol>? localsView;
    internal bool HasLocals => locals is not null;
    public IReadOnlyDictionary<TextSlice, Symbol> Locals => localsView ?? ReadOnlyDictionary<TextSlice, Symbol>.Empty;
}

/// <summary>Binding state is owned separately from the immutable parsed tree and published only after a successful bind.</summary>
public sealed class BoundSourceFile
{
    private readonly Dictionary<SyntaxNode, NodeBinding> nodes = new(ReferenceEqualityComparer.Instance);
    public SourceFileNode SourceFile { get; }
    public Symbol? Symbol => Get(SourceFile)?.Symbol;
    public IReadOnlyDictionary<TextSlice, Symbol> Locals => Get(SourceFile)!.Locals;
    public SyntaxNode? CommonJSModuleIndicator { get; internal set; }
    public bool IsModule => SourceFile.ExternalModuleIndicator is not null || CommonJSModuleIndicator is not null;
    public IReadOnlyList<Diagnostic> Diagnostics { get; internal set; } = [];
    public IReadOnlyList<SyntaxNode> Containers { get; internal set; } = [];
    public IReadOnlyDictionary<TextSlice, Symbol> GlobalExports { get; internal set; } = ReadOnlyDictionary<TextSlice, Symbol>.Empty;
    public int SymbolCount { get; internal set; }

    internal BoundSourceFile(SourceFileNode file) => SourceFile = file;

    public NodeBinding? Get(SyntaxNode node) => nodes.GetValueOrDefault(node);

    internal NodeBinding Data(SyntaxNode node)
    {
        ref NodeBinding? data = ref CollectionsMarshal.GetValueRefOrAddDefault(nodes, node, out _);
        return data ??= new() { Flags = node.Flags };
    }
}
