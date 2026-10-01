using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class DeclarationTransformer
{
    private readonly Dictionary<SyntaxNode, SyntaxNode?> expandoHosts = [];
    private readonly Dictionary<SyntaxNode, List<SyntaxNode>> expandoMembers = [];
    private readonly Dictionary<SyntaxNode, List<BinaryExpressionNode>> deferredExpandos = [];

    private static SyntaxNode ExpandoRoot(SyntaxNode node) => node is VariableDeclarationNode ? node.Parent!.Parent! : node;
    private static IdentifierNode? LeftmostName(SyntaxNode? node)
    {
        while (AccessBase(node) is { } left) node = left;
        return node as IdentifierNode;
    }
    private static bool ReservedName(Utf8String name)
    {
        var kind = TokenFacts.FromText(name.Span);
        return kind is >= K.FirstKeyword and <= K.LastReservedWord or >= K.FirstFutureReservedWord and <= K.LastFutureReservedWord;
    }

    private async ValueTask ExpandoAssignmentAsync(BinaryExpressionNode node)
    {
        if (checker.Symbols.Declaration(node) is not { } symbol || (symbol.Flags & SymbolFlags.Assignment) == 0
            || LeftmostName(node.Left) is not { } leftmost) return;
        var declaration = await checker.GetReferencedValueForEmitAsync(leftmost, Cancellation);
        if (declaration is null || StripInternal(declaration) || declaration is VariableDeclarationNode { Type: not null }
            or FunctionDeclarationNode { FullSignature: not null }) return;
        if (declaration is VariableDeclarationNode { Initializer: not (FunctionExpressionNode or ArrowFunctionNode) }) return;
        if (checker.Symbols.Declaration(declaration) is not { } host) return;
        var property = node.Left is ElementAccessExpressionNode element ? await checker.GetElementAccessNameForEmitAsync(element, Cancellation)
            : node.Left is PropertyAccessExpressionNode { Name: { } propertyName } ? SyntaxNameText.Get(propertyName) : Utf8String.Empty;
        if (!IdentifierText(property)) return;
        var root = Context.MostOriginal(ExpandoRoot(declaration));
        if (await InvisibleAsync(declaration))
        {
            if (!deferredExpandos.TryGetValue(root, out var deferred)) deferredExpandos.Add(root, deferred = []);
            deferred.Add(node);
            return;
        }
        if (declaration is FunctionDeclarationNode { Body: null } && !host.Declarations.OfType<FunctionDeclarationNode>().Any(function => function.Body is not null)) return;
        var name = F.NewIdentifier(leftmost.Text);
        await ExpandoHostAsync(name, declaration);
        if (!expandoMembers.TryGetValue(root, out var members)) expandoMembers.Add(root, members = []);
        var exportName = F.NewIdentifier(property);
        IdentifierNode? local = null;
        if (!await checker.IsNameResolvableForEmitAsync(enclosing, property, Cancellation) && !ReservedName(property)) local = exportName;
        local ??= Context.NewGeneratedNameForNode(node);
        var saved = SaveContext();
        try
        {
            SetDiagnosticContext(node);
            bool hasExport = members.Any(member => member is ExportDeclarationNode);
            if (node.Right is IdentifierNode)
            {
                if (!hasExport) ExportExistingMembers();
                members.Add(await AliasExportAsync(node, exportName));
                return;
            }
            tracker.ErrorName = DeclarationDiagnostics.Name(node);
            var type = await checker.CreateExpandoTypeForEmitAsync(node, enclosing, host, symbol, name.Text, local.Text,
                Context, CurrentBuilderFlags, tracker, InternalFlags, Cancellation);
            await FlushInferenceAsync();
            var variable = F.NewVariableStatement(hasExport ? new([F.NewToken(K.ExportKeyword)]) : null,
                F.NewVariableDeclarationList(new([F.NewVariableDeclaration(local, null, type ?? F.NewKeywordTypeNode(K.AnyKeyword), null)]), NodeFlags.None));
            if (local.Text != exportName.Text)
            {
                if (!hasExport) ExportExistingMembers();
                members.Add(variable);
                members.Add(NamedExport(local, exportName));
            }
            else members.Add(variable);
        }
        finally { RestoreContext(saved); }

        void ExportExistingMembers()
        {
            for (int i = 0; i < members.Count; i++)
                if (members[i] is IModifiedNode)
                {
                    var updated = Context.Clone(members[i]);
                    ((IModifiedNode)updated).Modifiers = FromModifierFlags(CombinedFlags(updated) | ModifierFlags.Export);
                    members[i] = updated;
                }
        }
    }

    private async ValueTask ExpandoHostAsync(IdentifierNode name, SyntaxNode declaration)
    {
        var root = Context.MostOriginal(ExpandoRoot(declaration));
        if (expandoHosts.ContainsKey(root)) return;
        var saved = SaveContext();
        try
        {
            needsDeclare = true;
            var modifiers = Modifiers(root);
            needsDeclare = saved.Declare;
            bool defaultExport = modifiers?.Any(modifier => modifier.Kind == K.DefaultKeyword) == true
                && modifiers.Any(modifier => modifier.Kind == K.ExportKeyword);
            if (defaultExport)
                modifiers = new([F.NewToken(K.DeclareKeyword), .. modifiers!.Where(modifier => modifier.Kind is not (K.DefaultKeyword or K.ExportKeyword or K.DeclareKeyword))]);
            SetDiagnosticContext(declaration);
            List<SyntaxNode> replacements = [];
            if (declaration is FunctionDeclarationNode function)
            {
                var updated = Context.Clone(function);
                updated.Modifiers = modifiers;
                updated.TypeParameters = await TypeParametersAsync(function, function.TypeParameters);
                updated.Parameters = await ParametersAsync(function, function.Parameters);
                updated.Type = await EnsureTypeAsync(function);
                updated.FullSignature = null; updated.Body = null;
                replacements.Add(updated);
            }
            else if (declaration is VariableDeclarationNode { Initializer: FunctionExpressionNode or ArrowFunctionNode } variable)
            {
                var functionNode = variable.Initializer!;
                var signature = (IFunctionSignature)functionNode;
                replacements.Add(F.NewFunctionDeclaration(modifiers, functionNode is FunctionExpressionNode expression ? expression.AsteriskToken : null,
                    F.NewIdentifier(name.Text), await TypeParametersAsync(functionNode, signature.TypeParameters), await ParametersAsync(functionNode, signature.Parameters),
                    await EnsureTypeAsync(functionNode), null, null));
            }
            else { expandoHosts[root] = await TopLevelAsync(declaration); return; }
            await ReportExpandoErrorsAsync(declaration);
            if (defaultExport)
            {
                if (declaration.Parent is SourceFileNode) hasModuleIndicator = true;
                hasScopeMarker = true;
                replacements.Add(F.NewExportAssignment(null, false, null, name));
            }
            expandoHosts[root] = F.NewSyntaxList(replacements.ToArray());
            if (lateStatements.ContainsKey(root)) lateStatements[root] = await FullExpandoAsync(root);
        }
        finally { RestoreContext(saved); }
    }

    private async ValueTask<SyntaxNode?> FullExpandoAsync(SyntaxNode root)
    {
        if (deferredExpandos.Remove(root, out var deferred))
            foreach (var assignment in deferred) await ExpandoAssignmentAsync(assignment);
        var host = expandoHosts.GetValueOrDefault(root);
        if (host is null || !expandoMembers.TryGetValue(root, out var members)) return host;
        var named = Parts(host).FirstOrDefault(node => SemanticSyntax.Name(node) is not null);
        if (named is null) return host;
        var name = Context.Clone(SemanticSyntax.Name(named)!);
        var modifiers = named.ModifierList is { } mods ? new NodeList(mods.Select(Context.Clone).ToArray()) : null;
        var space = F.NewModuleDeclaration(modifiers, K.NamespaceKeyword, name, null, F.NewModuleBlock(new(members.ToArray())));
        return F.NewSyntaxList([.. Parts(host), space]);
    }

    private async ValueTask ReportExpandoErrorsAsync(SyntaxNode node)
    {
        if (options.IsolatedDeclarations != true) return;
        foreach (var property in await checker.GetContainerFunctionPropertiesForEmitAsync(node, Cancellation))
            if (property.ValueDeclaration is BinaryExpressionNode or PropertyAccessExpressionNode or ElementAccessExpressionNode)
                Report(property.ValueDeclaration is BinaryExpressionNode binary ? binary.Left! : property.ValueDeclaration,
                    Messages.Assigning_properties_to_functions_without_declaring_them_is_not_supported_with_isolatedDeclarations_Add_an_explicit_declaration_for_the_properties_assigned_to_this_function);
    }

    private async ValueTask<bool> ChildOfExpandoAsync(SyntaxNode node)
    {
        for (var current = node; current is not null && current is not SourceFileNode and not BlockNode; current = current.Parent)
            if (current is BinaryExpressionNode { Left: PropertyAccessExpressionNode left } && checker.Symbols.Declaration(current) is { } symbol
                && (symbol.Flags & SymbolFlags.Assignment) != 0 && LeftmostName(left) is { } name
                && await checker.GetReferencedValueForEmitAsync(name, Cancellation) is { } declaration
                && await checker.IsExpandoFunctionForEmitAsync(declaration, Cancellation)) return true;
        return false;
    }
}
