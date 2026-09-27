using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly HashSet<Symbol> checkedInterfaces = [];
    private readonly HashSet<Symbol> checkedTypeParameterLists = [];

    private async ValueTask CheckInterfaceSourceAsync(InterfaceDeclarationNode node, CancellationToken cancellation)
    {
        if (!AllowsBlockScopedDeclaration(node.Parent) && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            Error(node, 1156, "interface");
        HeritageGrammar(node, node.HeritageClauses, isInterface: true);
        ExportedDeclaration(node, false);
        await CheckMergedExportsAsync(node, cancellation).ConfigureAwait(false);
        if (node.TypeParameters is not null)
            foreach (TypeParameterDeclarationNode parameter in node.TypeParameters)
                await FunctionDeclarations.TypeParameterAsync(parameter, cancellation).ConfigureAwait(false);
        if (ReservedTypeName(node.Name!.Text))
            Error(node.Name, 2427, node.Name.Text);
        var symbol = program.Symbols.Declaration(node)!;
        var type = (InterfaceType)await Declared.GetAsync(symbol, cancellation).ConfigureAwait(false);
        await CheckMergedTypeParametersAsync(symbol, type, cancellation).ConfigureAwait(false);
        if (checkedInterfaces.Add(symbol))
        {
            try
            {
                var withThis = await Bases.WithThisAsync(type, null, cancellation: cancellation).ConfigureAwait(false);
                var bases = await Bases.GetAsync(type, cancellation).ConfigureAwait(false);
                bool identical = true;
                if (bases.Count >= 2)
                {
                    await Members.ResolveAsync(type, cancellation).ConfigureAwait(false);
                    var seen = new Dictionary<string, (Symbol Property, Type Owner)>(StringComparer.Ordinal);
                    if (type.DeclaredMembers is { } declaredMembers)
                        foreach (var (name, property) in declaredMembers)
                            if (await Members.NamedAsync(name, property, cancellation).ConfigureAwait(false))
                                seen[property.Name] = (property, type);
                    foreach (var baseType in bases)
                        foreach (var property in await Properties.GetAsync(
                            await Bases.WithThisAsync(baseType, type.ThisType, cancellation: cancellation).ConfigureAwait(false),
                            cancellation).ConfigureAwait(false))
                        {
                            if (!seen.TryGetValue(property.Name, out var existing))
                                seen[property.Name] = (property, baseType);
                            else if (existing.Owner != type && await Identity.PropertyAsync(existing.Property, property,
                                async (source, target, token) => await Relations.RelatedAsync(
                                    source,
                                    target,
                                    RelationKind.Identity,
                                    token).ConfigureAwait(false)
                                    ? Ternary.True
                                    : Ternary.False,
                                cancellation).ConfigureAwait(false) == Ternary.False)
                            {
                                identical = false;
                                string first = await TypeDisplay.GetAsync(existing.Owner, cancellation);
                                string second = await TypeDisplay.GetAsync(baseType, cancellation);
                                var detail = CheckerDiagnostic.Create(node.Name,
                                    Messages.Named_property_0_of_types_1_and_2_are_not_identical,
                                    TypeDisplay.SymbolName(property), first, second);
                                Error(node.Name, CheckerDiagnostic.Create(node.Name,
                                    Messages.Interface_0_cannot_simultaneously_extend_types_1_and_2,
                                    await TypeDisplay.GetAsync(type, cancellation), first, second) with
                                { MessageChain = [detail] });
                            }
                        }
                }
                if (identical)
                {
                    foreach (var baseType in bases)
                    {
                        var target = await Bases.WithThisAsync(baseType, type.ThisType, cancellation: cancellation).ConfigureAwait(false);
                        if (await Relations.ExplainAsync(withThis, target, RelationKind.Assignable, cancellation) is { } explanation)
                            await ReportRelationMessageAsync(node.Name, 2430, withThis, target, RelationKind.Assignable, cancellation,
                                preparedExplanation: explanation);
                    }
                    await IndexDeclarationChecks.CheckAsync(type, false, cancellation).ConfigureAwait(false);
                }
            }
            catch
            {
                checkedInterfaces.Remove(symbol);
                throw;
            }
        }
        IndexDeclarationChecks.DuplicateProperties(node.Members!, cancellation);
        if (node.HeritageClauses?.OfType<HeritageClauseNode>().FirstOrDefault(h => h.Token == SyntaxKind.ExtendsKeyword) is { } heritage)
            foreach (var element in heritage.Types!)
            {
                if (element is ExpressionWithTypeArgumentsNode expression
                    && (!ConstantEvaluator.EntityName(expression.Expression!)
                        || (expression.Expression!.Flags & NodeFlags.OptionalChain) != 0))
                    Error(expression.Expression!, 2499);
                await TypeReferenceChecks.CheckAsync(element, cancellation).ConfigureAwait(false);
            }
        foreach (var member in node.Members!)
            await CheckSourceElementAsync(member, cancellation).ConfigureAwait(false);
        await IndexDeclarationChecks.DuplicateIndexesAsync(node, cancellation).ConfigureAwait(false);
        RegisterUnused(node);
    }

    private async ValueTask CheckMergedTypeParametersAsync(Symbol symbol, InterfaceType type, CancellationToken cancellation)
    {
        if (symbol.Declarations.Count <= 1 || checkedTypeParameterLists.Contains(symbol))
            return;
        var declarations = symbol.Declarations.Where(n => n is InterfaceDeclarationNode or ClassDeclarationNode).ToArray();
        var parameters = type.AllTypeParameters.Skip(type.OuterTypeParameterCount).Take(
            type.AllTypeParameters.Count - type.OuterTypeParameterCount - (type.ThisType is null ? 0 : 1)).Cast<TypeParameter>().ToArray();
        bool identical = true;
        foreach (var declaration in declarations)
        {
            var source = declaration is InterfaceDeclarationNode iface
                ? iface.TypeParameters
                : ((ClassDeclarationNode)declaration).TypeParameters;
            int count = source?.Count ?? 0;
            if (count < TypeReferences.Minimum(parameters) || count > parameters.Length)
            {
                identical = false;
                break;
            }
            for (int i = 0; i < count; i++)
            {
                var parameter = (TypeParameterDeclarationNode)source![i];
                if (parameter.Name!.Text != parameters[i].Symbol!.Name)
                {
                    identical = false;
                    break;
                }
                var constraint = await Instantiation.Constraints.ConstraintAsync(parameters[i], cancellation).ConfigureAwait(false);
                if (parameter.Constraint is not null
                    && constraint is not null
                    && !await IdenticalAsync(
                        await Nodes.FromNodeAsync(parameter.Constraint, cancellation).ConfigureAwait(false),
                        constraint,
                        cancellation).ConfigureAwait(false))
                {
                    identical = false;
                    break;
                }
                var defaultType = await Instantiation.Constraints.DefaultAsync(parameters[i], cancellation).ConfigureAwait(false);
                if (parameter.DefaultType is not null
                    && defaultType is not null
                    && !await IdenticalAsync(
                        await Nodes.FromNodeAsync(parameter.DefaultType, cancellation).ConfigureAwait(false),
                        defaultType,
                        cancellation).ConfigureAwait(false))
                {
                    identical = false;
                    break;
                }
            }
            if (!identical)
                break;
        }
        cancellation.ThrowIfCancellationRequested();
        checkedTypeParameterLists.Add(symbol);
        if (!identical)
            foreach (INamedNode declaration in declarations)
                Error(declaration.Name!, 2428, TypeDisplay.SymbolName(symbol));
    }

    private static bool ReservedTypeName(string name) =>
        name is "any" or "unknown" or "never" or "number" or "bigint" or "boolean" or "string" or "symbol" or "void" or "object"
            or "undefined";
}
