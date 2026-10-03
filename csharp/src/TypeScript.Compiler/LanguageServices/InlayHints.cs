using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.LanguageServices;

public sealed record InlayHintLabelPart(Utf8String Value, DocumentLocation? Location = null);
public sealed record InlayHint(DocumentPosition Position, Utf8String Label, IReadOnlyList<InlayHintLabelPart>? Parts = null,
    int? Kind = null, bool? PaddingLeft = null, bool? PaddingRight = null);

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<IReadOnlyList<InlayHint>?> GetInlayHintsAsync(ProjectSnapshot project, DocumentRange range,
        UserPreferences? preferences = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        preferences ??= new();
        if (!preferences.InlayHints.Enabled) return null;
        if (project.Program is null) throw new ArgumentException("Project has no program", nameof(project));
        List<InlayHint> result = [];
        using var request = new ProjectRequest(cancellation);
        char quote = RenameQuote(projections[0].File, preferences) == "'"u8 ? '\'' : '"';
        foreach (var projection in projections)
            foreach (var span in projection.FromRange(range, MappingFeature.InlayHints))
            {
                using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request, projection.File).ConfigureAwait(false);
                var query = new InlayHintQuery(lease.Checker, projection, preferences.InlayHints, quote,
                    source => ProjectionForFile(project.Program, source), cancellation);
                result.AddRange(await query.GetAsync(span.Start, span.End));
            }
        return result;
    }

    private sealed partial class InlayHintQuery(Checker checker, DocumentProjection projection, InlayHintPreferences preferences,
        char quote, Func<SourceFileNode, DocumentProjection> project, CancellationToken cancellation)
    {
        private readonly List<InlayHint> result = [];
        private SourceFileNode File => projection.File;

        internal async ValueTask<IReadOnlyList<InlayHint>> GetAsync(int start, int end)
        {
            Stack<SyntaxNode> nodes = new(); nodes.Push(File);
            while (nodes.TryPop(out var node))
            {
                cancellation.ThrowIfCancellationRequested();
                if (node.End == node.Pos || (node.Flags & NodeFlags.Reparsed) != 0 || node.Pos > end || node.End < start
                    || SemanticSyntax.TypeNode(node) && node is not ExpressionWithTypeArgumentsNode) continue;
                if (preferences.VariableTypes == true && node is VariableDeclarationNode
                    || preferences.PropertyTypes == true && node is PropertyDeclarationNode) await VariableAsync(node);
                else if (preferences.EnumValues == true && node is EnumMemberNode { Initializer: null } member)
                {
                    if (await checker.GetConstantValueForEmitAsync(member, cancellation) is { } value)
                        Add(member.End, "= "u8 + ConstantEvaluator.ToText(value), null, null, true, null);
                }
                else if (preferences.ParameterNames == "all"u8 || preferences.ParameterNames == "literals"u8)
                {
                    if (node is CallExpressionNode or NewExpressionNode) await ArgumentsAsync(node);
                    else await FunctionAsync(node);
                }
                else await FunctionAsync(node);
                for (int i = node.ChildCount - 1; i >= 0; i--) nodes.Push(node.GetChild(i));
            }
            return result;
        }

        private async ValueTask FunctionAsync(SyntaxNode node)
        {
            if (!SemanticSyntax.FunctionDeclarationLike(node)) return;
            var function = (IFunctionSignature)node;
            var parameters = function.Parameters;
            if (preferences.ParameterTypes == true && function.TypeParameters is null
                && (parameters?.Any(p => p is ITypedNode { Type: null }) == true
                    || node is not ArrowFunctionNode && (parameters is null or { Count: 0 } || !ThisParameter(parameters[0]))
                        && (node.Flags & NodeFlags.ContainsThis) != 0))
            {
                var signature = await checker.Signatures.FromDeclarationAsync(node, cancellation);
                int index = 0;
                foreach (var parameter in (IReadOnlyList<SyntaxNode>?)parameters ?? [])
                {
                    var symbol = ThisParameter(parameter) ? signature.ThisParameter : signature.Parameters.ElementAtOrDefault(index++);
                    if (HintableDeclaration(parameter) && parameter is ParameterDeclarationNode { Type: null } declaration
                        && symbol?.ValueDeclaration is ParameterDeclarationNode valueDeclaration)
                    {
                        var type = await checker.GetTypeOfSymbolAtLocationAsync(symbol, valueDeclaration, cancellation);
                        if (!Module(type)) AddType(declaration.QuestionToken?.End ?? declaration.Name!.End, await TypePartsAsync(type));
                    }
                }
            }
            if (preferences.ReturnTypes != true || node.Kind is not (K.ArrowFunction or K.FunctionExpression or K.FunctionDeclaration or K.MethodDeclaration or K.GetAccessor)
                || function.Type is not null || SemanticSyntax.Body(node) is null) return;
            if (node is ArrowFunctionNode && await SyntaxNavigation.FindChildOfKindAsync(node, K.OpenParenToken, File, cancellation) is null) return;
            var returnSignature = await checker.Signatures.FromDeclarationAsync(node, cancellation);
            var predicate = await checker.PredicateAsync(returnSignature, cancellation);
            IReadOnlyList<InlayHintLabelPart> parts;
            if (predicate?.Type is not null) parts = await TypePartsAsync(null, predicate);
            else
            {
                var type = await checker.GetReturnTypeOfSignatureAsync(returnSignature, cancellation);
                if (Module(type)) return;
                parts = await TypePartsAsync(type);
            }
            AddType((await SyntaxNavigation.FindChildOfKindAsync(node, K.CloseParenToken, File, cancellation))?.End ?? parameters!.End, parts);
        }

        private async ValueTask VariableAsync(SyntaxNode node)
        {
            var name = node.DeclarationName!;
            if (((IInitializedNode)node).Initializer is null && !(node is PropertyDeclarationNode
                    && ((await checker.GetTypeAtLocationAsync(node, cancellation)).Flags & TypeFlags.Any) == 0)
                || name.Kind is K.ArrayBindingPattern or K.ObjectBindingPattern || node is VariableDeclarationNode && !HintableDeclaration(node)
                || ((ITypedNode)node).Type is not null) return;
            var type = await checker.GetTypeAtLocationAsync(node, cancellation);
            if (Module(type)) return;
            var parts = await TypePartsAsync(type);
            if (preferences.VariableTypesWhenNameMatches != true && name is not ComputedPropertyNameNode
                && GoUnicode.EqualFold(GoUnicode.Runes(Text(name)), GoUnicode.Runes(Utf8String.Concat(parts.Select(part => part.Value))))) return;
            AddType(name.End, parts);
        }

        private async ValueTask ArgumentsAsync(SyntaxNode node)
        {
            var arguments = node is CallExpressionNode call ? call.Arguments : ((NewExpressionNode)node).Arguments;
            if (arguments is null or { Count: 0 }) return;
            var signature = await checker.GetResolvedSignatureAsync(node, cancellation);
            if (signature is null) return;
            int index = 0;
            foreach (var original in arguments)
            {
                var argument = SkipParentheses(original);
                if (preferences.ParameterNames == "literals"u8 && !HintableLiteral(argument)) { index++; continue; }
                int spread = 0;
                if (argument is SpreadElementNode expression && await checker.GetTypeAtLocationAsync(expression.Expression!, cancellation) is TypeReference { Target: TupleType tuple })
                {
                    if (tuple.FixedLength == 0) continue;
                    spread = tuple.ElementInfos.TakeWhile(info => (info.Flags & ElementFlags.Required) != 0).Count();
                }
                var info = await ParameterAsync(signature, index);
                index += spread > 0 ? spread : 1;
                if (info is null) return;
                var (parameter, name, rest) = info.Value;
                if (preferences.ParameterNamesWhenArgumentMatches != true && !rest
                    && (argument is IdentifierNode id && id.Text == name || argument is PropertyAccessExpressionNode access && Text(access.Name!) == name)) continue;
                if (HasParameterComment(argument, name)) continue;
                Add(await SyntaxNavigation.GetStartAsync(original, File, cancellation: cancellation), default,
                    [NodePart(rest ? "..."u8 + name : name, parameter), new(":"u8)], 2, null, true);
            }
        }

        private async ValueTask<(SyntaxNode Node, Utf8String Name, bool IsRest)?> ParameterAsync(Signature signature, int index)
        {
            int count = signature.Parameters.Count - (signature.HasRestParameter ? 1 : 0);
            if (index < count)
                return signature.Parameters[index].ValueDeclaration is ParameterDeclarationNode { Name: IdentifierNode name } ? (name, name.Text, false) : null;
            if (count == signature.Parameters.Count || signature.Parameters[count].ValueDeclaration is not ParameterDeclarationNode { Name: IdentifierNode restName }) return null;
            var symbol = signature.Parameters[count];
            var type = await checker.GetTypeOfSymbolAtLocationAsync(symbol, null, cancellation);
            if (type is TypeReference { Target: TupleType tuple })
            {
                if (index - count < tuple.ElementInfos.Count && tuple.ElementInfos[index - count].LabeledDeclaration is { DeclarationName: IdentifierNode name } declaration)
                    return (name, name.Text, declaration is NamedTupleMemberNode { DotDotDotToken: not null } or ParameterDeclarationNode { DotDotDotToken: not null });
                return null;
            }
            return index == count ? (restName, symbol.Name, true) : null;
        }

        private bool HasParameterComment(SyntaxNode node, Utf8String name)
        {
            bool first = true;
            foreach (var rune in name.ToString().EnumerateRunes())
            {
                if (first ? !TokenFacts.IsIdentifierStart(rune.Value) : !TokenFacts.IsIdentifierPart(rune.Value)) return false;
                first = false;
            }
            if (first) return false;
            foreach (var comment in SyntaxPrinter.CommentRanges(File.Source.Text, node.Pos, false))
            {
                var text = File.Source.Text[comment.Pos..comment.End].ToString();
                int start = 0, end = text.Length;
                while (start < end && Trim(text[start])) start++;
                while (end > start && Trim(text[end - 1])) end--;
                if (text.AsSpan(start, end - start).SequenceEqual(name.ToString())) return true;
            }
            return false;
            static bool Trim(char ch) => char.IsWhiteSpace(ch) || ch is '/' or '*';
        }

        private void AddType(int position, IReadOnlyList<InlayHintLabelPart> parts) => Add(position, default, [new(": "u8), .. parts], 1, true, null);
        private void Add(int position, Utf8String label, IReadOnlyList<InlayHintLabelPart>? parts, int? kind, bool? left, bool? right)
        {
            var mapped = projection.ToRange(position, position, MappingFeature.InlayHints);
            if (mapped.Fidelity != MappingFidelity.None) result.Add(new(mapped.Range.Start, label, parts, kind, left, right));
        }
        private InlayHintLabelPart NodePart(Utf8String text, SyntaxNode node)
        {
            var source = SemanticSyntax.Source(node)!;
            var target = project(source);
            var range = target.ToRange(new Scanner(source.Source).SkipTriviaAt(node.Pos, inJSDoc: (node.Flags & NodeFlags.JSDoc) != 0), node.End, MappingFeature.InlayHints);
            return new(text, range.Fidelity is MappingFidelity.Exact or MappingFidelity.Atom
                ? new(DocumentUris.FromFileName(target.OriginalFileName), range.Range) : null);
        }
        private async ValueTask<IReadOnlyList<InlayHintLabelPart>> TypePartsAsync(Type? type, TypePredicate? predicate = null)
        {
            var syntax = await checker.GetInlayHintTypeSyntaxAsync(type, predicate, cancellation);
            return DisplayParts(syntax.Node, syntax.Symbols);
        }
        private static bool Module(Type type) => type.Symbol is { } symbol && (symbol.Flags & SymbolFlags.Module) != 0;
        private static bool ThisParameter(SyntaxNode node) => node is ParameterDeclarationNode { Name: IdentifierNode { Text: var text } } && text == "this"u8;
        private static SyntaxNode SkipParentheses(SyntaxNode node)
        { while (node is ParenthesizedExpressionNode expression) node = expression.Expression!; return node; }
        private static bool HintableDeclaration(SyntaxNode node) => node is not IInitializedNode { Initializer: { } initializer }
            || !(node is ParameterDeclarationNode || node is VariableDeclarationNode && (node.Parent!.Flags & NodeFlags.Const) != 0)
            || SkipParentheses(initializer) is var value && !HintableLiteral(value) && value.Kind is not (K.NewExpression or K.ObjectLiteralExpression or K.AsExpression or K.TypeAssertionExpression);
        private static bool Literal(SyntaxNode node) => node.Kind is >= K.FirstLiteralToken and <= K.LastLiteralToken;
        private static bool HintableLiteral(SyntaxNode node) => node switch
        {
            PrefixUnaryExpressionNode { Operand: { } operand } => Literal(operand) || operand is IdentifierNode && Infinity(Text(operand)),
            IdentifierNode name => name.Text == "undefined"u8 || Infinity(name.Text),
            _ => Literal(node) || node.Kind is K.TrueKeyword or K.FalseKeyword or K.NullKeyword or K.NoSubstitutionTemplateLiteral or K.TemplateExpression,
        };
        private static bool Infinity(Utf8String text) => text == "Infinity"u8 || text == "-Infinity"u8 || text == "NaN"u8;
        private static Utf8String Text(SyntaxNode node) => node switch
        {
            IdentifierNode n => n.Text, PrivateIdentifierNode n => n.Text, StringLiteralNode n => n.Text,
            NumericLiteralNode n => n.Text, BigIntLiteralNode n => n.Text, RegularExpressionLiteralNode n => n.Text,
            NoSubstitutionTemplateLiteralNode n => n.Text, TemplateHeadNode n => n.Text, TemplateMiddleNode n => n.Text, TemplateTailNode n => n.Text,
            _ => default,
        };
    }
}
