using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IAccessExpressionHost, IIndexedAccessValidationHost, IMemberAccessHost, IElementAccessErrorHost, IMemberAccessibilityHost, IPrivateAccessHost, IThisExpressionHost, IAccessNameHost
{
    internal AccessExpressions Access { get; }
    internal OptionalExpressions Optional { get; }
    internal AccessFlow AccessFlow { get; }
    internal IndexedAccessValidation IndexValidation { get; }
    internal MemberAccessRules MemberAccess { get; }
    internal ElementAccessErrors ElementErrors { get; }
    internal MemberAccessibility MemberAccessibility { get; }
    internal PrivateAccess PrivateAccess { get; }
    internal SymbolSuggestions SymbolSuggestions { get; }
    internal ThisExpressions ThisExpressions { get; }
    internal AccessNames AccessNames { get; }
    public bool NoImplicitThis => program.Symbols.Program.Configuration.Options.StrictOption("noImplicitThis");
    public bool LegacyDecorators => program.Symbols.Program.Configuration.Options.Boolean("experimentalDecorators") == true;

    public void ThisError(SyntaxNode node, int code, SyntaxNode? related = null) => Error(node, code);

    internal List<(SyntaxNode Node, Type Type, bool Suggestion)> DeferredMissingProperties { get; } = [];
    public bool StrictPropertyInitialization => program.Symbols.Program.Configuration.Options.StrictOption("strictPropertyInitialization");
    public bool NoPropertyAccessFromIndexSignature =>
        program.Symbols.Program.Configuration.Options.Boolean("noPropertyAccessFromIndexSignature") == true;
    public bool UseDefineForClassFields => program.Symbols.Program.Configuration.Options.Boolean("useDefineForClassFields")
        ?? program.Symbols.Program.Configuration.Options.EmitTargetYear >= 2022;

    public void AccessError(SyntaxNode node, int code, Type? type = null, Symbol? symbol = null) => Error(node, code);

    public void MemberError(SyntaxNode node, int code, Symbol symbol, Type? type = null) => Error(node, code);

    public ValueTask MarkPropertyAliasAsync(SyntaxNode node, Symbol? property, Type parentType, CancellationToken cancellation) =>
        AliasReferences.PropertyAsync(node, property, parentType, cancellation);

    public ValueTask<(Symbol? Property, Type? Result)> PrivatePropertyAsync(
        SyntaxNode node,
        Type leftType,
        Type apparentType,
        PrivateIdentifierNode name,
        bool anyLike,
        int assignment,
        CancellationToken cancellation)
            => PrivateAccess.ResolveAsync(node, leftType, apparentType, name, anyLike, assignment, cancellation);

    public ValueTask<bool> UncheckedJsAsync(SyntaxNode node, Symbol? symbol, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var file = SemanticSyntax.Source(node)!;
        if (program.Symbols.Program.Configuration.Options.Boolean("checkJs") is not null || file.CheckJsDirective is not null
            || file.ScriptKind is not (ScriptKind.JS or ScriptKind.JSX))
            return ValueTask.FromResult(false);
        var declarationFile = SemanticSyntax.Source(symbol?.Declarations.FirstOrDefault());
        if (declarationFile is not null && declarationFile != file && program.Symbols.Binding(declarationFile)?.IsModule != true)
            return ValueTask.FromResult(false);
        var declaration = symbol?.ValueDeclaration;
        bool exclude = declaration is null || !SemanticSyntax.ClassLike(declaration)
            || (declaration is ClassDeclarationNode c
                ? c.HeritageClauses
                : ((ClassExpressionNode)declaration).HeritageClauses)?.OfType<HeritageClauseNode>().Any(
                    h => h.Token == SyntaxKind.ExtendsKeyword && h.Types?.Count > 0) == true
            || declaration is IModifiedNode { Modifiers: { } modifiers } && modifiers.Any(m => m is DecoratorNode);
        return ValueTask.FromResult(!((symbol?.Flags & SymbolFlags.Class) != 0 && symbol is not null && exclude)
            && !(node is PropertyAccessExpressionNode { Expression.Kind: SyntaxKind.ThisKeyword } && exclude));
    }

    public async ValueTask<bool> JsLiteralAsync(Type type, CancellationToken cancellation)
    {
        if (NoImplicitAny)
            return false;
        if ((type.ObjectFlags & ObjectFlags.JSLiteral) != 0)
            return true;
        if (type is UnionOrIntersectionType composite)
        {
            bool union = type is UnionType;
            foreach (var part in composite.Types)
                if (await JsLiteralAsync(part, cancellation) != union)
                    return !union;
            return union;
        }
        if ((type.Flags & TypeFlags.Instantiable) != 0)
        {
            var constraint = await Instantiation.Constraints.ResolvedBaseConstraintAsync(type, cancellation);
            return constraint != type && await JsLiteralAsync(constraint, cancellation);
        }
        return false;
    }

    public ValueTask<bool> ExtendingInterfaceAsync(SyntaxNode node, CancellationToken cancellation)
            => DeclarationOrder.Ancestor(node, n => n is HeritageClauseNode) is null ? ValueTask.FromResult(false)
                : throw new InvalidOperationException("Checker requires heritage property diagnostics");

    public void MissingProperty(SyntaxNode node, Type type, bool suggestion) => DeferredMissingProperties.Add((node, type, suggestion));

    public async ValueTask PropertyDeprecatedAsync(Symbol property, SyntaxNode node, SyntaxNode errorNode, CancellationToken cancellation)
    {
        if (program.Deprecations.Symbol(property) && await program.Deprecations.UncalledAsync(node, property, FlowReferences, cancellation))
            program.Suggestion(errorNode, 6385, property.Name);
    }

    public ValueTask IndexDeprecatedAsync(IndexInfo index, SyntaxNode node, CancellationToken cancellation)
    {
        if (index.Declaration is { } declaration && program.Deprecations.Declaration(declaration))
            program.Suggestion(node, 6385, SyntaxNameText.Get(node));
        return ValueTask.CompletedTask;
    }

    public ValueTask PropertyBeforeDeclarationAsync(Symbol property, SyntaxNode node, SyntaxNode name, CancellationToken cancellation)
            => MemberAccess.BeforeDeclarationAsync(property, node, name, cancellation);

    public ValueTask MarkPropertyAsync(
        Symbol property,
        SyntaxNode node,
        SyntaxNode receiver,
        Symbol? parent,
        CancellationToken cancellation)
    {
        MemberAccess.MarkReferenced(property, node, receiver, parent, cancellation);
        return ValueTask.CompletedTask;
    }

    public async ValueTask AccessibilityAsync(
        SyntaxNode node,
        bool super,
        bool writing,
        Type type,
        Symbol property,
        CancellationToken cancellation)
            => await MemberAccessibility.CheckAsync(node, super, writing, type, property, cancellation: cancellation);

    public ValueTask<Type?> ContextualThisAsync(SyntaxNode node, CancellationToken cancellation)
        => FunctionThis.GetAsync(node, cancellation);

    public bool ClassInstanceProperty(SyntaxNode declaration)
    {
        if ((declaration.Flags & NodeFlags.JavaScriptFile) != 0 && declaration is BinaryExpressionNode binary)
        {
            var left = binary.Left!;
            var receiver = FlowReferences.Receiver(left);
            var name = receiver switch
            {
                PropertyAccessExpressionNode property => property.Name,
                ElementAccessExpressionNode element => element.ArgumentExpression,
                _ => null
            };
            bool access = left is PropertyAccessExpressionNode or ElementAccessExpressionNode && BindableStaticName(left, false);
            bool prototype = receiver is PropertyAccessExpressionNode or ElementAccessExpressionNode
                && BindableStaticName(receiver, false) && SyntaxNameText.Get(name) == "prototype";
            return (!access || !prototype) && !BindableStaticName(left, true);
        }
        return SemanticSyntax.ClassLike(declaration.Parent) && declaration is PropertyDeclarationNode
            && !SemanticSyntax.HasModifier(declaration, SyntaxKind.AccessorKeyword);
    }

    public ValueTask<bool> ReadonlyAssignmentAsync(SyntaxNode node, Symbol property, int assignment, CancellationToken cancellation)
            => ValueTask.FromResult(MemberAccess.ReadonlyAssignment(node, property, assignment, cancellation));

    public ValueTask<bool> AutoConstructorPropertyAsync(SyntaxNode node, Symbol property, CancellationToken cancellation)
            => MemberAccess.AutoConstructorAsync(node, property, cancellation);

    public ValueTask<SyntaxNode?> ConstructorPropertyAsync(Symbol symbol, CancellationToken cancellation)
    {
        var (kind, location) = ThisAssignment(symbol, cancellation);
        return ValueTask.FromResult(kind == ThisAssignmentKind.Constructor ? location : null);
    }

    public ValueTask<Type> AutoPropertyFlowAsync(SyntaxNode node, Symbol? property, CancellationToken cancellation)
            => PropertyInitializers.FlowAsync(node, property, cancellation: cancellation);

    public async ValueTask<bool> NumericForInAsync(SyntaxNode index, CancellationToken cancellation)
    {
        if (MemberAccessRules.SkipParentheses(index) is not IdentifierNode identifier)
            return false;
        var symbol = program.ReferenceSymbols.Resolve(identifier, cancellation);
        if ((symbol.Flags & SymbolFlags.Variable) == 0)
            return false;
        var child = index;
        for (var node = index.Parent; node is not null; child = node, node = node.Parent)
            if (node is ForInOrOfStatementNode { Kind: SyntaxKind.ForInStatement } loop && child == loop.Statement)
            {
                var declared = loop.Initializer is VariableDeclarationListNode { Declarations.Count: > 0 } list
                    && ((VariableDeclarationNode)list.Declarations[0]).Name is not BindingPatternNode ? program.Symbols.Declaration(list.Declarations[0])
                    : loop.Initializer is IdentifierNode reference ? program.ReferenceSymbols.Resolve(reference, cancellation) : null;
                if (declared == symbol
                    && await IndexesAsync(await ExpressionAsync(loop.Expression!, cancellation), cancellation) is [var info]
                    && info.KeyType == context.NumberType)
                    return true;
            }
        return false;
    }

    public ValueTask<Type> ValidateIndexAsync(Type type, SyntaxNode node, CancellationToken cancellation) =>
        IndexValidation.CheckAsync(type, node, cancellation);

    public ValueTask<Type?> ElementPropertyAsync(
        Symbol property,
        Type objectType,
        ElementAccessExpressionNode node,
        AccessFlags flags,
        CancellationToken cancellation)
            => Access.ElementPropertyAsync(property, objectType, node, flags, cancellation);

    public void ReadonlyIndex(IndexInfo? index, Type objectType, ElementAccessExpressionNode? node) =>
        Access.ReadonlyIndex(index, objectType, node);

    public ValueTask<Type?> MissingElementAsync(
        Type original,
        Type objectType,
        Type index,
        Type fullIndex,
        ElementAccessExpressionNode node,
        string? propertyName,
        AccessFlags flags,
        CancellationToken cancellation)
            => ElementErrors.MissingAsync(original, objectType, index, fullIndex, node, propertyName, flags, cancellation);

    public async ValueTask<bool> StaticPropertyAsync(string name, Type type, CancellationToken cancellation)
            => type.Symbol is { } symbol
                && await Properties.PropertyAsync(
                    await Values.GetAsync(symbol, cancellation),
                    name,
                    cancellation: cancellation) is { ValueDeclaration: { } declaration }
                && SemanticSyntax.IsStatic(declaration);

    public async ValueTask<string?> PropertySuggestionAsync(string name, Type type, CancellationToken cancellation)
            =>
                (await SymbolSuggestions.FindAsync(
                    name,
                    await Properties.GetAsync(type, cancellation),
                    SymbolFlags.Value,
                    cancellation))?.Name;

    public async ValueTask<string?> IndexSuggestionAsync(
        Type type,
        ElementAccessExpressionNode node,
        Type index,
        CancellationToken cancellation)
    {
        string name = ReferenceSyntax.AssignmentTarget(node) is not null ? "set" : "get";
        if (type is not ObjectType || await Properties.ObjectPropertyAsync(type, name, cancellation) is not { } property)
            return null;
        var signatures = await SignaturesAsync(await Values.GetAsync(property, cancellation), false, cancellation);
        if (signatures is [var signature] && await Parameters.MinimumAsync(signature, cancellation: cancellation) >= 1
            && await AssignableAsync(index, await Parameters.AtAsync(signature, 0, cancellation), cancellation))
            return name;
        return null;
    }

    public bool PlainJavaScript(SourceFileNode file) => file.ScriptKind is ScriptKind.JS or ScriptKind.JSX && file.CheckJsDirective is null
            && program.Symbols.Program.Configuration.Options.Boolean("checkJs") is null;

    public ValueTask PrivateEmitHelpersAsync(SyntaxNode node, bool read, bool write, CancellationToken cancellation)
            => program.Symbols.Program.Configuration.Options.Boolean("importHelpers") != true ? ValueTask.CompletedTask
                : throw new InvalidOperationException("Checker requires external private-field emit helpers");

    public async ValueTask<Type> LiteralNameTypeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (node is PrivateIdentifierNode)
            return context.NeverType;
        if (node is IdentifierNode identifier)
            return context.GetStringLiteralType(identifier.Text);
        if (node is StringLiteralNode text)
            return context.GetStringLiteralType(text.Text);
        if (node is NoSubstitutionTemplateLiteralNode template)
            return context.GetStringLiteralType(template.Text);
        if (node is NumericLiteralNode number)
            return await Algebra.RegularTypeAsync(await Expressions.CheckAsync(number, cancellation: cancellation), cancellation);
        if (node is ComputedPropertyNameNode computed)
            return await Algebra.RegularTypeAsync(await ObjectLiterals.ComputedAsync(computed, cancellation), cancellation);
        if (node is BigIntLiteralNode)
            return await Algebra.RegularTypeAsync(await Expressions.CheckAsync(node, cancellation: cancellation), cancellation);
        return context.NeverType;
    }

}
