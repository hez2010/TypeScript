using System.Collections.Concurrent;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Ast;

public readonly record struct SourceCommentRange(SyntaxKind Kind, int Pos, int End, bool HasTrailingNewLine);
public readonly record struct PragmaArgument(Utf8String Name, Utf8String Value, int Pos, int End);
public readonly record struct SourcePragma(Utf8String Name, SourceCommentRange Range, IReadOnlyDictionary<Utf8String, PragmaArgument> Arguments);
public enum ReferenceResolutionMode
{
    Unspecified = 0,
    Require = 1,
    Import = 99
}
public readonly record struct FileReference(
    Utf8String FileName,
    int Pos,
    int End,
    ReferenceResolutionMode ResolutionMode = ReferenceResolutionMode.Unspecified,
    bool Preserve = false);
public readonly record struct CheckJsDirective(bool Enabled, SourceCommentRange Range);
public readonly record struct AmdDependency(Utf8String Path, Utf8String? Name);

public sealed partial class SourceFileNode
{
    public Utf8String FileName { get; internal set; } = Utf8String.Empty;
    public SourceText Source { get; internal set; } = new(Utf8String.Empty);
    internal int NodeCount { get; set; }
    public ScriptKind ScriptKind { get; internal set; }
    public bool IsDeclarationFile { get; internal set; }
    public IReadOnlyList<Diagnostic> ParseDiagnostics { get; internal set; } = [];
    public IReadOnlyList<CommentDirective> CommentDirectives { get; internal set; } = [];
    public IReadOnlyList<Diagnostic> JSDocDiagnostics { get; internal set; } = [];
    public IReadOnlyList<Diagnostic> JSDiagnostics { get; internal set; } = [];
    public IReadOnlyList<SyntaxNode> ReparsedClones { get; internal set; } = [];
    public IReadOnlyList<SourcePragma> Pragmas { get; internal set; } = [];
    public IReadOnlyList<FileReference> ReferencedFiles { get; internal set; } = [];
    public IReadOnlyList<FileReference> TypeReferenceDirectives { get; internal set; } = [];
    public IReadOnlyList<FileReference> LibReferenceDirectives { get; internal set; } = [];
    public CheckJsDirective? CheckJsDirective { get; internal set; }
    public IReadOnlyList<AmdDependency> AmdDependencies { get; internal set; } = [];
    public Utf8String? ModuleName { get; internal set; }
    public bool HasNoDefaultLib { get; internal set; }
    public SyntaxNode? ExternalModuleIndicator { get; internal set; }
    public IReadOnlyList<SyntaxNode> Imports { get; internal set; } = [];
    public IReadOnlyList<SyntaxNode> ModuleAugmentations { get; internal set; } = [];
    public IReadOnlyList<Utf8String> AmbientModuleNames { get; internal set; } = [];
    private ConcurrentDictionary<SyntaxNode, JSDocNode[]>? documentation;

    internal void SetDocumentation(IDictionary<SyntaxNode, JSDocNode[]> values)
    {
        if (values.Count != 0)
            documentation = new(values, ReferenceEqualityComparer.Instance);
    }

    public IReadOnlyList<JSDocNode> GetDocumentation(SyntaxNode node) => Parser.RunParse(GetDocumentationAsync(node));

    public async ValueTask<IReadOnlyList<JSDocNode>> GetDocumentationAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        SyntaxNode owner = node;
        while (owner.Parent is { } parent)
            owner = parent;
        if (!ReferenceEquals(owner, this))
            throw new ArgumentException("Node does not belong to this source file", nameof(node));
        if ((node.Flags & NodeFlags.HasJSDoc) == 0)
            return [];
        if (documentation is null)
            Interlocked.CompareExchange(ref documentation, new(ReferenceEqualityComparer.Instance), null);
        if (documentation.TryGetValue(node, out JSDocNode[]? cached))
            return cached;
        var parser = new DocumentationParser(Source, ScriptKind, cancellation: cancellation);
        var nodes = await parser.LeadingAsync(
            node.Pos,
            node.End,
            node.Kind).ConfigureAwait(false);
        foreach (JSDocNode comment in nodes)
        {
            comment.Parent = node;
        }
        return documentation.GetOrAdd(node, nodes);
    }

    internal override SyntaxNode ShallowClone()
    {
        var clone = (SourceFileNode)base.ShallowClone();
        clone.documentation = null;
        clone.ReparsedClones = [];
        return clone;
    }

    internal void RemapSourceMetadata(SourceFileNode original, IReadOnlyDictionary<SyntaxNode, SyntaxNode> copies)
    {
        Dictionary<SyntaxNode, SyntaxNode>? documentationCopies = null;
        if (original.documentation is { } originalDocumentation)
            foreach (var entry in originalDocumentation)
            {
                if (!copies.TryGetValue(entry.Key, out SyntaxNode? clonedOwner))
                    continue;
                documentationCopies ??= new(ReferenceEqualityComparer.Instance);
                var comments = new JSDocNode[entry.Value.Length];
                for (int i = 0; i < comments.Length; i++)
                {
                    JSDocNode comment = entry.Value[i];
                    if (!documentationCopies.TryGetValue(comment, out SyntaxNode? copiedComment))
                    {
                        JSDocNode clonedComment = comment.DeepClone<JSDocNode>();
                        foreach (var pair in comment.DescendantsAndSelf().Zip(clonedComment.DescendantsAndSelf()))
                            documentationCopies.Add(pair.First, pair.Second);
                        clonedComment.Parent = comment.Parent is { } parent && copies.TryGetValue(parent, out SyntaxNode? parentCopy)
                            ? parentCopy
                            : clonedOwner;
                        copiedComment = clonedComment;
                    }
                    comments[i] = (JSDocNode)copiedComment;
                }
                // Synthesized aliases may begin inside their comment. Preserve
                // cached associations instead of trying to parse their node ranges.
                (documentation ??= new(ReferenceEqualityComparer.Instance))[clonedOwner] = comments;
            }
        ExternalModuleIndicator = original.ExternalModuleIndicator is { } indicator ? copies[indicator] : null;
        Imports = Array.AsReadOnly(original.Imports.Select(MapReference).ToArray());
        ModuleAugmentations = Array.AsReadOnly(original.ModuleAugmentations.Select(node => copies[node]).ToArray());

        SyntaxNode MapReference(SyntaxNode node)
        {
            if (copies.TryGetValue(node, out SyntaxNode? clone))
                return clone;
            if (documentationCopies?.TryGetValue(node, out clone) == true)
                return clone;
            throw new InvalidOperationException("Module reference does not belong to the cloned source file");
        }
    }
}
