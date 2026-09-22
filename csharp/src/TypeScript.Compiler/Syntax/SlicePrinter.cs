using System.Globalization;
using System.Text;
using TypeScript.Compiler.Storage;

namespace TypeScript.Compiler.Syntax;

// An iterative, semantic printer for the phase-1 productions. The full emitter
// and preservation of comments/maps remain later work; this retires stack risk
// without pretending that returning original source is tree printing.
public static class SlicePrinter
{
    private readonly record struct Item(NodeId Node, string? Text);

    public static string Print<TStore>(SliceFile<TStore> file, CancellationToken cancellation = default) where TStore : INodeStore
    {
        using var profile = Diagnostics.NativeProfile.Enter("TypeScript.Print");
        var output = new StringBuilder();
        var stack = new Stack<Item>();
        stack.Push(new(file.Root, null));
        while (stack.TryPop(out Item item))
        {
            Diagnostics.NativeProfile.Poll();
            cancellation.ThrowIfCancellationRequested();
            if (item.Text is not null)
            {
                output.Append(item.Text);
                continue;
            }
            if (item.Node.IsNull)
                continue;
            var id = item.Node;
            var store = file.Store;
            var kind = store.Header(id).Kind;
            switch (kind)
            {
                case SyntaxKind.SourceFile:
                    Sequence(N(store.Get<SourceFileData>(id).Statements));
                    break;
                case SyntaxKind.NodeList:
                    Nodes(store.Get<NodeListData>(id).Nodes, "\n");
                    break;
                case SyntaxKind.TypeAliasDeclaration:
                    var alias = store.Get<TypeAliasDeclarationData>(id);
                    Sequence(T(alias.Modifiers.IsNull ? "" : "export "), T("type "), N(alias.Name), T(" = "), N(alias.Type), T(";"));
                    break;
                case SyntaxKind.ImportDeclaration:
                    var import = store.Get<ImportDeclarationData>(id);
                    Sequence(T("import "), N(import.ImportClause), T(" from "), N(import.ModuleSpecifier), T(";"));
                    break;
                case SyntaxKind.ImportClause:
                    var clause = store.Get<ImportClauseData>(id);
                    Sequence(T(clause.PhaseModifier == SyntaxKind.TypeKeyword ? "type " : ""), N(clause.NamedBindings));
                    break;
                case SyntaxKind.NamedImports:
                    stack.Push(T(" }"));
                    Nodes(store.Get<NodeListData>(store.Get<NamedImportsData>(id).Elements).Nodes, ", ");
                    stack.Push(T("{ "));
                    break;
                case SyntaxKind.ImportSpecifier:
                    var specifier = store.Get<ImportSpecifierData>(id);
                    Sequence(
                        T(specifier.IsTypeOnly ? "type " : ""),
                        N(specifier.PropertyName),
                        T(specifier.PropertyName.IsNull ? "" : " as "),
                        N(specifier.Name));
                    break;
                case SyntaxKind.VariableStatement:
                    var variable = store.Get<VariableStatementData>(id);
                    Sequence(T(variable.Modifiers.IsNull ? "" : "export "), N(variable.DeclarationList), T(";"));
                    break;
                case SyntaxKind.VariableDeclarationList:
                    Nodes(store.Get<NodeListData>(store.Get<VariableDeclarationListData>(id).Declarations).Nodes, ", ");
                    stack.Push(T((store.Header(id).Flags & 2) != 0 ? "const " : (store.Header(id).Flags & 1) != 0 ? "let " : "var "));
                    break;
                case SyntaxKind.VariableDeclaration:
                    var declaration = store.Get<VariableDeclarationData>(id);
                    Sequence(
                        N(declaration.Name),
                        T(declaration.Type.IsNull ? "" : ": "),
                        N(declaration.Type),
                        T(declaration.Initializer.IsNull ? "" : " = "),
                        N(declaration.Initializer));
                    break;
                case SyntaxKind.Identifier:
                    output.Append(store.Get<IdentifierData>(id).Text);
                    break;
                case SyntaxKind.StringLiteral:
                    String(store.Get<StringLiteralData>(id).Text);
                    break;
                case SyntaxKind.NumericLiteral:
                    output.Append(store.Get<NumericLiteralData>(id).Text);
                    break;
                case SyntaxKind.LiteralType:
                    Sequence(N(store.Get<LiteralTypeNodeData>(id).Literal));
                    break;
                case SyntaxKind.TypeReference:
                    Sequence(N(store.Get<TypeReferenceNodeData>(id).TypeName));
                    break;
                case SyntaxKind.ParenthesizedType:
                    Sequence(T("("), N(store.Get<ParenthesizedTypeNodeData>(id).Type), T(")"));
                    break;
                case SyntaxKind.UnionType:
                    Nodes(store.Get<NodeListData>(store.Get<UnionTypeNodeData>(id).Types).Nodes, " | ");
                    break;
                case SyntaxKind.PrefixUnaryExpression:
                    var unary = store.Get<PrefixUnaryExpressionData>(id);
                    Sequence(T(unary.Operator == SyntaxKind.MinusToken ? "-" : "+"), N(unary.Operand));
                    break;
                case SyntaxKind.EndOfFile:
                    break;
                default:
                    if (kind is >= SyntaxKind.FirstKeyword and <= SyntaxKind.LastKeyword)
                        output.Append(SliceSchema.KeywordText(kind));
                    else
                        throw new NotSupportedException($"Cannot print {kind}");
                    break;
            }
        }
        return output.ToString();

        static Item N(NodeId id) => new(id, null);
        static Item T(string text) => new(default, text);
        void Sequence(params ReadOnlySpan<Item> items)
        {
            for (int i = items.Length - 1; i >= 0; i--)
                stack.Push(items[i]);
        }
        void Nodes(NodeId[] nodes, string separator)
        {
            for (int i = nodes.Length - 1; i >= 0; i--)
            {
                if (i < nodes.Length - 1)
                    stack.Push(T(separator));
                stack.Push(N(nodes[i]));
            }
        }
        void String(string value)
        {
            output.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (ch is '"' or '\\')
                    output.Append('\\').Append(ch);
                else if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    output.Append(ch).Append(value[++i]);
                else if (ch < 32 || char.IsSurrogate(ch))
                    output.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                else
                    output.Append(ch);
            }
            output.Append('"');
        }
    }
}
