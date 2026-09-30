using TypeScript.Compiler.Text;
using System.Globalization;
using System.Text;
using TypeScript.Compiler.Storage;

namespace TypeScript.Compiler.Syntax;

// An iterative, semantic printer for the phase-1 productions. The full emitter
// and preservation of comments/maps remain later work; this retires stack risk
// without pretending that returning original source is tree printing.
public static class SlicePrinter
{
    private readonly record struct Item(NodeId Node, Utf8String? Text);

    public static Utf8String Print<TStore>(SliceFile<TStore> file, CancellationToken cancellation = default) where TStore : INodeStore
    {
        using var profile = Diagnostics.NativeProfile.Enter(Utf8Literals.TypeScriptPrint);
        var output = new Utf8StringBuilder();
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
                    Nodes(store.Get<NodeListData>(id).Nodes, Utf8Literals.LineFeed);
                    break;
                case SyntaxKind.TypeAliasDeclaration:
                    var alias = store.Get<TypeAliasDeclarationData>(id);
                    Sequence(T(alias.Modifiers.IsNull ? Utf8String.Empty : Utf8Literals.ExportPrefix), T(Utf8Literals.TypePrefix), N(alias.Name), T(Utf8Literals.AssignmentSeparator), N(alias.Type), T(Utf8Literals.Semicolon));
                    break;
                case SyntaxKind.ImportDeclaration:
                    var import = store.Get<ImportDeclarationData>(id);
                    Sequence(T(Utf8Literals.ImportPrefix), N(import.ImportClause), T(Utf8Literals.From), N(import.ModuleSpecifier), T(Utf8Literals.Semicolon));
                    break;
                case SyntaxKind.ImportClause:
                    var clause = store.Get<ImportClauseData>(id);
                    Sequence(T(clause.PhaseModifier == SyntaxKind.TypeKeyword ? Utf8Literals.TypePrefix : Utf8String.Empty), N(clause.NamedBindings));
                    break;
                case SyntaxKind.NamedImports:
                    stack.Push(T(Utf8Literals.SpaceCloseBrace));
                    Nodes(store.Get<NodeListData>(store.Get<NamedImportsData>(id).Elements).Nodes, Utf8Literals.CommaSpace);
                    stack.Push(T(Utf8Literals.OpenBraceSpace));
                    break;
                case SyntaxKind.ImportSpecifier:
                    var specifier = store.Get<ImportSpecifierData>(id);
                    Sequence(
                        T(specifier.IsTypeOnly ? Utf8Literals.TypePrefix : Utf8String.Empty),
                        N(specifier.PropertyName),
                        T(specifier.PropertyName.IsNull ? Utf8String.Empty : Utf8Literals.As),
                        N(specifier.Name));
                    break;
                case SyntaxKind.VariableStatement:
                    var variable = store.Get<VariableStatementData>(id);
                    Sequence(T(variable.Modifiers.IsNull ? Utf8String.Empty : Utf8Literals.ExportPrefix), N(variable.DeclarationList), T(Utf8Literals.Semicolon));
                    break;
                case SyntaxKind.VariableDeclarationList:
                    Nodes(store.Get<NodeListData>(store.Get<VariableDeclarationListData>(id).Declarations).Nodes, Utf8Literals.CommaSpace);
                    stack.Push(T((store.Header(id).Flags & 2) != 0 ? Utf8Literals.ConstPrefix : (store.Header(id).Flags & 1) != 0 ? Utf8Literals.LetPrefix : Utf8Literals.Var));
                    break;
                case SyntaxKind.VariableDeclaration:
                    var declaration = store.Get<VariableDeclarationData>(id);
                    Sequence(
                        N(declaration.Name),
                        T(declaration.Type.IsNull ? Utf8String.Empty : Utf8Literals.ColonSpace),
                        N(declaration.Type),
                        T(declaration.Initializer.IsNull ? Utf8String.Empty : Utf8Literals.AssignmentSeparator),
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
                    Sequence(T(Utf8Literals.OpenParen), N(store.Get<ParenthesizedTypeNodeData>(id).Type), T(Utf8Literals.CloseParen));
                    break;
                case SyntaxKind.UnionType:
                    Nodes(store.Get<NodeListData>(store.Get<UnionTypeNodeData>(id).Types).Nodes, Utf8Literals.UnionSeparator);
                    break;
                case SyntaxKind.PrefixUnaryExpression:
                    var unary = store.Get<PrefixUnaryExpressionData>(id);
                    Sequence(T(unary.Operator == SyntaxKind.MinusToken ? Utf8Literals.Dash : Utf8Literals.Plus), N(unary.Operand));
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
        return output.ToUtf8String();

        static Item N(NodeId id) => new(id, null);
        static Item T(Utf8String text) => new(default, text);
        void Sequence(params ReadOnlySpan<Item> items)
        {
            for (int i = items.Length - 1; i >= 0; i--)
                stack.Push(items[i]);
        }
        void Nodes(NodeId[] nodes, Utf8String separator)
        {
            for (int i = nodes.Length - 1; i >= 0; i--)
            {
                if (i < nodes.Length - 1)
                    stack.Push(T(separator));
                stack.Push(N(nodes[i]));
            }
        }
        void String(Utf8String value)
        {
            output.Append((byte)'"');
            for (int i = 0; i < value.Length; i++)
            {
                int ch = Wtf8.Decode(value.Span[i..], out int width);
                i += width - 1;
                if (ch is '"' or '\\')
                    output.Append((byte)'\\').AppendCodePoint(ch);
                else if (ch < 32 || ch is >= 0xD800 and <= 0xDFFF)
                    output.Append("\\u"u8).Append(Utf8String.Format(ch, "x4"));
                else
                    output.AppendCodePoint(ch);
            }
            output.Append((byte)'"');
        }
    }
}
