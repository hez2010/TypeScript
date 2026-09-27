using TypeScript.Compiler.Text;
using System.Collections.ObjectModel;
using System.Collections.Immutable;
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
    internal ImmutableArray<SyntaxNode> DeclarationList { get; set; } = [];
    public ImmutableArray<SyntaxNode> Declarations => DeclarationList;

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
    private List<FlowNode>? antecedents;
    private IReadOnlyList<FlowNode>? antecedentsView;
    internal List<FlowNode> AntecedentList
    {
        get
        {
            if (antecedents is null)
            {
                antecedents = [];
                antecedentsView = antecedents.AsReadOnly();
            }
            return antecedents;
        }
    }
    public IReadOnlyList<FlowNode> Antecedents => antecedentsView ?? [];
    public FlowNode? ReducedTarget { get; internal set; }
    public IReadOnlyList<FlowNode> ReducedAntecedents { get; internal set; } = [];
    public int ClauseStart { get; internal set; }
    public int ClauseEnd { get; internal set; }

    internal FlowNode(FlowFlags flags, SyntaxNode? node = null, FlowNode? antecedent = null)
    {
        Flags = flags;
        Node = node;
        Antecedent = antecedent;
    }
}

/// <summary>A view of the semantic state attached to one syntax node.</summary>
public readonly struct NodeBinding
{
    private readonly SyntaxNode node;
    internal NodeBinding(SyntaxNode node) => this.node = node;
    public Symbol? Symbol
    {
        get => node?.BindingSymbol;
        internal set => node.BindingSymbol = value;
    }
    public Symbol? LocalSymbol
    {
        get => node?.BindingLocalSymbol;
        internal set => node.BindingLocalSymbol = value;
    }
    public FlowNode? Flow
    {
        get => node?.BindingFlow;
        internal set => node.BindingFlow = value;
    }
    public FlowNode? EndFlow
    {
        get => node?.BindingEndFlow;
        internal set => node.BindingEndFlow = value;
    }
    public FlowNode? ReturnFlow
    {
        get => node?.BindingReturnFlow;
        internal set => node.BindingReturnFlow = value;
    }
    public NodeFlags Flags
    {
        get => node?.BindingFlags ?? 0;
        internal set => node.BindingFlags = value;
    }

    internal Dictionary<TextSlice, Symbol> LocalTable
    {
        get
        {
            if (node.BindingLocals is not { } locals)
            {
                node.BindingLocals = locals = new();
                node.BindingLocalsView = locals.AsReadOnly();
            }
            return locals;
        }
    }

    internal bool HasLocals => node?.BindingLocals is not null;
    public IReadOnlyDictionary<TextSlice, Symbol> Locals =>
        node?.BindingLocalsView ?? ReadOnlyDictionary<TextSlice, Symbol>.Empty;
}

/// <summary>Binding slots belong to one source tree and are published only after a successful bind. Syntax clones clear them.</summary>
public sealed class BoundSourceFile
{
    public SourceFileNode SourceFile { get; }
    public Symbol? Symbol => Get(SourceFile)?.Symbol;
    public IReadOnlyDictionary<TextSlice, Symbol> Locals => Get(SourceFile)!.Value.Locals;
    public SyntaxNode? CommonJSModuleIndicator { get; internal set; }
    public bool IsModule => SourceFile.ExternalModuleIndicator is not null || CommonJSModuleIndicator is not null;
    public IReadOnlyList<Diagnostic> Diagnostics { get; internal set; } = [];
    public IReadOnlyList<SyntaxNode> Containers { get; internal set; } = [];
    public IReadOnlyDictionary<TextSlice, Symbol> GlobalExports { get; internal set; } = ReadOnlyDictionary<TextSlice, Symbol>.Empty;
    public int SymbolCount { get; internal set; }

    internal BoundSourceFile(SourceFileNode file) => SourceFile = file;

    public NodeBinding? Get(SyntaxNode node) => ReferenceEquals(node.BindingOwner, this) ? new NodeBinding(node) : null;

    internal NodeBinding Data(SyntaxNode node)
    {
        if (!ReferenceEquals(node.BindingOwner, this))
        {
            if (node.BindingOwner is not null)
                node.ClearBindingState();
            node.BindingFlags = node.Flags;
            node.BindingOwner = this;
        }
        return new(node);
    }
}
