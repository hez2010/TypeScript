using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Emission;

/// <summary>
/// One-pass feature scan of a source file, used to skip transform passes that provably cannot
/// change it. Each flag corresponds to the only syntax its pass rewrites, so a missing flag means
/// the pass would visit the whole tree and return it unchanged.
/// </summary>
internal static class SourceFeatures
{
    [Flags]
    internal enum Flags
    {
        None = 0,
        /// <summary>Class declarations/expressions: the class-fields pass only rewrites classes.</summary>
        Class = 1,
        /// <summary>Decorators: the ES decorators pass only rewrites decorated declarations.</summary>
        Decorator = 2,
        /// <summary>`using`/`await using` declarations: the using pass only rewrites those.</summary>
        Using = 4,
        /// <summary>Property/element accesses: the const-enum inliner only replaces those.</summary>
        Access = 8,
        /// <summary>Enum declarations: the runtime pass drops or transforms them.</summary>
        Enum = 16,
        /// <summary>Namespace/module declarations: the runtime pass transforms their bodies.</summary>
        Namespace = 32,
        /// <summary>TypeScript-only modifiers: the runtime pass strips them from emitted output.</summary>
        TsModifier = 64,
        /// <summary>`import x = ...` declarations: both the runtime pass and module rewriting handle them.</summary>
        ImportEquals = 128,
        /// <summary>
        /// Syntax that can create an aliased reference: import/export declarations, `export =`,
        /// import-equals and JSX elements (their factory names are marked as aliases too). Only files
        /// with one of these need the checker-side linked-reference marking before emit.
        /// </summary>
        ModuleSyntax = 256,
        All = Class | Decorator | Using | Access
    }

    internal static Flags Scan(SyntaxNode file)
    {
        var features = Flags.None;
        var pending = new Stack<SyntaxNode>();
        for (int i = file.ChildCount - 1; i >= 0; i--)
            pending.Push(file.GetChild(i));
        while (pending.TryPop(out var node))
        {
            features |= node switch
            {
                ClassDeclarationNode or ClassExpressionNode => Flags.Class,
                DecoratorNode => Flags.Decorator,
                VariableDeclarationListNode list when (list.Flags & NodeFlags.BlockScoped) is NodeFlags.Using or NodeFlags.AwaitUsing
                    => Flags.Using,
                PropertyAccessExpressionNode or ElementAccessExpressionNode => Flags.Access,
                EnumDeclarationNode => Flags.Enum,
                ModuleDeclarationNode => Flags.Namespace,
                ImportEqualsDeclarationNode => Flags.ImportEquals | Flags.ModuleSyntax,
                ImportDeclarationNode or ExportDeclarationNode or ExportAssignmentNode or NamespaceExportDeclarationNode
                    => Flags.ModuleSyntax,
                JsxOpeningElementNode or JsxSelfClosingElementNode or JsxOpeningFragmentNode => Flags.ModuleSyntax,
                TokenNode token when token.Kind is SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword
                    or SyntaxKind.ProtectedKeyword or SyntaxKind.ReadonlyKeyword or SyntaxKind.OverrideKeyword => Flags.TsModifier,
                _ => Flags.None
            };
            if (features == Flags.All)
                return features;
            for (int i = node.ChildCount - 1; i >= 0; i--)
                pending.Push(node.GetChild(i));
        }
        return features;
    }
}
