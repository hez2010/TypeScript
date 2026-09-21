using System.Collections.Concurrent;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Ast;

public sealed partial class SourceFileNode
{
    public string FileName { get; internal set; } = "";
    public SourceText Source { get; internal set; } = new("");
    public ScriptKind ScriptKind { get; internal set; }
    public bool IsDeclarationFile { get; internal set; }
    public IReadOnlyList<Diagnostic> ParseDiagnostics { get; internal set; } = [];
    public IReadOnlyList<CommentDirective> CommentDirectives { get; internal set; } = [];
    public IReadOnlyList<Diagnostic> JSDocDiagnostics { get; internal set; } = [];
    public IReadOnlyList<SyntaxNode> ReparsedClones { get; internal set; } = [];
    private ConcurrentDictionary<SyntaxNode, JSDocNode[]>? documentation;
    internal void SetDocumentation(IDictionary<SyntaxNode, JSDocNode[]> values)
    {
        if (values.Count != 0) documentation = new(values, ReferenceEqualityComparer.Instance);
    }
    public IReadOnlyList<JSDocNode> GetDocumentation(SyntaxNode node)
    {
        SyntaxNode owner = node;
        while (owner.Parent is { } parent) owner = parent;
        if (!ReferenceEquals(owner, this)) throw new ArgumentException("Node does not belong to this source file", nameof(node));
        if ((node.Flags & NodeFlags.HasJSDoc) == 0) return [];
        if (documentation is null) Interlocked.CompareExchange(ref documentation, new(ReferenceEqualityComparer.Instance), null);
        return documentation.GetOrAdd(node, target =>
        {
            var parser = new DocumentationParser(Source, ScriptKind);
            var nodes = parser.Leading(Source.ToUtf16Position(target.Pos), Source.ToUtf16Position(target.End));
            foreach (JSDocNode comment in nodes)
            {
                foreach (SyntaxNode child in comment.DescendantsAndSelf()) child.ConvertPositions(Source.ToBytePosition);
                comment.Parent = target;
            }
            return nodes;
        });
    }
    internal override SyntaxNode ShallowClone()
    {
        var clone = (SourceFileNode)base.ShallowClone();
        clone.documentation = null;
        clone.ReparsedClones = [];
        return clone;
    }
}
