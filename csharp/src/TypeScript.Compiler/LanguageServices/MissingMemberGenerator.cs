using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.LanguageServices;

[Flags]
internal enum PreserveOptional { Method = 1, Property = 2, All = Method | Property }

internal sealed class MissingMemberGenerator(Checker checker, EmitContext context, bool singleQuotes, bool noImplicitOverride, ImportAdder? imports = null)
{
    private NodeFactory F => context.Factory;
    private NodeBuilderFlags TypeFlags => NodeBuilderFlags.NoTruncation | (singleQuotes ? NodeBuilderFlags.UseSingleQuotesForStringLiteralType : 0);

    internal async ValueTask<IReadOnlyList<SyntaxNode>> CreateAsync(Symbol symbol, SyntaxNode enclosing, BlockNode body,
        PreserveOptional preserveOptional, bool abstractMember, CancellationToken cancellation)
    {
        var declarations = symbol.Declarations;
        var declaration = declarations.FirstOrDefault();
        bool signatureOnly = (enclosing.Flags & NodeFlags.Ambient) != 0 || abstractMember;
        bool optional = (symbol.Flags & SymbolFlags.Optional) != 0;
        SyntaxNode name = (symbol.CheckFlags & CheckFlags.Mapped) != 0
            && ((await checker.PropertyNameTypeAsync(symbol, cancellation)).Flags & Checking.TypeFlags.StringOrNumberLiteralOrUnique) != 0
            ? F.NewIdentifier(MappedMembers.PropertyName(await checker.PropertyNameTypeAsync(symbol, cancellation)))
            : declaration?.DeclarationName?.DeepClone<SyntaxNode>(F) ?? F.NewIdentifier(symbol.Name);
        var effective = MemberModifiers.FromSymbol(symbol);
        var flags = declaration is null ? ModifierFlags.None : effective & (ModifierFlags.Static | ModifierFlags.Public | ModifierFlags.Protected);
        if ((flags & ModifierFlags.Public) != 0) flags &= ~ModifierFlags.Protected;
        if (declaration is PropertyDeclarationNode && SemanticSyntax.HasModifier(declaration, K.AccessorKeyword)) flags |= ModifierFlags.Accessor;
        if (noImplicitOverride && declaration is not null && SemanticSyntax.HasModifier(declaration, K.AbstractKeyword)) flags |= ModifierFlags.Override;
        var modifiers = MemberModifiers.Create(F, flags);
        var type = await checker.GetWidenedMemberTypeAsync(symbol, enclosing, cancellation);
        var kind = declaration?.Kind ?? K.PropertySignature;
        SyntaxNode PropertyName()
        {
            if (name is IdentifierNode { Text: var text } && text == "constructor"u8)
                return F.NewComputedPropertyName(F.NewStringLiteral(text, singleQuotes ? TokenFlags.SingleQuote : 0));
            var clone = name.DeepClone<SyntaxNode>(F);
            foreach (var child in clone.DescendantsAndSelf()) { child.Pos = -1; child.End = -1; }
            return clone;
        }
        BlockNode? Body() => signatureOnly ? null : body.DeepClone<BlockNode>(F);
        if (kind is K.PropertySignature or K.PropertyDeclaration)
            return [F.NewPropertyDeclaration(modifiers, PropertyName(), optional && (preserveOptional & PreserveOptional.Property) != 0 ? F.NewToken(K.QuestionToken) : null,
                await TypeAsync(type, TypeFlags), null)];
        if (kind is K.GetAccessor or K.SetAccessor)
        {
            List<SyntaxNode> result = [];
            bool getter = false, setter = false;
            foreach (var accessor in declarations)
            {
                cancellation.ThrowIfCancellationRequested();
                if (accessor is GetAccessorDeclarationNode && !getter)
                {
                    getter = true;
                    result.Add(F.NewGetAccessorDeclaration(modifiers, PropertyName(), null, null,
                        await TypeAsync(type, TypeFlags), null, Body()));
                }
                else if (accessor is SetAccessorDeclarationNode set && !setter)
                {
                    setter = true;
                    var value = set.Parameters?.OfType<ParameterDeclarationNode>().FirstOrDefault(parameter => parameter.Name is not IdentifierNode id || id.Text != "this"u8)
                        ?? throw new InvalidOperationException("Expected set accessor to have a parameter");
                    var parameters = DummyParameters(1, [((IdentifierNode)value.Name!).Text],
                        [await TypeAsync(type, TypeFlags)], 1, (enclosing.Flags & NodeFlags.JavaScriptFile) != 0);
                    result.Add(F.NewSetAccessorDeclaration(modifiers, PropertyName(), null, parameters, null, null, Body()));
                }
            }
            return result;
        }
        if (kind is not (K.MethodSignature or K.MethodDeclaration)) return [];
        List<Signature> signatures = [];
        foreach (var part in type is UnionType union ? union.Types : [type]) signatures.AddRange(await checker.SignaturesAsync(part, false, cancellation));
        if (signatures.Count == 0) return [];
        bool optionalMethod = optional && (preserveOptional & PreserveOptional.Method) != 0;
        List<SyntaxNode> nodes = [];
        if (declarations.Length == 1)
        {
            if (await MethodAsync(signatures[0], Body()) is { } method) nodes.Add(method);
            return nodes;
        }
        foreach (var signature in signatures)
            if ((signature.Declaration?.Flags & NodeFlags.Ambient) != NodeFlags.Ambient && await MethodAsync(signature, null) is { } method) nodes.Add(method);
        if (signatureOnly) return nodes;
        if (declarations.Length > signatures.Count)
        {
            if (await MethodAsync(await checker.Signatures.FromDeclarationAsync(declarations[^1], cancellation), Body()) is { } method) nodes.Add(method);
        }
        else
        {
            var maximum = signatures[0];
            int minimum = maximum.MinArgumentCount;
            bool rest = false;
            foreach (var signature in signatures)
            {
                minimum = Math.Min(minimum, signature.MinArgumentCount);
                rest |= signature.HasRestParameter;
                if (signature.Parameters.Count >= maximum.Parameters.Count && (!signature.HasRestParameter || maximum.HasRestParameter)) maximum = signature;
            }
            int count = maximum.Parameters.Count - (maximum.HasRestParameter ? 1 : 0);
            var names = maximum.Parameters.Select(parameter => parameter.Name).ToArray();
            var parameters = DummyParameters(count, names, [], minimum, (enclosing.Flags & NodeFlags.JavaScriptFile) != 0).ToList();
            if (rest) parameters.Add(F.NewParameterDeclaration(null, F.NewToken(K.DotDotDotToken),
                F.NewIdentifier(count < names.Length && !names[count].IsEmpty ? names[count] : "rest"u8),
                count >= minimum ? F.NewToken(K.QuestionToken) : null, F.NewArrayTypeNode(F.NewKeywordTypeNode(K.UnknownKeyword)), null));
            List<Type> returns = [];
            foreach (var signature in signatures) returns.Add(await checker.GetReturnTypeOfSignatureAsync(signature, cancellation));
            var returnType = await TypeAsync(await checker.Algebra.UnionAsync(returns, cancellation: cancellation),
                NodeBuilderFlags.NoTruncation, NodeBuilderInternalFlags.AllowUnresolvedNames);
            nodes.Add(F.NewMethodDeclaration(modifiers, null, PropertyName(), optionalMethod ? F.NewToken(K.QuestionToken) : null,
                null, new([.. parameters]), returnType, null, Body()));
        }
        return nodes;

        async ValueTask<MethodDeclarationNode?> MethodAsync(Signature signature, BlockNode? methodBody)
        {
            Dictionary<SyntaxNode, Symbol>? symbols = imports is null ? null : [];
            var syntax = await checker.SignatureToDeclarationAsync(signature, K.MethodDeclaration, enclosing,
                TypeFlags | NodeBuilderFlags.SuppressAnyReturnType | NodeBuilderFlags.AllowEmptyTuple, cancellation, NodeBuilderInternalFlags.AllowUnresolvedNames, context, symbols);
            if (imports is not null) syntax = await imports.RewriteTypeAsync(syntax, symbols!, F);
            if (syntax is not MethodDeclarationNode method) return null;
            bool js = (enclosing.Flags & NodeFlags.JavaScriptFile) != 0;
            List<SyntaxNode> parameters = [];
            foreach (ParameterDeclarationNode parameter in method.Parameters ?? new([]))
                parameters.Add(F.NewParameterDeclaration(parameter.Modifiers, parameter.DotDotDotToken, parameter.Name,
                    js ? null : parameter.QuestionToken, parameter.Type, parameter.Initializer));
            return F.NewMethodDeclaration(modifiers, method.AsteriskToken, PropertyName(), optionalMethod ? F.NewToken(K.QuestionToken) : null,
                js ? null : method.TypeParameters, new([.. parameters]), js ? null : method.Type, method.FullSignature, methodBody);
        }

        async ValueTask<SyntaxNode?> TypeAsync(Type type, NodeBuilderFlags flags, NodeBuilderInternalFlags internalFlags = 0)
        {
            Dictionary<SyntaxNode, Symbol>? symbols = imports is null ? null : [];
            var node = await checker.TypeToTypeNodeAsync(type, enclosing, flags, cancellation, internalFlags, context, symbols);
            return imports is null ? node : await imports.RewriteTypeAsync(node, symbols!, F);
        }
    }

    private NodeList DummyParameters(int count, IReadOnlyList<Utf8String> names, IReadOnlyList<SyntaxNode?> types, int minimum, bool js)
    {
        List<SyntaxNode> parameters = [];
        Dictionary<Utf8String, int> occurrences = [];
        for (int i = 0; i < count; i++)
        {
            var name = i < names.Count && !names[i].IsEmpty ? names[i] : "arg"u8 + Utf8String.Format(i);
            int seen = occurrences.GetValueOrDefault(name); occurrences[name] = seen + 1;
            if (seen > 0) name += Utf8String.Format(seen);
            parameters.Add(F.NewParameterDeclaration(null, null, F.NewIdentifier(name), i >= minimum ? F.NewToken(K.QuestionToken) : null,
                js ? null : i < types.Count && types[i] is { } type ? type : F.NewKeywordTypeNode(K.UnknownKeyword), null));
        }
        return new([.. parameters]);
    }
}
