using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class IsolatedDeclarationFixer
{
    private async ValueTask<SyntaxNode?> RelativeAsync(SyntaxNode node)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (node is ParameterDeclarationNode) return null;
        if (node is ShorthandPropertyAssignmentNode shorthand) return TypeOf(shorthand.Name!);
        if (EntityName(node)) return TypeOf(node);
        if (ConstAssertion(node)) return await RelativeAsync(node is AsExpressionNode assertion ? assertion.Expression! : ((TypeAssertionNode)node).Expression!);
        if (node is ArrayLiteralExpressionNode or ObjectLiteralExpressionNode) return FromSpreads(node);
        if (node is VariableDeclarationNode { Initializer: { } initializer }) return await RelativeAsync(initializer);
        if (node is ConditionalExpressionNode conditional)
        {
            var whenTrue = await RelativeAsync(conditional.WhenTrue!);
            if (whenTrue is null) return null;
            bool changed = mutatedTarget;
            var whenFalse = await RelativeAsync(conditional.WhenFalse!);
            if (whenFalse is null) return null;
            mutatedTarget |= changed;
            return F.NewUnionTypeNode(new([whenTrue, whenFalse]));
        }
        return null;
    }

    private SyntaxNode? FromSpreads(SyntaxNode node)
    {
        bool array = node is ArrayLiteralExpressionNode;
        bool constant = Ancestor(node, ConstAssertion) is not null;
        if (array && !constant) return null;
        var name = Ancestor(node, node => node is VariableDeclarationNode) is VariableDeclarationNode { Name: IdentifierNode identifier }
            ? identifier.Text : (Utf8String)"temp"u8;
        var statement = Ancestor(node, TypeNodeFlow.Statement);
        List<SyntaxNode> types = [], spreads = [], current = [];
        var children = node is ArrayLiteralExpressionNode literal ? literal.Elements : ((ObjectLiteralExpressionNode)node).Properties;
        foreach (var child in children ?? new([]))
        {
            cancellation.ThrowIfCancellationRequested();
            var expression = child switch { SpreadElementNode element => element.Expression, SpreadAssignmentNode assignment => assignment.Expression, _ => null };
            if (expression is null) { current.Add(child); continue; }
            Flush();
            if (EntityName(expression)) { types.Add(TypeOf(expression)); spreads.Add(child); }
            else MakeVariable(expression);
        }
        if (spreads.Count == 0) return null;
        Flush();
        tracker.ReplaceNode(node, Literal(spreads));
        mutatedTarget = true;
        return array ? F.NewTupleTypeNode(new([.. types.Select(type => F.NewRestTypeNode(type))])) : F.NewIntersectionTypeNode(new([.. types]));

        SyntaxNode Literal(List<SyntaxNode> parts) => array ? F.NewArrayLiteralExpression(new([.. parts]), true) : F.NewObjectLiteralExpression(new([.. parts]), true);
        void Flush()
        {
            if (current.Count == 0) return;
            MakeVariable(Literal(current));
            current.Clear();
        }
        void MakeVariable(SyntaxNode expression)
        {
            var temp = Unique(name + "_Part"u8 + Utf8String.Format(spreads.Count + 1));
            var initializer = constant ? F.NewAsExpression(Clone(expression), ConstType()) : Clone(expression);
            if (statement is not null) tracker.InsertBefore(statement, Variable(temp, initializer));
            types.Add(TypeOf(temp));
            spreads.Add(array ? F.NewSpreadElement(temp) : F.NewSpreadAssignment(temp));
        }
    }

    private async ValueTask<Utf8String> DestructureAsync(SyntaxNode pattern)
    {
        if (pattern.Parent is not VariableDeclarationNode { Initializer: { } initializer, Parent: { Parent: VariableStatementNode statement } } declaration) return default;
        List<SyntaxNode> nodes = [];
        SyntaxNode expression;
        if (initializer is IdentifierNode identifier) expression = F.NewIdentifier(identifier.Text);
        else
        {
            expression = Unique("dest"u8);
            nodes.Add(Variable(expression, Clone(initializer)));
        }
        await ExtractBindingsAsync(pattern, expression, nodes, statement);
        if (nodes.Count == 0) return default;
        if (statement.DeclarationList is { Declarations.Count: > 1 } list)
        {
            var updatedList = Context.Clone(list);
            updatedList.Declarations = new([.. list.Declarations.Where(node => node != declaration)]);
            var updated = Context.Clone(statement); updated.DeclarationList = updatedList;
            nodes.Add(updated);
        }
        tracker.Replace(SmartIndenter.Start(statement, File), statement.End, nodes);
        return Messages.Extract_binding_expressions_to_variable.Format(preferences.Locale);
    }

    private async ValueTask ExtractBindingsAsync(SyntaxNode pattern, SyntaxNode expression, List<SyntaxNode> nodes, VariableStatementNode statement)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        var elements = ((BindingPatternNode)pattern).Elements!;
        for (int i = 0; i < elements.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (elements[i] is not BindingElementNode { Name: { } name } element) continue;
            SyntaxNode access;
            if (pattern.Kind == K.ArrayBindingPattern) access = F.NewElementAccessExpression(expression, null, F.NewNumericLiteral(Utf8String.Format(i), 0), 0);
            else if (element.PropertyName is ComputedPropertyNameNode { Expression: { } computed })
            {
                var temporary = Context.NewGeneratedNameForNode(computed);
                nodes.Add(Variable(temporary, computed));
                access = F.NewElementAccessExpression(expression, null, temporary, 0);
            }
            else if (element.PropertyName is { } property)
                access = F.NewPropertyAccessExpression(expression, null, F.NewIdentifier(SyntaxNameText.Get(property)), 0);
            else if (name is IdentifierNode identifier) access = F.NewPropertyAccessExpression(expression, null, F.NewIdentifier(identifier.Text), 0);
            else continue;
            if (BindingPattern(name)) { await ExtractBindingsAsync(name, access, nodes, statement); continue; }
            var type = await InferAsync(name);
            if (element.Initializer is { } fallback)
            {
                var temporary = Unique(element.PropertyName is IdentifierNode propertyName ? propertyName.Text : (Utf8String)"temp"u8);
                nodes.Add(Variable(temporary, access));
                access = F.NewConditionalExpression(F.NewBinaryExpression(null, temporary, null, F.NewToken(K.EqualsEqualsEqualsToken), F.NewIdentifier("undefined"u8)),
                    F.NewToken(K.QuestionToken), fallback, F.NewToken(K.ColonToken), access);
            }
            NodeList? modifiers = SemanticSyntax.HasModifier(statement, K.ExportKeyword) ? new([F.NewToken(K.ExportKeyword)]) : null;
            nodes.Add(Variable(F.NewIdentifier(SyntaxNameText.Get(name)), access, type, modifiers));
        }
    }
}
