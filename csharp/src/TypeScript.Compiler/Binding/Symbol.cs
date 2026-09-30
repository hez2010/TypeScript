using TypeScript.Compiler.Text;
using System.Collections.ObjectModel;
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
    public Utf8String Name { get; }
    public Symbol? Parent { get; internal set; }
    public Symbol? ExportSymbol { get; internal set; }
    public SyntaxNode? ValueDeclaration { get; internal set; }
    private object? declarations;
    internal SymbolDeclarations DeclarationList
    {
        get => new(declarations);
        set => declarations = value.Data;
    }
    public SymbolDeclarations Declarations => new(declarations);

    internal SymbolTable MemberTable
    {
        get
        {
            if (members is null)
            {
                int capacity = DeclarationList is [var declaration, ..] ? declaration switch
                {
                    InterfaceDeclarationNode n => n.Members?.Count ?? 0,
                    TypeLiteralNode n => n.Members?.Count ?? 0,
                    ObjectLiteralExpressionNode n => n.Properties?.Count ?? 0,
                    JsxAttributesNode n => n.Properties?.Count ?? 0,
                    _ => 0
                } : 0;
                members = new(capacity);
            }
            return members;
        }
    }

    internal SymbolTable ExportTable
    {
        get
        {
            if (exports is null)
            {
                exports = new(DeclarationList is [EnumDeclarationNode { Members: { } list }, ..] ? list.Count : 0);
            }
            return exports;
        }
    }

    private SymbolTable? members, exports;
    public IReadOnlyDictionary<Utf8String, Symbol> Members => (IReadOnlyDictionary<Utf8String, Symbol>?)members ?? Empty;
    public IReadOnlyDictionary<Utf8String, Symbol> Exports => (IReadOnlyDictionary<Utf8String, Symbol>?)exports ?? Empty;
    private static readonly IReadOnlyDictionary<Utf8String, Symbol> Empty = ReadOnlyDictionary<Utf8String, Symbol>.Empty;
    public SymbolFlags CombinedFlags => Flags | (ExportSymbol?.Flags ?? 0);

    internal Symbol(SymbolFlags flags, Utf8String name)
    {
        Flags = flags;
        Name = name;
    }

    // FE is not a valid UTF-8 leading byte and cannot occur in an identifier.
    public static readonly Utf8String InternalPrefix = new(new byte[] { 0xFE });

    internal static readonly Utf8String InternalAssignment = Utf8String.Concat(InternalPrefix, "assignment"u8);
    internal static readonly Utf8String InternalCall = Utf8String.Concat(InternalPrefix, "call"u8);
    internal static readonly Utf8String InternalClass = Utf8String.Concat(InternalPrefix, "class"u8);
    internal static readonly Utf8String InternalComputed = Utf8String.Concat(InternalPrefix, "computed"u8);
    internal static readonly Utf8String InternalConstructor = Utf8String.Concat(InternalPrefix, "constructor"u8);
    internal static readonly Utf8String InternalExport = Utf8String.Concat(InternalPrefix, "export"u8);
    internal static readonly Utf8String InternalFunction = Utf8String.Concat(InternalPrefix, "function"u8);
    internal static readonly Utf8String InternalGlobal = Utf8String.Concat(InternalPrefix, "global"u8);
    internal static readonly Utf8String InternalImportAttributes = Utf8String.Concat(InternalPrefix, "importAttributes"u8);
    internal static readonly Utf8String InternalIndex = Utf8String.Concat(InternalPrefix, "index"u8);
    internal static readonly Utf8String InternalInstantiationExpression = Utf8String.Concat(InternalPrefix, "instantiationExpression"u8);
    internal static readonly Utf8String InternalJsxAttributes = Utf8String.Concat(InternalPrefix, "jsxAttributes"u8);
    internal static readonly Utf8String InternalMissing = Utf8String.Concat(InternalPrefix, "missing"u8);
    internal static readonly Utf8String InternalNew = Utf8String.Concat(InternalPrefix, "new"u8);
    internal static readonly Utf8String InternalObject = Utf8String.Concat(InternalPrefix, "object"u8);
    internal static readonly Utf8String InternalPrivatePrefix = Utf8String.Concat(InternalPrefix, "#"u8);
    internal static readonly Utf8String InternalType = Utf8String.Concat(InternalPrefix, "type"u8);
    internal static readonly Utf8String InternalUnique = Utf8String.Concat(InternalPrefix, "@"u8);

    public static Utf8String EscapeName(Utf8String name) => name.Span.StartsWith(InternalPrefix, StringComparison.Ordinal)
                ? Utf8String.Concat("__"u8, name[InternalPrefix.Length..]) : name.Span.StartsWith("__"u8, StringComparison.Ordinal) ? Utf8String.Concat("_"u8, name) : name;
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

    internal SymbolTable LocalTable
    {
        get
        {
            if (node.BindingLocals is not { } locals)
            {
                // These lists already describe the declarations about to be bound.
                // Reserve once instead of growing every function's parameter table.
                int capacity = node switch
                {
                    IFunctionSignature signature => (signature.Parameters?.Count ?? 0) + (signature.TypeParameters?.Count ?? 0),
                    SourceFileNode source => source.Statements?.Count ?? 0,
                    _ => 0
                };
                node.BindingLocals = locals = new(capacity);
            }
            return locals;
        }
    }

    internal bool HasLocals => node?.BindingLocals is not null;
    public IReadOnlyDictionary<Utf8String, Symbol> Locals =>
        (IReadOnlyDictionary<Utf8String, Symbol>?)node?.BindingLocals ?? ReadOnlyDictionary<Utf8String, Symbol>.Empty;
}

/// <summary>Binding slots belong to one source tree and are published only after a successful bind. Syntax clones clear them.</summary>
public sealed class BoundSourceFile
{
    // Validate ownership without another GC reference from every syntax node.
    private static long nextId;
    private readonly long id = Interlocked.Increment(ref nextId);
    internal long Id => id;
    public SourceFileNode SourceFile { get; }
    public Symbol? Symbol => Get(SourceFile)?.Symbol;
    public IReadOnlyDictionary<Utf8String, Symbol> Locals => Get(SourceFile)!.Value.Locals;
    public SyntaxNode? CommonJSModuleIndicator { get; internal set; }
    public bool IsModule => SourceFile.ExternalModuleIndicator is not null || CommonJSModuleIndicator is not null;
    public IReadOnlyList<Diagnostic> Diagnostics { get; internal set; } = [];
    public IReadOnlyList<SyntaxNode> Containers { get; internal set; } = [];
    public IReadOnlyDictionary<Utf8String, Symbol> GlobalExports { get; internal set; } = ReadOnlyDictionary<Utf8String, Symbol>.Empty;
    public int SymbolCount { get; internal set; }

    internal BoundSourceFile(SourceFileNode file) => SourceFile = file;

    public NodeBinding? Get(SyntaxNode node) => node.BindingId == id ? new NodeBinding(node) : null;

    internal NodeBinding Data(SyntaxNode node)
    {
        if (node.BindingId != id)
        {
            if (node.BindingId != 0)
                node.ClearBindingState();
            node.BindingFlags = node.Flags;
            node.BindingId = id;
        }
        return new(node);
    }
}
