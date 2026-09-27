using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
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
            kind == NodeFlags.AwaitUsing
                ? DiagnosticCode.TheInitializerOfAnAwaitUsingDeclarationMustBeEitherAnObjectWithASymbolAsyncDisposeOrSymbolDisposeMethodOrBeNullOrUndefined
                : DiagnosticCode.TheInitializerOfAUsingDeclarationMustBeEitherAnObjectWithASymbolDisposeMethodOrBeNullOrUndefined,
            cancellation);
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
            DiagnosticCode code = DiagnosticCode.None;
            if (node.Declarations!.HasTrailingComma)
                code = DiagnosticCode.TrailingCommaNotAllowed;
            else if (node.Declarations.Count == 0)
                code = DiagnosticCode.VariableDeclarationListCannotBeEmpty;
            else if (usingDeclaration)
            {
                if (node.Parent?.Kind == SyntaxKind.ForInStatement)
                    code = awaitUsing
                        ? DiagnosticCode.TheLeftHandSideOfAForInStatementCannotBeAnAwaitUsingDeclaration
                        : DiagnosticCode.TheLeftHandSideOfAForInStatementCannotBeAUsingDeclaration;
                else if ((node.Flags & NodeFlags.Ambient) != 0)
                    code = awaitUsing
                        ? DiagnosticCode.XAwaitUsingDeclarationsAreNotAllowedInAmbientContexts
                        : DiagnosticCode.XUsingDeclarationsAreNotAllowedInAmbientContexts;
                else if (node.Parent is VariableStatementNode { Parent.Kind: SyntaxKind.CaseClause or SyntaxKind.DefaultClause })
                    code = awaitUsing
                        ? DiagnosticCode.XAwaitUsingDeclarationsAreNotAllowedInCaseOrDefaultClausesUnlessContainedWithinABlock
                        : DiagnosticCode.XUsingDeclarationsAreNotAllowedInCaseOrDefaultClausesUnlessContainedWithinABlock;
            }
            if (grammar && code != DiagnosticCode.None)
            {
                if (code == DiagnosticCode.TrailingCommaNotAllowed)
                    TrailingCommaError(node, node.Declarations!);
                else if (code == DiagnosticCode.VariableDeclarationListCannotBeEmpty)
                    ListError(node, node.Declarations!, code);
                else
                    Error(node, code);
                invalid = true;
            }
            else if (awaitUsing)
                invalid = AwaitUsingGrammar(node);
            if (!invalid && kind != 0 && node.Parent is VariableStatementNode variable)
            {
                if (!AllowsBlockScopedDeclaration(variable.Parent))
                    Error(
                        variable,
                        DiagnosticCode.X0DeclarationsCanOnlyBeDeclaredInsideABlock,
                        kind == NodeFlags.Let ? "let" : kind == NodeFlags.Const ? "const"
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
            Error(node, DiagnosticCode.XAwaitUsingStatementsCannotBeUsedInsideAClassStaticBlock);
            return true;
        }
        if ((node.Flags & NodeFlags.AwaitContext) == 0 && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
        {
            if (MissingNamePrefixes.ThisContainer(node, true, false) is SourceFileNode)
                return TopLevelAwait(
                    node,
                    DiagnosticCode.XAwaitUsingStatementsAreOnlyAllowedAtTheTopLevelOfAFileWhenThatFileIsAModuleButThisFileHasNoImportsOrExportsConsiderAddingAnEmptyExportToMakeThisFileAModule,
                    DiagnosticCode.TopLevelAwaitUsingStatementsAreOnlyAllowedWhenTheModuleOptionIsSetToEs2022EsnextSystemNode16Node18Node20NodenextOrPreserveAndTheTargetOptionIsSetToEs2017OrHigher);
            ExpressionError(node, DiagnosticCode.XAwaitUsingStatementsAreOnlyAllowedWithinAsyncFunctionsAndAtTheTopLevelsOfModules);
            return true;
        }
        return false;
    }
}
