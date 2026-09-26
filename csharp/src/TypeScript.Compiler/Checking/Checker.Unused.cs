using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private void CheckUnusedSource(SourceFileNode file, CancellationToken cancellation)
    {
        if (file.IsDeclarationFile)
            return;
        foreach (var renamed in RenamedBindingElements)
            if (SemanticSyntax.Source(renamed) == file && program.Symbols.ReferenceKinds(program.Symbols.Declaration(renamed)!) == 0)
                Error(renamed.Name!, 2842);
        foreach (var node in UnusedIdentifierScopes)
        {
            cancellation.ThrowIfCancellationRequested();
            if (program.Symbols.Binding(node)?.SourceFile != file)
                continue;
            switch (node)
            {
                case ClassDeclarationNode or ClassExpressionNode:
                    CheckUnusedClass(node);
                    CheckUnusedTypeParameters(node);
                    break;
                case SourceFileNode or ModuleDeclarationNode or BlockNode or CaseBlockNode or ForStatementNode or ForInOrOfStatementNode:
                    CheckUnusedLocals(node);
                    break;
                case IFunctionSignature:
                    if (SemanticSyntax.Body(node) is not null)
                        CheckUnusedLocals(node);
                    CheckUnusedTypeParameters(node);
                    break;
                case TypeAliasDeclarationNode or InterfaceDeclarationNode:
                    CheckUnusedTypeParameters(node);
                    break;
                case InferTypeNode infer:
                    if (UnreferencedTypeParameter(infer.TypeParameter!))
                        ReportUnused(infer, infer.TypeParameter!.Name!, 6196, true);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected unused-check scope {node.Kind}");
            }
        }
    }

    private void ReportUnused(SyntaxNode declaration, SyntaxNode location, int code, bool parameter)
    {
        if (((declaration.Flags | (program.Symbols.Binding(declaration)?.Get(declaration)?.Flags ?? 0)) & (NodeFlags.Ambient | NodeFlags.ThisNodeOrAnySubNodesHasError)) != 0)
            return;
        if (program.Symbols.Program.Configuration.Options.Boolean(parameter ? "noUnusedParameters" : "noUnusedLocals") == true)
        {
            string[] arguments = [];
            if (code is 6133 or 6138 or 6196)
            {
                var name = SemanticSyntax.Name(location) ?? location;
                arguments = [name is IdentifierNode identifier ? identifier.Text
                    : program.Symbols.Declaration(declaration) is { } symbol ? TypeDisplay.SymbolName(symbol)
                    : CheckerDiagnostic.DeclarationName(name)];
            }
            Error(location, code, arguments);
        }
        else
            ExpressionSuggestion(location, code);
    }

    private void ReportUnusedVariable(SyntaxNode node, SyntaxNode location, int code)
    {
        while (node is BindingElementNode or BindingPatternNode)
            node = node.Parent!;
        ReportUnused(node, location, code, node is ParameterDeclarationNode);
    }

    private void CheckUnusedClass(SyntaxNode node)
    {
        var members = node is ClassDeclarationNode declaration ? declaration.Members! : ((ClassExpressionNode)node).Members!;
        foreach (var member in members)
        {
            if (member is MethodDeclarationNode or PropertyDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode)
            {
                var symbol = program.Symbols.Declaration(member)!;
                if (member is SetAccessorDeclarationNode && (symbol.Flags & SymbolFlags.GetAccessor) != 0)
                    continue;
                if (program.Symbols.ReferenceKinds(symbol) == 0
                    && (SemanticSyntax.HasModifier(member, SyntaxKind.PrivateKeyword)
                        || SemanticSyntax.Name(member) is PrivateIdentifierNode))
                    ReportUnused(member, SemanticSyntax.Name(member)!, 6133, false);
            }
            else if (member is ConstructorDeclarationNode constructor)
                foreach (var parameter in constructor.Parameters!)
                    if (program.Symbols.ReferenceKinds(program.Symbols.Declaration(parameter)!) == 0
                        && SemanticSyntax.HasModifier(parameter, SyntaxKind.PrivateKeyword))
                        ReportUnused(parameter, SemanticSyntax.Name(parameter)!, 6138, false);
        }
    }

    private void CheckUnusedLocals(SyntaxNode node)
    {
        var locals = program.Symbols.Binding(node)?.Get(node)?.Locals;
        if (locals is null)
            return;
        var variableParents = new HashSet<SyntaxNode>();
        var imports = new Dictionary<ImportClauseNode, List<SyntaxNode>>();
        foreach (var local in locals.Values)
        {
            var referenced = program.Symbols.ReferenceKinds(local);
            if ((local.Flags & SymbolFlags.TypeParameter) != 0
                ? (local.Flags & SymbolFlags.Variable) == 0 || (referenced & SymbolFlags.Variable) != 0
                : referenced != 0 || local.ExportSymbol is not null || (local.Flags & SymbolFlags.ModuleExports) != 0)
                continue;
            foreach (var declaration in local.Declarations)
            {
                if (declaration is VariableDeclarationNode or ParameterDeclarationNode or BindingElementNode)
                    variableParents.Add(SemanticSyntax.RootDeclaration(declaration).Parent!);
                else if (declaration is ImportClauseNode or ImportSpecifierNode or NamespaceImportNode)
                {
                    if (StartsWithUnderscore(SemanticSyntax.Name(declaration)))
                        continue;
                    var clause = declaration as ImportClauseNode ?? (ImportClauseNode)(declaration is NamespaceImportNode
                        ? declaration.Parent!
                        : declaration.Parent!.Parent!);
                    if (!imports.TryGetValue(clause, out var unused))
                        imports.Add(clause, unused = []);
                    unused.Add(declaration);
                }
                else if (declaration is not TypeParameterDeclarationNode && !AmbientModule(declaration))
                    ReportUnusedLocal(declaration);
            }
        }
        foreach (var parent in variableParents)
            if (parent is VariableDeclarationListNode list && list.Declarations!.Count > 1 && list.Declarations.All(UnreferencedVariable))
                ReportUnusedVariable(list, list, 6199);
            else
                ReportUnusedVariables(
                    parent is VariableDeclarationListNode variables ? variables.Declarations! : ((IFunctionSignature)parent).Parameters!);
        foreach (var (clause, unused) in imports)
        {
            int count = clause.Name is null ? 0 : 1;
            count += clause.NamedBindings is NamespaceImportNode ? 1 : (clause.NamedBindings as NamedImportsNode)?.Elements?.Count ?? 0;
            if (count > 1 && count == unused.Count)
                ReportUnused(clause, clause.Parent!, 6192, false);
            else
                foreach (var declaration in unused)
                    ReportUnusedLocal(declaration);
        }
    }

    private void ReportUnusedLocal(SyntaxNode node)
    {
        bool type = node is TypeParameterDeclarationNode or ClassDeclarationNode or InterfaceDeclarationNode or TypeAliasDeclarationNode
            or EnumDeclarationNode
            || node is ImportClauseNode && SemanticSyntax.TypeOnly(node)
            || node is ImportSpecifierNode or ExportSpecifierNode && node.Parent?.Parent is { } parent && SemanticSyntax.TypeOnly(parent);
        ReportUnused(node, SemanticSyntax.Name(node) ?? node, type ? 6196 : 6133, false);
    }

    private void ReportUnusedVariables(IEnumerable<SyntaxNode> declarations)
    {
        var pending = new Stack<SyntaxNode>(declarations.Reverse());
        while (pending.TryPop(out var declaration))
        {
            var name = SemanticSyntax.Name(declaration);
            if (name is null
                || ParameterProperty(declaration)
                || declaration is ParameterDeclarationNode { Name: IdentifierNode { Text: "this" } })
                continue;
            if (name is BindingPatternNode pattern)
            {
                if (pattern.Elements!.Count > 1 && pattern.Elements.All(UnreferencedVariable))
                    ReportUnusedVariable(pattern, pattern, 6198);
                else
                    for (int i = pattern.Elements.Count - 1; i >= 0; i--)
                        pending.Push(pattern.Elements[i]);
            }
            else if (UnreferencedVariable(declaration))
                ReportUnusedVariable(declaration, name, 6133);
        }
    }

    private bool UnreferencedVariable(SyntaxNode node)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(node);
        while (pending.TryPop(out var declaration))
        {
            var name = SemanticSyntax.Name(declaration);
            if (name is null)
                continue;
            if (name is BindingPatternNode pattern)
            {
                foreach (var element in pattern.Elements!)
                    pending.Push(element);
                continue;
            }
            if ((program.Symbols.ReferenceKinds(program.Symbols.Declaration(declaration)!) & SymbolFlags.Variable) != 0)
                return false;
            if (declaration is BindingElementNode
                && declaration.Parent is BindingPatternNode { Kind: SyntaxKind.ObjectBindingPattern } objectPattern
                && objectPattern.Elements!.LastOrDefault() is BindingElementNode { DotDotDotToken: not null } last && declaration != last)
                return false;
            bool ignoredUnderscore = declaration is ParameterDeclarationNode
                || declaration is VariableDeclarationNode variable
                    && (variable.Parent?.Parent is ForInOrOfStatementNode || (variable.Parent!.Flags & NodeFlags.Using) != 0)
                || declaration is BindingElementNode binding
                    && (binding.Parent?.Kind != SyntaxKind.ObjectBindingPattern || binding.PropertyName is not null);
            if (ignoredUnderscore && StartsWithUnderscore(name))
                return false;
        }
        return true;
    }

    private static bool StartsWithUnderscore(SyntaxNode? node) => node is IdentifierNode identifier && identifier.Text.StartsWith('_');

    private bool UnreferencedTypeParameter(SyntaxNode parameter) => !StartsWithUnderscore(SemanticSyntax.Name(parameter))
        && (program.Symbols.ReferenceKinds(program.Symbols.Declaration(parameter)!) & SymbolFlags.TypeParameter) == 0;

    private void CheckUnusedTypeParameters(SyntaxNode node)
    {
        if (program.Symbols.Declaration(node) is { } symbol && symbol.Declarations.Select(SemanticSyntax.Source).Distinct().Skip(1).Any())
            return;
        var parameters = node switch
        {
            IFunctionSignature signature => signature.TypeParameters,
            ClassDeclarationNode declaration => declaration.TypeParameters,
            ClassExpressionNode expression => expression.TypeParameters,
            InterfaceDeclarationNode declaration => declaration.TypeParameters,
            TypeAliasDeclarationNode alias => alias.TypeParameters,
            _ => null
        };
        if (parameters is null)
            return;
        if (parameters.Count > 1 && parameters.All(UnreferencedTypeParameter))
            ReportUnused(node, node, 6205, true);
        else
            foreach (var parameter in parameters)
                if (UnreferencedTypeParameter(parameter))
                    ReportUnused(node, parameter, 6196, true);
    }
}
