using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private Type? importCallOptionsType;

    private static bool IsImportCall(CallExpressionNode node) => node.Expression?.Kind == SyntaxKind.ImportKeyword
        || node.Expression is MetaPropertyNode { KeywordToken: SyntaxKind.ImportKeyword, Name.Text: "defer" };

    private async ValueTask<Type> CheckImportCallAsync(CallExpressionNode node, CancellationToken cancellation)
    {
        ImportCallGrammar(node);
        Type result = context.AnyType;
        if (node.Arguments is { Count: > 0 } arguments)
        {
            var specifier = arguments[0];
            var specifierType = await CachedExpressionAsync(specifier, 0, cancellation);
            Type? options = arguments.Count > 1 ? await CachedExpressionAsync(arguments[1], 0, cancellation) : null;
            for (int i = 2; i < arguments.Count; i++)
                await CachedExpressionAsync(arguments[i], 0, cancellation);
            if ((specifierType.Flags & TypeFlags.Nullable) != 0 || !await AssignableAsync(specifierType, context.StringType, cancellation))
                Error(specifier, 7036, await TypeDisplay.GetAsync(specifierType, cancellation));
            Type? attributes = null;
            if (options is not null)
            {
                importCallOptionsType ??= await program.Globals.GetAsync("ImportCallOptions", 0, true, cancellation);
                if (importCallOptionsType != context.EmptyObjectType)
                    await RelationDiagnostics.CheckAsync(options,
                        await Algebra.UnionAsync([importCallOptionsType, context.UndefinedType], cancellation: cancellation),
                        RelationKind.Assignable, arguments[1], null, 2322, cancellation);
                if (arguments[1] is ObjectLiteralExpressionNode literal
                    && literal.Properties!.OfType<PropertyAssignmentNode>().FirstOrDefault(p => p.Name is IdentifierNode { Text: "assert" }) is { } assertion)
                    Error(assertion.Name!, 2880);
                if (await Properties.PropertyAsync(options, "with", cancellation: cancellation) is { } property)
                    attributes = await Values.GetAsync(property, cancellation);
            }
            var module = await ResolveImportModuleAsync(node, specifier, attributes, cancellation);
            if (module is not null && await program.AliasTargets.ExternalModuleAsync(module, true, cancellation) is { } target)
            {
                var type = await Values.GetAsync(target, cancellation);
                if (DefaultOnlyModule(module, specifier) && type != context.ErrorType)
                {
                    if (!syntheticModuleTypes.TryGetValue((type, true), out result!))
                        syntheticModuleTypes[(type, true)] = result = await DefaultWrapperAsync(target, module, null, cancellation);
                }
                else
                    result = await SyntheticModuleTypeAsync(type, target, module, specifier, cancellation);
            }
        }
        var promise = await PromiseResultAsync(node, result, false, cancellation);
        if (promise == context.UnknownType)
        {
            Error(node, 2711);
            return context.ErrorType;
        }
        if (program.Symbols.Lookup(program.Symbols.Globals, "Promise", SymbolFlags.Value) is null)
        {
            program.Error(null, TypeScript.Compiler.Diagnostics.Messages.Cannot_find_global_value_0, "Promise", "es2015");
            Error(node, 2712);
        }
        return promise;
    }

    private void ImportCallGrammar(CallExpressionNode node)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        if (program.Symbols.Program.Configuration.Options.Boolean("verbatimModuleSyntax") == true && ModuleKind == 1)
        {
            Error(node, VerbatimModuleCode(node));
            return;
        }
        if (node.Expression is MetaPropertyNode && ModuleKind is not (99 or 200))
        {
            Error(node, 18060);
            return;
        }
        if (node.Expression is not MetaPropertyNode && ModuleKind == 5)
        {
            Error(node, 1323);
            return;
        }
        if (node.TypeArguments is not null)
        {
            Error(node, 1326);
            return;
        }
        var arguments = node.Arguments!;
        if (ModuleKind is not (>= 100 and <= 199 or 99 or 200))
        {
            if (arguments.HasTrailingComma)
                TrailingCommaError(node, arguments);
            if (arguments.Count > 1)
            {
                Error(arguments[1], 1324);
                return;
            }
        }
        if (arguments.Count is 0 or > 2)
        {
            Error(node, 1450);
            return;
        }
        if (arguments.FirstOrDefault(a => a is SpreadElementNode) is { } spread)
            Error(spread, 1325);
    }

    private async ValueTask<Type> ResolveImportTypeAsync(ImportTypeNode node, CancellationToken cancellation)
    {
        var data = links.TypeNodes.Get(node);
        if (data.ResolvedType is { } cached)
            return cached;
        if (node.Argument is not LiteralTypeNode { Literal: StringLiteralNode literal })
        {
            if (node.Argument!.Pos == node.Argument.End)
                ErrorOnFirstToken(node.Argument, 1141);
            else
                Error(node.Argument, 1141);
            links.SymbolNodes.Get(node).ResolvedSymbol = UnknownSymbol;
            return data.ResolvedType = context.ErrorType;
        }
        var module = await ResolveImportModuleAsync(node, literal,
            node.Attributes is null ? null : await ImportAttributesExpressionAsync(node.Attributes, cancellation), cancellation);
        if (module is null || await program.AliasTargets.ExternalModuleAsync(module, false, cancellation) is not { } target)
        {
            links.SymbolNodes.Get(node).ResolvedSymbol = UnknownSymbol;
            return data.ResolvedType = context.ErrorType;
        }
        var meaning = node.IsTypeOf ? SymbolFlags.Value : SymbolFlags.Type;
        if (node.Qualifier is { } qualifier && qualifier.Pos != qualifier.End)
        {
            var names = new Stack<SyntaxNode>();
            while (qualifier is QualifiedNameNode qualified)
            {
                names.Push(qualified.Right!);
                qualifier = qualified.Left!;
            }
            names.Push(qualifier);
            while (names.TryPop(out var current))
            {
                var resolved = program.Symbols.Merger.GetMergedSymbol(
                    await program.Aliases.SymbolAsync(target, cancellation: cancellation))!;
                string name = ((IdentifierNode)current).Text;
                var next = node.IsTypeOf ? await Properties.PropertyAsync(
                    await Values.GetAsync(resolved, cancellation),
                    name,
                    false,
                    true,
                    cancellation)
                    : program.Symbols.Lookup(
                        await program.ExportsAsync(resolved, cancellation),
                        name,
                        names.Count == 0 ? meaning : SymbolFlags.Namespace);
                if (next is null
                    && !node.IsTypeOf
                    && await program.AliasTargets.ExternalModuleAsync(module, true, cancellation) is { } immediate
                    && immediate.Declarations.Any(
                        d => ModuleExportsAccess(d is BinaryExpressionNode binary ? binary.Left! : d)) && immediate.Parent is { } parent)
                    next = program.Symbols.Lookup(
                        await program.ExportsAsync(parent, cancellation),
                        name,
                        names.Count == 0 ? meaning : SymbolFlags.Namespace);
                if (next is null)
                {
                    Error(
                        current,
                        2694,
                        await FullyQualifiedNameAsync(target, null, cancellation),
                        CheckerDiagnostic.DeclarationName(current));
                    return data.ResolvedType = context.ErrorType;
                }
                links.SymbolNodes.Get(current).ResolvedSymbol = next;
                links.SymbolNodes.Get(current.Parent!).ResolvedSymbol = next;
                target = next;
            }
        }
        else if ((await program.Aliases.FlagsAsync(target, cancellation: cancellation) & meaning) == 0)
        {
            Error(node, node.IsTypeOf ? 1339 : 1340, literal.Text);
            links.SymbolNodes.Get(node).ResolvedSymbol = UnknownSymbol;
            return data.ResolvedType = context.ErrorType;
        }
        var symbol = (await program.Aliases.SymbolAsync(target, cancellation: cancellation))!;
        links.SymbolNodes.Get(node).ResolvedSymbol = symbol;
        return data.ResolvedType = node.IsTypeOf ? await InstantiationExpressions.GetAsync(
            await Values.GetAsync(target, cancellation),
            node,
            cancellation)
            : await References.ReferenceAsync(node, symbol, cancellation);
    }
}
