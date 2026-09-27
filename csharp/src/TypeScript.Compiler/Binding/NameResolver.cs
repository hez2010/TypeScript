using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using static TypeScript.Compiler.Binding.SemanticSyntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Binding;

public delegate Symbol? ResolveName(SyntaxNode? location, TextSlice name, SymbolFlags meaning,
    DiagnosticMessage? nameNotFoundMessage, bool isUse, bool excludeGlobals);

/// <summary>Lexical lookup over bound syntax. Checker hooks supply merged symbols, alias meanings and semantic diagnostics.</summary>
public sealed class NameResolver(CompilerOptions options, Func<SyntaxNode, BoundSourceFile?> getBinding)
{
    public IReadOnlyDictionary<TextSlice, Symbol>? Globals { get; init; }
    public Symbol? ArgumentsSymbol { get; set; }
    public Symbol? RequireSymbol { get; init; }
    public Func<SyntaxNode, Symbol?>? GetSymbolOfDeclaration { get; init; }
    public Func<IReadOnlyDictionary<TextSlice, Symbol>?, TextSlice, S, Symbol?>? Lookup { get; init; }
    public Action<SyntaxNode?, DiagnosticMessage, TextSlice[]>? Error { get; init; }
    public Action<Symbol, S>? SymbolReferenced { get; init; }
    public Func<SyntaxNode, bool?>? GetRequiresScopeChangeCache { get; init; }
    public Action<SyntaxNode, bool>? SetRequiresScopeChangeCache { get; init; }
    public Func<SyntaxNode?, TextSlice, SyntaxNode, Symbol?, bool>? OnPropertyWithInvalidInitializer { get; init; }
    public Action<SyntaxNode?, TextSlice, S, DiagnosticMessage>? OnFailedToResolveSymbol { get; init; }
    public Action<SyntaxNode?, Symbol, S, SyntaxNode?, SyntaxNode?, bool>? OnSuccessfullyResolvedSymbol { get; init; }
    public CancellationToken Cancellation { get; init; }

    private NodeBinding? Data(SyntaxNode? node) => node is null ? null : getBinding(node)?.Get(node);

    private Symbol? SymbolOf(SyntaxNode node) => GetSymbolOfDeclaration is { } get ? get(node) : Data(node)?.Symbol;

    private Symbol BoundTypeSymbol(SyntaxNode node) => SymbolOf(node)
            ?? throw new InvalidOperationException("Class or interface declaration has no bound symbol");

    private static TextSlice Key(TextSlice name) =>
        name.Span.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal) ? TextSlice.Concat(Symbol.InternalPrefix, name) : name;

    private static TextSlice UserName(Symbol symbol) => symbol.Name.Span.StartsWith(
        Symbol.InternalPrefix + Symbol.InternalPrefix,
        StringComparison.Ordinal)
            ? symbol.Name[1..] : symbol.Name;

    private Symbol? Find(IReadOnlyDictionary<TextSlice, Symbol>? table, TextSlice name, S meaning)
    {
        if (Lookup is { } lookup)
            return lookup(table, name, meaning);
        var symbol = meaning == 0 ? null : table?.GetValueOrDefault(Key(name));
        return symbol is not null && (symbol.Flags & meaning) != 0 ? symbol : null;
    }

    public Symbol? Resolve(SyntaxNode? location, TextSlice name, S meaning, DiagnosticMessage? nameNotFoundMessage = null,
        bool isUse = false, bool excludeGlobals = false)
    {
        Cancellation.ThrowIfCancellationRequested();
        Symbol? result = null;
        SyntaxNode? last = null, selfReference = null, invalidInitializer = null, associatedDeclaration = null;
        bool deferred = false;
        var original = location;
        while (location is not null)
        {
            Cancellation.ThrowIfCancellationRequested();
            if (name == "const" && ConstAssertion(location))
                return null;
            if (location.Kind is K.ModuleDeclaration or K.EnumDeclaration && last is not null && Name(location) == last)
            {
                last = location;
                location = location.Parent;
                if (location is null)
                    break;
            }
            bool attributes = location is ModuleDeclarationNode { Attributes: { } a } && last == a;
            var locals = Data(location)?.Locals;
            if (locals is not null && !(location is SourceFileNode && getBinding(location)?.IsModule != true))
            {
                result = Find(locals, name, meaning);
                if (result is not null)
                {
                    bool use = true;
                    if (attributes)
                        use = false;
                    else if (location is IFunctionSignature && last is not null && last != Body(location))
                    {
                        if ((meaning & result.Flags & S.Type) != 0 && last.Kind != K.JSDoc)
                            use = (result.Flags & S.TypeParameter) != 0 && ((last.Flags & NodeFlags.Synthesized) != 0
                                || last == (location as ITypedNode)?.Type
                                || last.Kind is K.Parameter or K.JSDocParameterTag or K.JSDocReturnTag or K.TypeParameter);
                        if ((meaning & result.Flags & S.Variable) != 0)
                        {
                            if (UseOuterVariableScope(result, location, last))
                                use = false;
                            else if ((result.Flags & S.FunctionScopedVariable) != 0)
                                use = last is ParameterDeclarationNode || (last.Flags & NodeFlags.Synthesized) != 0
                                    || last == (location as ITypedNode)?.Type && ParameterAncestor(result.ValueDeclaration);
                        }
                    }
                    else if (location is ConditionalTypeNode conditional)
                        use = last == conditional.TrueType;
                    if (use)
                        break;
                    result = null;
                }
            }
            deferred |= DeferredContext(location, last);
            switch (location.Kind)
            {
                case K.SourceFile:
                case K.ModuleDeclaration:
                    if (location is SourceFileNode && getBinding(location)?.IsModule != true || attributes)
                        break;
                    var module = SymbolOf(location);
                    if (module is null)
                        break;
                    var exports = module.Exports;
                    if (location is SourceFileNode || location is ModuleDeclarationNode { Keyword: not K.GlobalKeyword }
                        && (location.Flags & NodeFlags.Ambient) != 0)
                    {
                        result = exports.GetValueOrDefault("default");
                        if (result is not null)
                        {
                            var local = GetLocalSymbolForExportDefault(result);
                            if (local is not null && (result.Flags & meaning) != 0 && UserName(local) == name)
                                goto Resolved;
                            result = null;
                        }
                        var exported = exports.GetValueOrDefault(Key(name));
                        if (exported is { Flags: S.Alias }
                            && exported.Declarations.Any(d => d.Kind is K.ExportSpecifier or K.NamespaceExport))
                            break;
                    }
                    if (name != "default" && (result = Find(exports, name, meaning & S.ModuleMember)) is not null)
                    {
                        if (location is SourceFileNode
                            && getBinding(location)?.CommonJSModuleIndicator is not null
                            && (result.Flags & S.Type) == 0)
                            result = null;
                        else
                            goto Resolved;
                    }
                    break;
                case K.EnumDeclaration:
                    var enumeration = SymbolOf(location);
                    if (enumeration is null)
                        break;
                    result = Find(enumeration.Exports, name, meaning & S.EnumMember);
                    if (result is not null)
                    {
                        if (nameNotFoundMessage is not null
                            && (options.Boolean("isolatedModules") == true || options.Boolean("verbatimModuleSyntax") == true)
                            && (location.Flags & NodeFlags.Ambient) == 0 && Source(location) != Source(result.ValueDeclaration))
                            Error?.Invoke(
                                original,
                                Messages.Cannot_access_0_from_another_file_without_qualification_when_1_is_enabled_Use_2_instead,
                                [
                                        name,
                                        options.Boolean("verbatimModuleSyntax") == true ? "verbatimModuleSyntax" : "isolatedModules",
                                        TextSlice.Concat(UserName(enumeration), ".", name)
                                    ]);
                        goto Resolved;
                    }
                    break;
                case K.PropertyDeclaration:
                    if (!IsStatic(location) && location.Parent is { } parent)
                    {
                        var constructor = Enumerable.Range(0, parent.ChildCount).Select(parent.GetChild)
                            .FirstOrDefault(n => n is ConstructorDeclarationNode { Body: { } body } && body.Pos != body.End);
                        if (constructor is not null && Find(Data(constructor)?.Locals, name, meaning & S.Value) is not null)
                            invalidInitializer = location;
                    }
                    break;
                case K.ClassDeclaration:
                case K.ClassExpression:
                case K.InterfaceDeclaration:
                    result = Find(BoundTypeSymbol(location).Members, name, meaning & S.Type);
                    if (result is not null)
                    {
                        if (!result.Declarations.Any(d => d is TypeParameterDeclarationNode && d.Parent == location))
                        {
                            result = null;
                            break;
                        }
                        if (last is not null && IsStatic(last))
                        {
                            if (nameNotFoundMessage is not null)
                                Error?.Invoke(original, Messages.Static_members_cannot_reference_class_type_parameters, []);
                            return null;
                        }
                        goto Resolved;
                    }
                    if (location is ClassExpressionNode { Name: IdentifierNode className }
                        && (meaning & S.Class) != 0
                        && name == className.Text)
                    {
                        result = Data(location)?.Symbol;
                        goto Resolved;
                    }
                    break;
                case K.ExpressionWithTypeArguments:
                    if (location is ExpressionWithTypeArgumentsNode expression && last == expression.Expression
                        && location.Parent is HeritageClauseNode { Token: K.ExtendsKeyword, Parent: { } container } && ClassLike(container)
                        && Find(BoundTypeSymbol(container).Members, name, meaning & S.Type) is not null)
                    {
                        if (nameNotFoundMessage is not null)
                            Error?.Invoke(original, Messages.Base_class_expressions_cannot_reference_class_type_parameters, []);
                        return null;
                    }
                    break;
                case K.ComputedPropertyName:
                    var grandparent = location.Parent?.Parent;
                    if ((ClassLike(grandparent) || grandparent is InterfaceDeclarationNode)
                        && Find(BoundTypeSymbol(grandparent!).Members, name, meaning & S.Type) is not null)
                    {
                        if (nameNotFoundMessage is not null)
                            Error?.Invoke(
                                original,
                                Messages.A_computed_property_name_cannot_reference_a_type_parameter_from_its_containing_type,
                                []);
                        return null;
                    }
                    break;
                case K.MethodDeclaration:
                case K.Constructor:
                case K.GetAccessor:
                case K.SetAccessor:
                case K.FunctionDeclaration:
                case K.FunctionExpression:
                    if ((meaning & S.Variable) != 0 && name == "arguments")
                    {
                        result = ArgumentsSymbol ??= new(S.Property | S.Transient, "arguments");
                        goto Resolved;
                    }
                    if (location is FunctionExpressionNode { Name: IdentifierNode functionName }
                        && (meaning & S.Function) != 0
                        && name == functionName.Text)
                    {
                        result = Data(location)?.Symbol;
                        goto Resolved;
                    }
                    break;
                case K.Decorator:
                    if (location.Parent is ParameterDeclarationNode)
                        location = location.Parent;
                    if (ClassElement(location.Parent) || location.Parent is ClassDeclarationNode)
                        location = location.Parent!;
                    break;
                case K.Parameter:
                case K.BindingElement:
                    if (last is not null
                        && (last == (location as IInitializedNode)?.Initializer || last == Name(location) && last is BindingPatternNode)
                        && (location is ParameterDeclarationNode || RootDeclaration(location) is ParameterDeclarationNode))
                        associatedDeclaration ??= location;
                    break;
                case K.InferType:
                    if ((meaning & S.TypeParameter) != 0 && location is InferTypeNode { TypeParameter: { } parameter }
                        && Name(parameter) is IdentifierNode identifier && name == identifier.Text)
                    {
                        result = Data(parameter)?.Symbol;
                        goto Resolved;
                    }
                    break;
                case K.ExportSpecifier:
                    if (location is ExportSpecifierNode specifier && last is not null && last == specifier.PropertyName
                        && location.Parent?.Parent is ExportDeclarationNode { ModuleSpecifier: not null } export)
                        location = export.Parent ?? throw new InvalidOperationException("Export declaration has no source");
                    break;
            }
            if (SelfReference(location, last))
                selfReference = location;
            last = location;
            location = location.Parent;
        }
    Resolved:
        if (isUse && result is not null && (selfReference is null || result != Data(selfReference)?.Symbol))
            SymbolReferenced?.Invoke(result, meaning);
        if (result is null && !excludeGlobals)
            result = Find(Globals, name, meaning | S.GlobalLookup);
        if (result is null && original is not null && (original.Flags & NodeFlags.JavaScriptFile) != 0 && RequireCall(original.Parent))
            return RequireSymbol;
        if (nameNotFoundMessage is not null)
        {
            if (invalidInitializer is not null
                && OnPropertyWithInvalidInitializer?.Invoke(original, name, invalidInitializer, result) == true)
                return null;
            if (result is null)
                OnFailedToResolveSymbol?.Invoke(original, name, meaning, nameNotFoundMessage);
            else
                OnSuccessfullyResolvedSymbol?.Invoke(original, result, meaning, last, associatedDeclaration, deferred);
        }
        return result;
    }

    public Symbol? GetLocalSymbolForExportDefault(Symbol? symbol)
    {
        if (symbol is null || symbol.Declarations.Count == 0 || !HasModifier(symbol.Declarations[0], K.DefaultKeyword))
            return null;
        foreach (var declaration in symbol.Declarations)
            if (Data(declaration)?.LocalSymbol is { } local)
                return local;
        return null;
    }

    private bool UseOuterVariableScope(Symbol symbol, SyntaxNode location, SyntaxNode last)
    {
        if (last is ParameterDeclarationNode && Body(location) is { } body && symbol.ValueDeclaration is { } value
            && value.Pos >= body.Pos && value.End <= body.End)
        {
            bool? cached = GetRequiresScopeChangeCache?.Invoke(location);
            if (cached is null)
            {
                cached = location is IFunctionSignature { Parameters: { } parameters } && parameters.Any(RequiresScopeChange);
                SetRequiresScopeChangeCache?.Invoke(location, cached.Value);
            }
            return cached != true;
        }
        return false;
    }

    private int Target => options.String("target")?.ToLowerInvariant() switch
    {
        "es3" => 3,
        "es5" => 5,
        "es6" or "es2015" => 2015,
        { } text when text.StartsWith("es", StringComparison.Ordinal) && int.TryParse(text.AsSpan(2), out int year) => year,
        _ => int.MaxValue
    };

    private bool RequiresScopeChange(SyntaxNode parameter)
    {
        var pending = new Stack<SyntaxNode>();
        if (Name(parameter) is { } name)
            pending.Push(name);
        if (parameter is IInitializedNode { Initializer: { } initializer })
            pending.Push(initializer);
        while (pending.TryPop(out var node))
        {
            Cancellation.ThrowIfCancellationRequested();
            switch (node.Kind)
            {
                case K.ArrowFunction:
                case K.FunctionExpression:
                case K.FunctionDeclaration:
                case K.Constructor:
                    continue;
                case K.MethodDeclaration:
                case K.GetAccessor:
                case K.SetAccessor:
                case K.PropertyAssignment:
                    if (Name(node) is { } memberName)
                        pending.Push(memberName);
                    continue;
                case K.PropertyDeclaration:
                    if (HasModifier(node, K.StaticKeyword))
                    {
                        if (!(options.Boolean("useDefineForClassFields") != false && Target >= 2022))
                            return true;
                    }
                    else if (Name(node) is { } propertyName)
                        pending.Push(propertyName);
                    continue;
            }
            if (node is BinaryExpressionNode { OperatorToken.Kind: K.QuestionQuestionToken } || (node.Flags & NodeFlags.OptionalChain) != 0)
            {
                if (Target < 2020)
                    return true;
                continue;
            }
            if (node is BindingElementNode { DotDotDotToken: not null, Parent.Kind: K.ObjectBindingPattern })
            {
                if (Target < 2017)
                    return true;
                continue;
            }
            if (TypeNode(node))
                continue;
            for (int i = node.ChildCount - 1; i >= 0; i--)
                pending.Push(node.GetChild(i));
        }
        return false;
    }

    private static bool ParameterAncestor(SyntaxNode? node)
    {
        for (; node is not null; node = node.Parent)
            if (node is ParameterDeclarationNode)
                return true;
        return false;
    }

    private static bool DeferredContext(SyntaxNode location, SyntaxNode? last)
    {
        if (location.Kind is not (K.ArrowFunction or K.FunctionExpression))
            return location is TypeQueryNode || (FunctionDeclarationLike(location)
                || location is PropertyDeclarationNode && !IsStatic(location))
                && (last is null || last != Name(location));
        if (last is not null && last == Name(location))
            return false;
        return Generator(location) || HasModifier(location, K.AsyncKeyword) || !ImmediatelyInvoked(location);
    }

    private static bool SelfReference(SyntaxNode node, SyntaxNode? last) => node.Kind switch
    {
        K.Parameter => last is not null && last == Name(node),
        K.FunctionDeclaration or K.ClassDeclaration or K.InterfaceDeclaration or K.EnumDeclaration or K.TypeAliasDeclaration
            or K.JSTypeAliasDeclaration or K.ModuleDeclaration => true,
        _ => false
    };
}
