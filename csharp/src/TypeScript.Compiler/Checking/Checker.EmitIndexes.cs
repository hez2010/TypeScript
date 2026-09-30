using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private bool ReverseMappedPlaceholder(Symbol property, TypeSyntaxContext state)
    {
        if ((property.CheckFlags & Binding.CheckFlags.ReverseMapped) == 0)
            return false;
        var stack = state.ReverseMappedProperties;
        if (stack.Contains(property))
            return true;
        if (stack.Count != 0 && links.ReverseMappedSymbols.TryGet(stack[^1])?.PropertyType is { } parent
            && (parent.ObjectFlags & ObjectFlags.Anonymous) == 0)
            return true;
        const int inspectionDepth = 3;
        if (stack.Count < inspectionDepth || links.ReverseMappedSymbols.TryGet(property)?.MappedType?.Symbol is not { } mappedSymbol)
            return false;
        // The reference examines offsets zero through three, inclusive, once the stack has three entries.
        for (int i = 0; i < stack.Count && i <= inspectionDepth; i++)
            if (links.ReverseMappedSymbols.TryGet(stack[stack.Count - 1 - i])?.MappedType?.Symbol == mappedSymbol)
                return true;
        return false;
    }

    internal ValueTask<IReadOnlyList<Utf8String>> SerializeLateBoundIndexesForEmitAsync(SyntaxNode container, SyntaxNode? enclosing,
        NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation, CancellationToken cancellation = default,
        INodeBuilderSymbolTracker? tracker = null,
        NodeBuilderInternalFlags internalFlags = NodeBuilderInternalFlags.None) =>
        EmitSyntaxQueryAsync<IReadOnlyList<Utf8String>>(container, enclosing, flags, async state =>
        {
            var symbol = program.Symbols.Binding(container)?.Get(container)?.Symbol ?? program.Symbols.Declaration(container);
            if (symbol is null)
                return [];
            var staticInfos = await IndexesAsync(await Values.GetAsync(symbol, cancellation), cancellation);
            var members = await MembersAsync(symbol, cancellation);
            var instanceInfos = members.TryGetValue(Symbol.InternalIndex, out var indexSymbol)
                ? await IndexInfosAsync(indexSymbol, members.Values.ToArray(), cancellation) : [];
            var results = new List<Utf8String>();
            TypeSyntaxContext FreshContext() => new(enclosing, (flags & NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope) != 0,
                (flags & NodeBuilderFlags.UseOnlyExternalAliasing) != 0, flags, tracker, internalFlags);
            foreach (var (infos, isStatic) in new[] { (staticInfos, true), (instanceInfos, false) })
                foreach (var info in infos)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (info.Declaration is not null || info == Members.AnyBaseIndex)
                        continue;
                    bool computedNames = info.Components.Count != 0 && enclosing is not null;
                    foreach (var component in info.Components)
                    {
                        if (!computedNames)
                            break;
                        computedNames = SemanticSyntax.Name(component) is ComputedPropertyNameNode { Expression: { } expression }
                            && ConstantEvaluator.EntityName(expression)
                            && (await EntityNameVisibilityAsync(expression, enclosing!, cancellation, computeAliases: false)).Accessibility
                                == SymbolAccessibility.Accessible;
                    }
                    if (computedNames)
                    {
                        foreach (var component in info.Components)
                        {
                            if (await LateMembers.BindableAsync(component, cancellation))
                                continue;
                            state = FreshContext();
                            var componentSymbol = program.Symbols.Binding(component)?.Get(component)?.Symbol
                                ?? program.Symbols.Declaration(component);
                            var type = componentSymbol is null ? context.ErrorType : await Values.GetAsync(componentSymbol, cancellation);
                            var name = CloneSyntaxBindingName(SemanticSyntax.Name(component)!, state);
                            var postfix = component is PropertyDeclarationNode property ? property.PostfixToken
                                : component is PropertySignatureDeclarationNode signature ? signature.PostfixToken : null;
                            var node = state.Factory.NewPropertyDeclaration(IndexModifiers(info, isStatic, state), name,
                                postfix?.Kind == K.QuestionToken ? state.Factory.NewToken(K.QuestionToken) : null,
                                await TypeSyntaxAsync(type, state, cancellation), null);
                            results.Add(FinishTypeSyntax(state) ? PrintEmitSyntax(node, enclosing, state, cancellation) : Utf8String.Empty);
                        }
                        continue;
                    }
                    state = FreshContext();
                    var index = await IndexSignatureSyntaxAsync(info, state, cancellation, isStatic);
                    results.Add(FinishTypeSyntax(state) ? PrintEmitSyntax(index, enclosing, state, cancellation) : Utf8String.Empty);
                }
            return results;
        }, [], cancellation, tracker, internalFlags);

    private static NodeList? IndexModifiers(IndexInfo info, bool isStatic, TypeSyntaxContext state)
    {
        var modifiers = new List<SyntaxNode>();
        if (isStatic)
            modifiers.Add(state.Factory.NewToken(K.StaticKeyword));
        if (info.IsReadonly)
            modifiers.Add(state.Factory.NewToken(K.ReadonlyKeyword));
        return modifiers.Count == 0 ? null : new(modifiers.ToArray());
    }

    private async ValueTask<IReadOnlyList<SyntaxNode>> ObjectIndexSyntaxAsync(IndexInfo info, SyntaxNode? valueNode,
        TypeSyntaxContext state, CancellationToken cancellation)
    {
        if (state.Symbols.Enclosing is { } enclosing && info.Components.Count != 0)
        {
            bool reusable = true;
            foreach (var component in info.Components)
                if (SemanticSyntax.Name(component) is not ComputedPropertyNameNode { Expression: { } expression }
                    || !ConstantEvaluator.EntityName(expression)
                    || (await EntityNameVisibilityAsync(expression, enclosing, cancellation, computeAliases: false)).Accessibility
                        != SymbolAccessibility.Accessible)
                {
                    reusable = false;
                    break;
                }
            if (reusable)
            {
                var nodes = new List<SyntaxNode>();
                foreach (var component in info.Components)
                {
                    if (await LateMembers.BindableAsync(component, cancellation))
                        continue;
                    var computed = (ComputedPropertyNameNode)SemanticSyntax.Name(component)!;
                    TrackComputedName(computed.Expression!, state, true, cancellation);
                    TrackComputedName(computed.Expression!, state, false, cancellation);
                    var symbol = program.Symbols.Binding(component)?.Get(component)?.Symbol ?? program.Symbols.Declaration(component);
                    var typeNode = valueNode is null
                        ? await TypeSyntaxAsync(
                            symbol is null ? context.ErrorType : await Values.GetAsync(symbol, cancellation),
                            state,
                            cancellation)
                        : CloneSyntaxBindingName(valueNode, state);
                    var postfix = component is PropertyDeclarationNode property ? property.PostfixToken
                        : component is PropertySignatureDeclarationNode signature ? signature.PostfixToken : null;
                    nodes.Add(state.Factory.NewPropertySignatureDeclaration(IndexModifiers(info, false, state),
                        CloneSyntaxBindingName(SemanticSyntax.Name(component)!, state),
                        postfix is null ? null : state.Factory.NewToken(postfix.Kind),
                        typeNode, null));
                }
                return nodes;
            }
        }
        return [await IndexSignatureSyntaxAsync(info, state, cancellation, valueNode: valueNode)];
    }

    private async ValueTask<SyntaxNode> IndexSignatureSyntaxAsync(IndexInfo info, TypeSyntaxContext state, CancellationToken cancellation,
        bool isStatic = false, SyntaxNode? valueNode = null)
    {
        var f = state.Factory;
        Utf8String name = info.Declaration is IndexSignatureDeclarationNode { Parameters: { Count: > 0 } parameters }
            && SemanticSyntax.Name(parameters[0]) is IdentifierNode id ? id.Text : Utf8Literals.X;
        var parameter = f.NewParameterDeclaration(null, null, f.NewIdentifier(name), null,
            await TypeSyntaxAsync(info.KeyType, state, cancellation), null);
        var value = valueNode ?? await TypeSyntaxAsync(info.ValueType, state, cancellation);
        state.Length.Add(name, 4 + (info.IsReadonly ? 9 : 0));
        return f.NewIndexSignatureDeclaration(IndexModifiers(info, isStatic, state), new([parameter]), value);
    }
}
