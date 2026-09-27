using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private Type? disposableType;
    private Type? asyncDisposableType;

    private async ValueTask CheckDisposableInitializerAsync(VariableDeclarationNode node, CancellationToken cancellation)
    {
        var kind = (node.Flags | (node.Parent?.Flags ?? 0)) & NodeFlags.BlockScoped;
        if (kind is not (NodeFlags.Using or NodeFlags.AwaitUsing) || node.Initializer is not { } initializer
            || node.Name is BindingPatternNode || node.Parent?.Parent?.Kind == SyntaxKind.ForInStatement
            || program.Symbols.Declaration(node)?.ValueDeclaration != node)
            return;
        if (kind == NodeFlags.AwaitUsing)
            asyncDisposableType ??= await program.Globals.GetAsync("AsyncDisposable", 0, true, cancellation);
        disposableType ??= await program.Globals.GetAsync("Disposable", 0, true, cancellation);
        if (disposableType == context.EmptyObjectType || kind == NodeFlags.AwaitUsing && asyncDisposableType == context.EmptyObjectType)
            return;
        var target = await Algebra.UnionAsync(kind == NodeFlags.AwaitUsing
            ? [asyncDisposableType!, disposableType, context.NullType, context.UndefinedType]
            : [disposableType, context.NullType, context.UndefinedType], cancellation: cancellation);
        var source = await Variables.WidenAsync(await CachedExpressionAsync(initializer, 0, cancellation), node, false, cancellation);
        await RelationDiagnostics.CheckAsync(source, target, RelationKind.Assignable, initializer, initializer,
            kind == NodeFlags.AwaitUsing ? 2851 : 2850, cancellation);
    }

    private async ValueTask CheckVariableListAsync(VariableDeclarationListNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var kind = node.Flags & NodeFlags.BlockScoped;
        bool grammar = SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0;
        bool usingDeclaration = kind is NodeFlags.Using or NodeFlags.AwaitUsing;
        bool awaitUsing = kind == NodeFlags.AwaitUsing;
        bool modifierError = node.Parent is VariableStatementNode statement && (DeclarationModifiers(statement)
            || grammar && usingDeclaration && statement.Modifiers?.Any(
                m => m.Kind is SyntaxKind.ExportKeyword or SyntaxKind.DefaultKeyword or SyntaxKind.DeclareKeyword) == true);
        bool invalid = modifierError;
        if (!modifierError)
        {
            int code = 0;
            if (node.Declarations!.HasTrailingComma)
                code = 1009;
            else if (node.Declarations.Count == 0)
                code = 1123;
            else if (usingDeclaration)
            {
                if (node.Parent?.Kind == SyntaxKind.ForInStatement)
                    code = awaitUsing ? 1494 : 1493;
                else if ((node.Flags & NodeFlags.Ambient) != 0)
                    code = awaitUsing ? 1546 : 1545;
                else if (node.Parent is VariableStatementNode { Parent.Kind: SyntaxKind.CaseClause or SyntaxKind.DefaultClause })
                    code = awaitUsing ? 1548 : 1547;
            }
            if (grammar && code != 0)
            {
                Error(node, code);
                invalid = true;
            }
            else if (awaitUsing)
                invalid = AwaitUsingGrammar(node);
            if (!invalid && kind != 0 && node.Parent is VariableStatementNode variable)
            {
                if (!AllowsBlockScopedDeclaration(variable.Parent))
                    Error(variable, 1156, kind == NodeFlags.Let ? "let" : kind == NodeFlags.Const ? "const"
                        : kind == NodeFlags.Using ? "using" : "await using");
            }
        }
        if (usingDeclaration && TargetYear < int.MaxValue)
            await ExternalHelpersAsync(node, ["__addDisposableResource", "__disposeResources"], cancellation);
    }

    private static bool AllowsBlockScopedDeclaration(SyntaxNode? parent)
    {
        while (parent is LabeledStatementNode)
            parent = parent.Parent;
        return parent is not (IfStatementNode or DoStatementNode or WhileStatementNode or WithStatementNode or ForStatementNode
            or ForInOrOfStatementNode);
    }

    private bool AwaitUsingGrammar(VariableDeclarationListNode node)
    {
        var container = DeclarationOrder.Ancestor(node.Parent, n => n is IFunctionSignature or ClassStaticBlockDeclarationNode);
        if (container is ClassStaticBlockDeclarationNode)
        {
            Error(node, 18054);
            return true;
        }
        if ((node.Flags & NodeFlags.AwaitContext) == 0 && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
        {
            if (MissingNamePrefixes.ThisContainer(node, true, false) is SourceFileNode)
                return TopLevelAwait(node, 2853, 2854);
            Error(node, 2852);
            return true;
        }
        return false;
    }
}
