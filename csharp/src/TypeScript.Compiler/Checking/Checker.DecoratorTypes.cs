using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly Dictionary<SyntaxNode, Signature?> decoratorSignatures = [];
    private readonly Dictionary<TextSlice, Type> decoratorGlobals = [];
    private readonly Dictionary<(Type Name, bool Private, bool Static), Type> decoratorContextOverrides = [];

    private async ValueTask<Signature?> DecoratorSignatureAsync(DecoratorNode decorator, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var node = decorator.Parent!;
        if (decoratorSignatures.TryGetValue(node, out var cached))
            return cached;
        decoratorSignatures.Add(node, null);
        try
        {
            var signature = await CreateDecoratorSignatureAsync(node, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return decoratorSignatures[node] = signature;
        }
        catch
        {
            decoratorSignatures.Remove(node);
            throw;
        }
    }

    private async ValueTask<Signature?> CreateDecoratorSignatureAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (SemanticSyntax.ClassLike(node))
        {
            var target = await Values.GetAsync(program.Symbols.Declaration(node)!, cancellation);
            var result = await Algebra.UnionAsync([target, context.VoidType], cancellation: cancellation);
            return DecoratorCall(result, LegacyDecorators ? [DecoratorParameter("target", target)]
                :
                    [
                        DecoratorParameter("target", target),
                        DecoratorParameter("context", await DecoratorGlobalAsync("ClassDecoratorContext", [target], cancellation))
                    ]);
        }
        if (LegacyDecorators && node is ParameterDeclarationNode parameter)
        {
            var owner = parameter.Parent!;
            if (owner is not (ConstructorDeclarationNode or MethodDeclarationNode or SetAccessorDeclarationNode)
                || !SemanticSyntax.ClassLike(owner.Parent) || parameter.Name is IdentifierNode { Text.Span: "this" })
                return null;
            var parameters = ((IFunctionSignature)owner).Parameters!;
            int index = parameters.IndexOf(parameter) - (parameters[0] is ParameterDeclarationNode { Name: IdentifierNode { Text.Span: "this" } }
                ? 1
                : 0);
            var target = owner is ConstructorDeclarationNode ? await Values.GetAsync(
                program.Symbols.Declaration(owner.Parent!)!,
                cancellation)
                : await DecoratorReceiverAsync(owner, cancellation);
            var key = owner is ConstructorDeclarationNode ? context.UndefinedType : await LegacyDecoratorKeyAsync(owner, cancellation);
            return DecoratorCall(context.VoidType, [DecoratorParameter("target", target), DecoratorParameter("propertyKey", key),
                DecoratorParameter("parameterIndex", context.GetNumberLiteralType(index))]);
        }
        if (node is not (MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode or PropertyDeclarationNode)
            || !SemanticSyntax.ClassLike(node.Parent))
            return null;
        var receiver = await DecoratorReceiverAsync(node, cancellation);
        var value = await Values.GetAsync(program.Symbols.Declaration(node)!, cancellation);
        bool accessor = SemanticSyntax.HasModifier(node, SyntaxKind.AccessorKeyword);
        if (LegacyDecorators)
        {
            var parameters = new List<Symbol>
            {
                DecoratorParameter("target", receiver),
                DecoratorParameter("propertyKey", await LegacyDecoratorKeyAsync(node, cancellation))
            };
            Type result = context.VoidType;
            if (node is not PropertyDeclarationNode || accessor)
            {
                var descriptor = await DecoratorGlobalAsync("TypedPropertyDescriptor", [value], cancellation);
                parameters.Add(DecoratorParameter("descriptor", descriptor));
                if (node is not PropertyDeclarationNode)
                    result = await Algebra.UnionAsync([descriptor, context.VoidType], cancellation: cancellation);
            }
            return DecoratorCall(result, parameters.ToArray());
        }
        if (node is MethodDeclarationNode)
            value = DecoratorFunction(await Signatures.FromDeclarationAsync(node, cancellation));
        Type targetType, returnType;
        if (node is PropertyDeclarationNode)
        {
            targetType = accessor
                ? await DecoratorGlobalAsync("ClassAccessorDecoratorTarget", [receiver, value], cancellation)
                : context.UndefinedType;
            returnType = accessor ? await DecoratorGlobalAsync("ClassAccessorDecoratorResult", [receiver, value], cancellation)
                : DecoratorFunction(DecoratorCall(value, [DecoratorParameter("value", value)], DecoratorParameter("this", receiver)));
        }
        else
        {
            targetType = node is GetAccessorDeclarationNode ? DecoratorFunction(DecoratorCall(value, []))
                : node is SetAccessorDeclarationNode
                    ? DecoratorFunction(DecoratorCall(context.VoidType, [DecoratorParameter("value", value)]))
                    : value;
            returnType = targetType;
        }
        TextSlice contextName = node switch
        {
            MethodDeclarationNode => "ClassMethodDecoratorContext",
            GetAccessorDeclarationNode => "ClassGetterDecoratorContext",
            SetAccessorDeclarationNode => "ClassSetterDecoratorContext",
            _ => accessor ? "ClassAccessorDecoratorContext" : "ClassFieldDecoratorContext"
        };
        var contextType = await DecoratorGlobalAsync(contextName, [receiver, value], cancellation);
        var name = SemanticSyntax.Name(node)!;
        bool privateName = name is PrivateIdentifierNode, isStatic = SemanticSyntax.IsStatic(node);
        var nameType = privateName
            ? context.GetStringLiteralType(((PrivateIdentifierNode)name).Text)
            : await LiteralNameTypeAsync(name, cancellation);
        if (!decoratorContextOverrides.TryGetValue((nameType, privateName, isStatic), out var overrides))
        {
            var members = new Dictionary<TextSlice, Symbol>();
            foreach (var (key, type) in new (TextSlice, Type)[]
            {
                ("name", nameType),
                ("private", privateName ? context.TrueType : context.FalseType),
                ("static", isStatic ? context.TrueType : context.FalseType)
            })
            {
                var property = new Symbol(SymbolFlags.Property | SymbolFlags.Transient, key);
                links.Values.Get(property).ResolvedType = type;
                members.Add(key, property);
            }
            var objectType = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved);
            objectType.Members = members.AsReadOnly();
            objectType.Properties = members.Values.ToArray();
            objectType.CallSignatures = objectType.ConstructSignatures = [];
            objectType.IndexInfos = [];
            overrides = decoratorContextOverrides[(nameType, privateName, isStatic)] = objectType;
        }
        contextType = await Algebra.IntersectionAsync([contextType, overrides], cancellation: cancellation);
        return DecoratorCall(await Algebra.UnionAsync([returnType, context.VoidType], cancellation: cancellation),
            [DecoratorParameter("target", targetType), DecoratorParameter("context", contextType)]);
    }

    private async ValueTask<Type> DecoratorGlobalAsync(TextSlice name, Type[] arguments, CancellationToken cancellation)
    {
        if (!decoratorGlobals.TryGetValue(name, out var target))
            decoratorGlobals[name] = target = await program.Globals.GetAsync(name, arguments.Length, true, cancellation);
        return target == context.EmptyGenericType ? name == "TypedPropertyDescriptor" ? context.EmptyObjectType : context.UnknownType
            : context.CreateTypeReference((InterfaceType)target, arguments);
    }

    private async ValueTask<Type> DecoratorReceiverAsync(SyntaxNode node, CancellationToken cancellation)
        => SemanticSyntax.IsStatic(node) ? await Values.GetAsync(program.Symbols.Declaration(node.Parent!)!, cancellation)
            : await Declared.GetAsync(program.Symbols.Declaration(node.Parent!)!, cancellation);

    private async ValueTask<Type> LegacyDecoratorKeyAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var name = SemanticSyntax.Name(node)!;
        if (name is IdentifierNode or StringLiteralNode or NumericLiteralNode)
            return context.GetStringLiteralType(SyntaxNameText.Get(name)!);
        if (name is ComputedPropertyNameNode computed)
        {
            var type = await ObjectLiterals.ComputedAsync(computed, cancellation);
            return await AssignableKindAsync(type, TypeFlags.ESSymbolLike, cancellation) ? type : context.StringType;
        }
        return context.ErrorType;
    }

    private Symbol DecoratorParameter(TextSlice name, Type type)
    {
        var parameter = new Symbol(SymbolFlags.FunctionScopedVariable | SymbolFlags.Transient, name);
        links.Values.Get(parameter).ResolvedType = type;
        return parameter;
    }

    private Signature DecoratorCall(Type result, Symbol[] parameters, Symbol? receiver = null)
        => context.NewSignature(0, null, [], receiver, parameters, result, null, parameters.Length);

    private Type DecoratorFunction(Signature signature)
    {
        if (signature.IsolatedSignatureType is { } cached)
            return cached;
        var type = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved);
        type.Members = new Dictionary<TextSlice, Symbol>().AsReadOnly();
        type.Properties = [];
        type.CallSignatures = [signature];
        type.ConstructSignatures = [];
        type.IndexInfos = [];
        return signature.IsolatedSignatureType = type;
    }
}
