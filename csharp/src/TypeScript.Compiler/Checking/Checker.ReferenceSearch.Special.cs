using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using R = TypeScript.Compiler.LanguageServices.ReferenceNavigation;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<Type?> GetReferenceStringContextAsync(SyntaxNode node, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(node, cancellation);
        return await new ReferenceSearch(this, [], new(ReferenceUse.Rename), cancellation).StringContextAsync(node);
    }
    private sealed partial class ReferenceSearch
    {
        private readonly Dictionary<SourceFileNode, HashSet<Utf8String>> nameTables = [];
        private async ValueTask<bool> HasNameAsync(SourceFileNode file, Utf8String name)
        {
            if (!nameTables.TryGetValue(file, out var names))
            {
                names = [];
                Stack<SyntaxNode> pending = new();
                for (int i = file.ChildCount - 1; i >= 0; i--) pending.Push(file.GetChild(i));
                while (pending.TryPop(out var node))
                {
                    cancellation.ThrowIfCancellationRequested();
                    bool tagName = node.Parent is { Kind: >= K.FirstJSDocTagNode and <= K.LastJSDocTagNode } tag && tag.GetChild(0) == node;
                    if (node is IdentifierNode && !tagName && !R.Text(node).IsEmpty || node is PrivateIdentifierNode
                        || node is StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode
                            && (QuerySyntax.DeclarationName(node) || node.Parent is ExternalModuleReferenceNode
                                || node.Parent is ElementAccessExpressionNode access && access.ArgumentExpression == node
                                || node.Parent is ComputedPropertyNameNode && QuerySyntax.DeclarationName(node.Parent))) names.Add(R.Text(node));
                    foreach (var doc in await file.GetDocumentationAsync(node, cancellation))
                        for (int i = doc.ChildCount - 1; i >= 0; i--) pending.Push(doc.GetChild(i));
                    for (int i = node.ChildCount - 1; i >= 0; i--) pending.Push(node.GetChild(i));
                }
                nameTables.Add(file, names);
            }
            return names.Contains(name);
        }
        private IEnumerable<SyntaxNode> Descendants(SyntaxNode root, Func<SyntaxNode, bool>? skip = null)
        {
            Stack<SyntaxNode> pending = new();
            pending.Push(root);
            while (pending.TryPop(out var node))
            {
                cancellation.ThrowIfCancellationRequested();
                yield return node;
                if (skip?.Invoke(node) == true) continue;
                for (int i = node.ChildCount - 1; i >= 0; i--) pending.Push(node.GetChild(i));
            }
        }
        private async ValueTask<IReadOnlyList<ReferenceGroup>?> SpecialAsync(SyntaxNode node)
        {
            if (R.TypeKeyword(node.Kind) && !(node.Kind == K.VoidKeyword && node.Parent is VoidExpressionNode)
                && !(node.Kind == K.ReadonlyKeyword && !R.ReadonlyOperator(node)))
            {
                var group = new ReferenceGroup(ReferenceDefinitionKind.Keyword, null, null);
                foreach (var file in files)
                    foreach (int position in R.Positions(file, TokenFacts.Text(node.Kind), file, cancellation))
                    {
                        var reference = await SyntaxNavigation.GetTouchingPropertyNameAsync(file, position, cancellation);
                        if (reference.Kind == node.Kind && (node.Kind != K.ReadonlyKeyword || R.ReadonlyOperator(reference))) group.Entries.Add(ReferenceEntry.FromNode(reference));
                    }
                return DefineFirst(group);
            }
            if (node.Parent is MetaPropertyNode { KeywordToken: K.ImportKeyword } meta && meta.Name == node)
            {
                var group = new ReferenceGroup(ReferenceDefinitionKind.Keyword, null, null);
                foreach (var file in files)
                    foreach (int position in R.Positions(file, "meta"u8, file, cancellation))
                        if ((await SyntaxNavigation.GetTouchingPropertyNameAsync(file, position, cancellation)).Parent is MetaPropertyNode { KeywordToken: K.ImportKeyword } reference)
                            group.Entries.Add(ReferenceEntry.FromNode(reference));
                return DefineFirst(group);
            }
            if (node.Kind == K.StaticKeyword && node.Parent is ClassStaticBlockDeclarationNode)
                return One(ReferenceDefinitionKind.Keyword, node, null, [ReferenceEntry.FromNode(node)]);
            if (node is IdentifierNode && node.Parent is BreakStatementNode or ContinueStatementNode or LabeledStatementNode)
            {
                var target = node.Parent is LabeledStatementNode ? node : R.TargetLabel(node.Parent, R.Text(node));
                if (target is null) return null;
                var file = SemanticSyntax.Source(target)!;
                List<ReferenceEntry> entries = [];
                foreach (int position in R.Positions(file, R.Text(target), target.Parent, cancellation))
                {
                    var reference = await SyntaxNavigation.GetTouchingPropertyNameAsync(file, position, cancellation);
                    if (reference == target || reference is IdentifierNode && reference.Parent is BreakStatementNode or ContinueStatementNode
                        && R.TargetLabel(reference, R.Text(target)) == target) entries.Add(ReferenceEntry.FromNode(reference));
                }
                return One(ReferenceDefinitionKind.Label, target, null, entries);
            }
            if (R.This(node)) return await ThisAsync(node);
            if (node.Kind == K.SuperKeyword && R.SuperContainer(node) is { } container
                && container is PropertyDeclarationNode or PropertySignatureDeclarationNode or MethodDeclarationNode or MethodSignatureDeclarationNode
                    or ConstructorDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode)
            {
                bool isStatic = SemanticSyntax.IsStatic(container);
                var search = container.Parent!;
                List<ReferenceEntry> entries = [];
                foreach (int position in R.Positions(SemanticSyntax.Source(search)!, "super"u8, search, cancellation))
                {
                    var reference = await SyntaxNavigation.GetTouchingPropertyNameAsync(SemanticSyntax.Source(search)!, position, cancellation);
                    if (reference.Kind == K.SuperKeyword && R.SuperContainer(reference) is { } owner && SemanticSyntax.IsStatic(owner) == isStatic
                        && owner.Parent?.BindingSymbol == search.BindingSymbol) entries.Add(ReferenceEntry.FromNode(reference));
                }
                return One(ReferenceDefinitionKind.Symbol, null, search.BindingSymbol, entries);
            }
            return null;
        }
        private async ValueTask<IReadOnlyList<ReferenceGroup>?> ThisAsync(SyntaxNode node)
        {
            var container = MissingNamePrefixes.ThisContainer(node, false, false);
            bool isStatic = true;
            bool parameter = node is IdentifierNode && node.Parent is ParameterDeclarationNode p && p.Name == node;
            switch (container)
            {
                case MethodDeclarationNode or MethodSignatureDeclarationNode or PropertyDeclarationNode or PropertySignatureDeclarationNode
                    or ConstructorDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode:
                    isStatic = SemanticSyntax.IsStatic(container); container = container.Parent!; break;
                case SourceFileNode source:
                    if (source.ExternalModuleIndicator is not null || parameter) return null;
                    break;
                case FunctionDeclarationNode or FunctionExpressionNode: break;
                default: return null;
            }
            List<ReferenceEntry> entries = [];
            foreach (var file in container is SourceFileNode ? files : [SemanticSyntax.Source(container)!])
                foreach (int position in R.Positions(file, "this"u8, container is SourceFileNode ? file : container, cancellation))
                {
                    var reference = await SyntaxNavigation.GetTouchingPropertyNameAsync(file, position, cancellation);
                    if (!R.This(reference)) continue;
                    var owner = MissingNamePrefixes.ThisContainer(reference, false, false);
                    bool matches = container switch
                    {
                        FunctionExpressionNode or FunctionDeclarationNode => container.BindingSymbol == owner.BindingSymbol,
                        MethodDeclarationNode or MethodSignatureDeclarationNode => container.Parent is ObjectLiteralExpressionNode && container.BindingSymbol == owner.BindingSymbol,
                        ClassExpressionNode or ClassDeclarationNode or ObjectLiteralExpressionNode => owner.Parent is not null
                            && container.BindingSymbol == owner.Parent.BindingSymbol && SemanticSyntax.IsStatic(owner) == isStatic,
                        SourceFileNode => owner is SourceFileNode { ExternalModuleIndicator: null }
                            && !(reference is IdentifierNode && reference.Parent is ParameterDeclarationNode declaration && declaration.Name == reference),
                        _ => false,
                    };
                    if (matches) entries.Add(ReferenceEntry.FromNode(reference));
                }
            return One(ReferenceDefinitionKind.This, entries.FirstOrDefault(e => e.Node?.Parent is ParameterDeclarationNode)?.Node ?? node, container.BindingSymbol, entries);
        }
        private static IReadOnlyList<ReferenceGroup>? DefineFirst(ReferenceGroup group) => group.Entries.Count == 0 ? null
            : One(group.Kind, group.Entries[0].Node, group.Symbol, group.Entries);
        private static IReadOnlyList<ReferenceGroup> One(ReferenceDefinitionKind kind, SyntaxNode? node, Symbol? symbol, IEnumerable<ReferenceEntry> entries)
        {
            var group = new ReferenceGroup(kind, node, symbol); group.Entries.AddRange(entries); return [group];
        }
        private async ValueTask<IReadOnlyList<ReferenceGroup>> StringAsync(SyntaxNode node)
        {
            var context = await StringContextAsync(node);
            List<ReferenceEntry> entries = [];
            foreach (var file in files)
                foreach (int position in R.Positions(file, R.Text(node), null, cancellation))
                {
                    var reference = await SyntaxNavigation.GetTouchingPropertyNameAsync(file, position, cancellation);
                    if (reference is not (StringLiteralNode or NoSubstitutionTemplateLiteralNode) || R.Text(reference) != R.Text(node)) continue;
                    if (context is not null)
                    {
                        var referenceType = await StringContextAsync(reference);
                        if (context == checker.context.StringType || context != referenceType && !(reference.Parent is PropertySignatureDeclarationNode
                            && await checker.Properties.PropertyAsync(await TypeAsync(reference.Parent.Parent!), R.Text(reference), cancellation: cancellation) is not null)) continue;
                    }
                    else if (reference is NoSubstitutionTemplateLiteralNode
                        && file.Source.Text.Span[reference.Pos..reference.End].IndexOfAny((byte)'\r', (byte)'\n') >= 0) continue;
                    entries.Add(ReferenceEntry.FromNode(reference, ReferenceEntryKind.StringLiteral));
                }
            return One(ReferenceDefinitionKind.String, node, null, entries);
        }
        internal async ValueTask<Type?> StringContextAsync(SyntaxNode node)
        {
            if ((node.Flags & (NodeFlags.JSDoc | NodeFlags.JavaScriptFile)) == NodeFlags.JSDoc) return null;
            var parent = node.Parent;
            while (parent?.Parent is ParenthesizedExpressionNode) parent = parent.Parent;
            Type? type = null;
            if (parent is NewExpressionNode) type = await checker.Contexts.GetAsync(parent, cancellation: cancellation);
            else if (parent is BinaryExpressionNode binary && binary.OperatorToken?.Kind is K.EqualsEqualsToken or K.EqualsEqualsEqualsToken or K.ExclamationEqualsToken or K.ExclamationEqualsEqualsToken)
                type = await TypeAsync(node == binary.Right ? binary.Left! : binary.Right!);
            else if (parent is CaseOrDefaultClauseNode { Kind: K.CaseClause, Parent.Parent: SwitchStatementNode statement }) type = await TypeAsync(statement.Expression!);
            else type = await checker.Contexts.GetAsync(node, cancellation: cancellation);
            if (type is not null) return type;
            SyntaxNode? last = null;
            for (var current = node; current is not null; current = current.Parent)
            {
                if (SemanticSyntax.TypeNode(current)) last = current;
                if (current.Parent is not QualifiedNameNode && (current.Parent is null || !SemanticSyntax.TypeNode(current.Parent) && !TypeElement(current.Parent))) break;
            }
            return last is null ? null : await TypeAsync(last);
        }
        private static bool TypeElement(SyntaxNode node) => node is PropertySignatureDeclarationNode or MethodSignatureDeclarationNode
            or CallSignatureDeclarationNode or ConstructSignatureDeclarationNode or IndexSignatureDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode;
        private static bool Implementation(SyntaxNode node)
        {
            if ((node.Flags & NodeFlags.Ambient) != 0) return node is not (InterfaceDeclarationNode or TypeAliasDeclarationNode);
            if (node is VariableDeclarationNode or ParameterDeclarationNode or BindingElementNode or PropertyDeclarationNode or PropertySignatureDeclarationNode
                or PropertyAssignmentNode or ShorthandPropertyAssignmentNode or EnumMemberNode)
                return node is IInitializedNode { Initializer: not null };
            if (SemanticSyntax.FunctionDeclarationLike(node)) return SemanticSyntax.Body(node) is not null;
            return node is ClassDeclarationNode or ClassExpressionNode or ModuleDeclarationNode or EnumDeclarationNode;
        }
        private static bool ImplementationExpression(SyntaxNode node)
        {
            while (node is ParenthesizedExpressionNode parent) node = parent.Expression!;
            return node is ArrowFunctionNode or FunctionExpressionNode or ObjectLiteralExpressionNode or ClassExpressionNode or ArrayLiteralExpressionNode;
        }
        private async ValueTask ImplementationsAsync(SyntaxNode node, Action<SyntaxNode> add)
        {
            if (QuerySyntax.DeclarationName(node) && Implementation(node.Parent!)) { add(node); return; }
            if (node is not IdentifierNode) return;
            if (node.Parent is ShorthandPropertyAssignmentNode && await SymbolAsync(node) is { ValueDeclaration: { } value }
                && await ShorthandAsync(value) is { } shorthand)
                foreach (var declaration in shorthand.Declarations) if ((R.DeclarationMeaning(declaration) & R.Value) != 0) add(declaration);
            var heritage = node.Parent;
            while (heritage is IdentifierNode or QualifiedNameNode or PropertyAccessExpressionNode) heritage = heritage.Parent;
            if (heritage is ExpressionWithTypeArgumentsNode or TypeReferenceNode && heritage.Parent is HeritageClauseNode { Parent: { } container }
                && container is ClassDeclarationNode or ClassExpressionNode or InterfaceDeclarationNode) { add(container); return; }
            var type = (SyntaxNode)node;
            while (type.Parent is QualifiedNameNode || type.Parent is { } parent && (SemanticSyntax.TypeNode(parent) || TypeElement(parent))) type = type.Parent!;
            if (type.Parent is not ITypedNode { Type: { } annotation } || annotation != type || !seenTypeReferences.Add(type.Parent)) return;
            var owner = type.Parent;
            void AddExpression(SyntaxNode? expression) { if (expression is not null && ImplementationExpression(expression)) add(expression); }
            if (owner is IInitializedNode { Initializer: { } initializer }) AddExpression(initializer);
            else if (owner is IFunctionSignature && SemanticSyntax.Body(owner) is { } body)
            {
                if (body is BlockNode)
                {
                    foreach (var statement in Descendants(body, n => n is IFunctionSignature or ClassDeclarationNode or ClassExpressionNode))
                        if (statement is ReturnStatementNode returned) AddExpression(returned.Expression);
                }
                else AddExpression(body);
            }
            else AddExpression(owner switch { TypeAssertionNode n => n.Expression, AsExpressionNode n => n.Expression, SatisfiesExpressionNode n => n.Expression, _ => null });
        }
        private async ValueTask ConstructorReferencesAsync(SyntaxNode node, Symbol symbol, Search search, bool addHere)
        {
            var target = node;
            while (target.Parent is PropertyAccessExpressionNode access && access.Name == target) target = target.Parent;
            if (target.Parent is NewExpressionNode creation && creation.Expression == target && addHere) await AddAsync(node, symbol, ReferenceEntryKind.Node);
            if (SemanticSyntax.ClassLike(node.Parent))
            {
                var file = SemanticSyntax.Source(node)!;
                foreach (var declaration in search.Symbol.Members.GetValueOrDefault(Symbol.InternalConstructor)?.Declarations ?? [])
                    if (declaration is ConstructorDeclarationNode && await SyntaxNavigation.FindChildOfKindAsync(declaration, K.ConstructorKeyword, file, cancellation) is { } keyword) Add(search.Symbol, keyword);
                foreach (var member in search.Symbol.Exports.Values)
                    if (member.ValueDeclaration is MethodDeclarationNode { Body: { } body })
                        foreach (var reference in Descendants(body))
                            if (reference.Kind == K.ThisKeyword && reference.Parent is NewExpressionNode n && n.Expression == reference) Add(search.Symbol, reference);
            }
            else if (target.Parent is ExpressionWithTypeArgumentsNode { Parent: HeritageClauseNode { Token: K.ExtendsKeyword, Parent: { } derived } }
                && SemanticSyntax.ClassLike(derived))
            {
                var constructor = derived.BindingSymbol?.Members.GetValueOrDefault(Symbol.InternalConstructor);
                foreach (var declaration in constructor?.Declarations ?? [])
                    if (declaration is ConstructorDeclarationNode { Body: { } body })
                        foreach (var reference in Descendants(body))
                            if (reference.Kind == K.SuperKeyword && reference.Parent is CallExpressionNode n && n.Expression == reference) Add(search.Symbol, reference);
                if (constructor is null && derived.BindingSymbol is { } derivedSymbol) await InScopeAsync(derivedSymbol, await CreateAsync(null, derivedSymbol));
            }
        }
        private void ClassThisReferences(SyntaxNode node, Symbol symbol)
        {
            var members = node switch { ClassDeclarationNode c => c.Members, ClassExpressionNode c => c.Members, _ => null };
            foreach (var member in members ?? new([]))
                if (member is MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
                    && SemanticSyntax.HasModifier(member, K.StaticKeyword) && SemanticSyntax.Body(member) is { } body)
                    foreach (var reference in Descendants(body, n => n is IFunctionSignature || SemanticSyntax.ClassLike(n)))
                        if (reference.Kind == K.ThisKeyword) Add(symbol, reference);
        }
        private async ValueTask<Symbol?> DestructuringPropertyAsync(SyntaxNode node)
        {
            if (node.Parent?.Parent is not (ObjectLiteralExpressionNode or ArrayLiteralExpressionNode) || !DestructuringPattern(node.Parent.Parent)) return null;
            var pattern = node.Parent.Parent;
            var type = await checker.AssignedTypeAsync(pattern, cancellation);
            await checker.CheckDestructuringAsync(pattern, type, 0, false, cancellation);
            return await checker.Properties.PropertyAsync(type, R.Text(node), cancellation: cancellation);
        }
        private static bool DestructuringPattern(SyntaxNode node)
        {
            while (node.Parent is ArrayLiteralExpressionNode or ObjectLiteralExpressionNode or PropertyAssignmentNode or SpreadAssignmentNode or SpreadElementNode) node = node.Parent;
            return node.Parent is BinaryExpressionNode binary && binary.Left == node || node.Parent is ForInOrOfStatementNode loop && loop.Initializer == node;
        }
        private IReadOnlyList<ReferenceGroup> Merge(params IReadOnlyList<ReferenceGroup>?[] lists)
        {
            List<ReferenceGroup> merged = [];
            foreach (var list in lists)
                foreach (var group in list ?? [])
                {
                    var previous = group.Kind == ReferenceDefinitionKind.Symbol ? merged.FirstOrDefault(g => g.Kind == ReferenceDefinitionKind.Symbol && g.Symbol == group.Symbol) : null;
                    if (previous is null) { merged.Add(group); continue; }
                    previous.Entries.AddRange(group.Entries);
                    var sorted = previous.Entries.OrderBy(e => Program.SourceFiles.Select(f => f.Syntax).ToList().IndexOf(e.File ?? SemanticSyntax.Source(e.Node)!))
                        .ThenBy(e => e.Start >= 0 ? e.Start : SmartIndenter.Start(e.Node!, SemanticSyntax.Source(e.Node)!)).ThenBy(e => e.End >= 0 ? e.End : e.Node!.End).ToArray();
                    previous.Entries.Clear(); previous.Entries.AddRange(sorted);
                }
            return merged;
        }
    }
}
