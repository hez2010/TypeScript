using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private void CheckJavaScriptSyntax(SourceFileNode file)
    {
        if (file.ScriptKind is not (ScriptKind.JS or ScriptKind.JSX))
            return;
        var errors = new List<Diagnostic>();
        var pending = new Stack<(SyntaxNode Node, bool Visited)>();
        pending.Push((file, false));
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            SyntaxNode node = item.Node;
            if (!item.Visited)
            {
                pending.Push((node, true));
                for (int i = node.ChildCount - 1; i >= 0; i--)
                    pending.Push((node.GetChild(i), false));
                continue;
            }
            if ((node.Flags & NodeFlags.JavaScriptFile) == 0
                || (node.Flags & (NodeFlags.JSDoc | NodeFlags.Reparsed)) != 0
                || !CheckJavaScriptNode(node))
                continue;
            SyntaxNode? question = node switch
            {
                ParameterDeclarationNode parameter => parameter.QuestionToken,
                PropertyDeclarationNode property => property.PostfixToken,
                MethodDeclarationNode method => method.PostfixToken,
                _ => null,
            };
            if (question is { Kind: K.QuestionToken } && (question.Flags & NodeFlags.Reparsed) == 0)
                Error(question, Messages.The_0_modifier_can_only_be_used_in_TypeScript_files, "?");

            switch (node.Kind)
            {
                case K.Parameter:
                case K.PropertyDeclaration:
                case K.MethodDeclaration:
                case K.MethodSignature:
                case K.Constructor:
                case K.GetAccessor:
                case K.SetAccessor:
                case K.FunctionExpression:
                case K.FunctionDeclaration:
                case K.ArrowFunction:
                case K.VariableDeclaration:
                case K.IndexSignature:
                    bool signature = node.Kind is not (K.Parameter or K.PropertyDeclaration or K.VariableDeclaration);
                    SyntaxNode? body = node switch
                    {
                        MethodDeclarationNode n => n.Body,
                        ConstructorDeclarationNode n => n.Body,
                        GetAccessorDeclarationNode n => n.Body,
                        SetAccessorDeclarationNode n => n.Body,
                        FunctionExpressionNode n => n.Body,
                        FunctionDeclarationNode n => n.Body,
                        ArrowFunctionNode n => n.Body,
                        _ => null,
                    };
                    if (signature && body is null)
                        Error(node, Messages.Signature_declarations_can_only_be_used_in_TypeScript_files);
                    else if (node is ITypedNode { Type: { } type } && (type.Flags & NodeFlags.Reparsed) == 0)
                        Error(type, Messages.Type_annotations_can_only_be_used_in_TypeScript_files);
                    break;
                case K.ImportDeclaration when node is ImportDeclarationNode { ImportClause.PhaseModifier: K.TypeKeyword }:
                    Error(node, Messages.X_0_declarations_can_only_be_used_in_TypeScript_files, "import type");
                    break;
                case K.ExportDeclaration when node is ExportDeclarationNode { IsTypeOnly: true }:
                    Error(node, Messages.X_0_declarations_can_only_be_used_in_TypeScript_files, "export type");
                    break;
                case K.ImportSpecifier when node is ImportSpecifierNode { IsTypeOnly: true }:
                    Error(node, Messages.X_0_declarations_can_only_be_used_in_TypeScript_files, "import...type");
                    break;
                case K.ExportSpecifier when node is ExportSpecifierNode { IsTypeOnly: true }:
                    Error(node, Messages.X_0_declarations_can_only_be_used_in_TypeScript_files, "export...type");
                    break;
                case K.ImportEqualsDeclaration:
                    Error(node, Messages.X_import_can_only_be_used_in_TypeScript_files);
                    break;
                case K.ExportAssignment when node is ExportAssignmentNode { IsExportEquals: true }:
                    Error(node, Messages.X_export_can_only_be_used_in_TypeScript_files);
                    break;
                case K.HeritageClause when node is HeritageClauseNode { Token: K.ImplementsKeyword }:
                    Error(node, Messages.X_implements_clauses_can_only_be_used_in_TypeScript_files);
                    break;
                case K.InterfaceDeclaration:
                    Error(((INamedNode)node).Name!, Messages.X_0_declarations_can_only_be_used_in_TypeScript_files, "interface");
                    break;
                case K.ModuleDeclaration:
                    Error(
                        ((INamedNode)node).Name!,
                        Messages.X_0_declarations_can_only_be_used_in_TypeScript_files,
                        TokenFacts.Text(((ModuleDeclarationNode)node).Keyword));
                    break;
                case K.TypeAliasDeclaration:
                    Error(((INamedNode)node).Name!, Messages.Type_aliases_can_only_be_used_in_TypeScript_files);
                    break;
                case K.EnumDeclaration:
                    Error(((INamedNode)node).Name!, Messages.X_0_declarations_can_only_be_used_in_TypeScript_files, "enum");
                    break;
                case K.NonNullExpression:
                    Error(node, Messages.Non_null_assertions_can_only_be_used_in_TypeScript_files);
                    break;
                case K.AsExpression:
                    if (node is ITypedNode { Type: { } assertion } && (assertion.Flags & NodeFlags.Reparsed) == 0)
                        Error(assertion, Messages.Type_assertion_expressions_can_only_be_used_in_TypeScript_files);
                    break;
                case K.SatisfiesExpression:
                    if (node is ITypedNode { Type: { } satisfaction } && (satisfaction.Flags & NodeFlags.Reparsed) == 0)
                        Error(satisfaction, Messages.Type_satisfaction_expressions_can_only_be_used_in_TypeScript_files);
                    break;
            }

            NodeList? modifiers = (node as IModifiedNode)?.Modifiers;
            CheckDecorators(node, modifiers);
            switch (node.Kind)
            {
                case K.ClassDeclaration:
                case K.ClassExpression:
                case K.MethodDeclaration:
                case K.Constructor:
                case K.GetAccessor:
                case K.SetAccessor:
                case K.FunctionExpression:
                case K.FunctionDeclaration:
                case K.ArrowFunction:
                    NodeList? parameters = node switch
                    {
                        IFunctionSignature n => n.TypeParameters,
                        ClassDeclarationNode n => n.TypeParameters,
                        ClassExpressionNode n => n.TypeParameters,
                        _ => null,
                    };
                    if (parameters?.Any(n => (n.Flags & NodeFlags.Reparsed) == 0) == true)
                        RangeError(
                            parameters.Pos,
                            parameters.End,
                            Messages.Type_parameter_declarations_can_only_be_used_in_TypeScript_files);
                    goto case K.VariableStatement;
                case K.VariableStatement:
                case K.PropertyDeclaration:
                    if (modifiers is not null)
                        foreach (SyntaxNode modifier in modifiers)
                            if ((modifier.Flags & NodeFlags.Reparsed) == 0
                                && modifier.Kind is not (K.Decorator or K.ExportKeyword or K.StaticKeyword or K.AccessorKeyword
                                    or K.AsyncKeyword or K.DefaultKeyword))
                                Error(
                                    modifier,
                                    Messages.The_0_modifier_can_only_be_used_in_TypeScript_files,
                                    TokenFacts.Text(modifier.Kind));
                    break;
                case K.Parameter:
                    if (modifiers?.Any(n => IsModifierKind(n.Kind)) == true)
                        RangeError(modifiers.Pos, modifiers.End, Messages.Parameter_modifiers_can_only_be_used_in_TypeScript_files);
                    break;
                case K.CallExpression:
                case K.NewExpression:
                case K.ExpressionWithTypeArguments:
                case K.JsxSelfClosingElement:
                case K.JsxOpeningElement:
                case K.TaggedTemplateExpression:
                    NodeList? arguments = node switch
                    {
                        CallExpressionNode n => n.TypeArguments,
                        NewExpressionNode n => n.TypeArguments,
                        ExpressionWithTypeArgumentsNode n => n.TypeArguments,
                        JsxSelfClosingElementNode n => n.TypeArguments,
                        JsxOpeningElementNode n => n.TypeArguments,
                        TaggedTemplateExpressionNode n => n.TypeArguments,
                        _ => null,
                    };
                    if (arguments?.Any(n => (n.Flags & NodeFlags.Reparsed) == 0) == true)
                        RangeError(arguments.Pos, arguments.End, Messages.Type_arguments_can_only_be_used_in_TypeScript_files);
                    break;
            }
        }
        file.JSDiagnostics = errors.ToArray();

        Diagnostic At(int start, int end, DiagnosticMessage message, params TextSlice[] arguments)
        {
            var trivia = new Scanner(source);
            trivia.ResetPosition(Math.Max(0, source.ToUtf16Position(start)));
            trivia.Scan();
            int tokenStart = Math.Min(source.ToBytePosition(trivia.TokenStart), end);
            return new(message, tokenStart, Math.Max(0, end - tokenStart), arguments) { FileName = options.FileName };
        }
        void RangeError(int start, int end, DiagnosticMessage message, params TextSlice[] arguments) =>
            errors.Add(At(start, end, message, arguments));
        void Error(SyntaxNode node, DiagnosticMessage message, params TextSlice[] arguments) =>
            RangeError(node.Pos, node.End, message, arguments);
        void CheckDecorators(SyntaxNode node, NodeList? modifiers)
        {
            if (modifiers is null)
                return;
            int firstDecorator = -1, export = -1, @default = -1;
            for (int i = 0; i < modifiers.Count; i++)
            {
                if (firstDecorator < 0 && modifiers[i].Kind == K.Decorator)
                    firstDecorator = i;
                if (export < 0 && modifiers[i].Kind == K.ExportKeyword)
                    export = i;
                if (@default < 0 && modifiers[i].Kind == K.DefaultKeyword)
                    @default = i;
            }
            if (firstDecorator < 0)
                return;
            if (node.Kind is K.FunctionDeclaration or K.Constructor or K.IndexSignature or K.VariableStatement
                or K.InterfaceDeclaration or K.TypeAliasDeclaration or K.EnumDeclaration or K.ModuleDeclaration
                or K.ImportEqualsDeclaration or K.ImportDeclaration or K.NamespaceExportDeclaration or K.ExportDeclaration or K.ExportAssignment)
                Error(modifiers[firstDecorator], Messages.Decorators_are_not_valid_here);
            else if (node.Kind == K.ClassDeclaration && export >= 0)
            {
                if (firstDecorator > export && @default >= 0 && firstDecorator < @default)
                    Error(modifiers[firstDecorator], Messages.Decorators_are_not_valid_here);
                else if (firstDecorator < export)
                    for (int i = export + 1; i < modifiers.Count; i++)
                        if (modifiers[i].Kind == K.Decorator)
                        {
                            Diagnostic related = At(
                                modifiers[firstDecorator].Pos,
                                modifiers[firstDecorator].End,
                                Messages.Decorator_used_before_export_here);
                            errors.Add(
                                At(
                                    modifiers[i].Pos,
                                    modifiers[i].End,
                                    Messages.Decorators_may_not_appear_after_export_or_export_default_if_they_also_appear_before_export) with
                                { RelatedInformation = [related] });
                            break;
                        }
            }
        }
    }

    private static bool CheckJavaScriptNode(SyntaxNode node) => node.Kind switch
    {
        K.Parameter => node.Parent is FunctionDeclarationNode or FunctionExpressionNode or ArrowFunctionNode or MethodDeclarationNode
            or ConstructorDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode,
        K.GetAccessor or K.SetAccessor => node.Parent is not (TypeLiteralNode or InterfaceDeclarationNode or MappedTypeNode),
        K.IndexSignature => node.Parent is ClassDeclarationNode or ClassExpressionNode,
        K.ExpressionWithTypeArguments => node.Parent is HeritageClauseNode
        {
            Token: K.ExtendsKeyword, Parent: ClassDeclarationNode
            or ClassExpressionNode
        },
        K.VariableStatement or K.VariableDeclaration or K.FunctionDeclaration or K.FunctionExpression or K.ArrowFunction
            or K.ClassDeclaration or K.ClassExpression or K.HeritageClause or K.Constructor or K.MethodDeclaration or K.PropertyDeclaration
            or K.InterfaceDeclaration or K.TypeAliasDeclaration or K.EnumDeclaration or K.ModuleDeclaration or K.ImportDeclaration
            or K.ImportEqualsDeclaration or K.ImportSpecifier or K.NamespaceExportDeclaration or K.ExportDeclaration or K.ExportSpecifier
            or K.ExportAssignment or K.AsExpression or K.SatisfiesExpression or K.NonNullExpression or K.CallExpression or K.NewExpression
            or K.TaggedTemplateExpression => true,
        _ => false,
    };
}
