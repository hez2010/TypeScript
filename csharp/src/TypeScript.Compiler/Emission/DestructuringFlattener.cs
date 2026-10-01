using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal enum FlattenLevel { All, ObjectRest }

internal sealed class DestructuringFlattener(EmitContext context, Func<SyntaxNode?, ValueTask<SyntaxNode?>> visit,
    Func<IdentifierNode, SyntaxNode, EmitRange, SyntaxNode>? assignment = null, CancellationToken cancellation = default,
    FlattenLevel level = FlattenLevel.All)
{
    private sealed class Pending(SyntaxNode name, SyntaxNode value, EmitRange range, SyntaxNode? original)
    {
        internal readonly SyntaxNode Name = name;
        internal SyntaxNode Value = value;
        internal readonly EmitRange Range = range;
        internal readonly SyntaxNode? Original = original;
        internal readonly List<SyntaxNode> Expressions = [];
    }
    private readonly List<SyntaxNode> expressions = [];
    private readonly List<Pending> declarations = [];
    private bool binding, hoist = true, transformedPriorElement;
    private NodeFactory F => context.Factory;
    private static EmitRange Range(SyntaxNode node) => new(node.Pos, node.End);
    private static T At<T>(T node, EmitRange range) where T : SyntaxNode { node.Pos = range.Pos; node.End = range.End; return node; }
    private static bool IsPattern(SyntaxNode? node) => node is BindingPatternNode or ArrayLiteralExpressionNode or ObjectLiteralExpressionNode;
    private static NodeList Elements(SyntaxNode node) => node switch
    {
        BindingPatternNode n => n.Elements!, ArrayLiteralExpressionNode n => n.Elements!, ObjectLiteralExpressionNode n => n.Properties!,
        _ => throw new ArgumentException("Expected a binding or assignment pattern", nameof(node))
    };
    private static bool IsDeclaration(SyntaxNode node) => node is VariableDeclarationNode or ParameterDeclarationNode or BindingElementNode;
    private static bool IsAssignment(SyntaxNode? node) => node is BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken };
    private static bool IsDestructuring(SyntaxNode node) => node is BinaryExpressionNode n && IsAssignment(n) && IsPattern(n.Left);
    private static bool Rest(SyntaxNode node) => node is BindingElementNode { DotDotDotToken: not null } or SpreadElementNode or SpreadAssignmentNode;

    internal static SyntaxNode? Target(SyntaxNode? element)
    {
        while (element is not null)
        {
            switch (element)
            {
                case VariableDeclarationNode n: return n.Name;
                case ParameterDeclarationNode n: return n.Name;
                case BindingElementNode n: return n.Name;
                case ShorthandPropertyAssignmentNode n: return n.Name;
                case PropertyAssignmentNode n: element = n.Initializer; break;
                case SpreadElementNode n: element = n.Expression; break;
                case SpreadAssignmentNode n: element = n.Expression; break;
                case BinaryExpressionNode n when IsAssignment(n): element = n.Left; break;
                case MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode: return null;
                default: return element;
            }
        }
        return null;
    }

    internal static SyntaxNode? Initializer(SyntaxNode? element)
    {
        while (element is SpreadElementNode spread) element = spread.Expression;
        return element switch
        {
            VariableDeclarationNode n => n.Initializer,
            ParameterDeclarationNode n => n.Initializer,
            BindingElementNode n => n.Initializer,
            PropertyAssignmentNode { Initializer: BinaryExpressionNode n } when IsAssignment(n) => n.Right,
            ShorthandPropertyAssignmentNode n => n.ObjectAssignmentInitializer,
            BinaryExpressionNode n when IsAssignment(n) => n.Right,
            _ => null
        };
    }

    internal static SyntaxNode? PropertyName(SyntaxNode element)
    {
        var name = element switch { BindingElementNode n => n.PropertyName, PropertyAssignmentNode n => n.Name, _ => null };
        if (name is ComputedPropertyNameNode { Expression: { } expression } && IsStringOrNumber(expression)) return expression;
        if (name is not null) return name;
        var target = Target(element);
        return target is IdentifierNode or PrivateIdentifierNode or StringLiteralNode or NumericLiteralNode or ComputedPropertyNameNode ? target : null;
    }
    private static bool IsStringOrNumber(SyntaxNode node) => node is StringLiteralNode or NumericLiteralNode || node.Kind == K.NoSubstitutionTemplateLiteral;

    internal async ValueTask<SyntaxNode> AssignmentAsync(SyntaxNode node, bool needsValue)
    {
        var range = Range(node);
        SyntaxNode? value = null;
        if (IsDestructuring(node))
        {
            value = ((BinaryExpressionNode)node).Right!;
            while (Elements(((BinaryExpressionNode)node).Left!).Count == 0)
            {
                if (!IsDestructuring(value)) return (await visit(value))!;
                node = value;
                range = Range(node);
                value = ((BinaryExpressionNode)node).Right!;
            }
        }
        if (value is not null)
        {
            value = (await visit(value))!;
            if (value is IdentifierNode id && AssignsTo(node, id.Text) || HasNonliteralComputedName(node))
                value = await EnsureIdentifierAsync(value, false, range);
            else if (needsValue)
                value = await EnsureIdentifierAsync(value, true, range);
            else if (node.Pos < 0 || node.End < 0)
                range = Range(value);
        }
        await FlattenAsync(node, value, range, IsDestructuring(node));
        if (value is not null && needsValue) expressions.Add(value);
        return context.InlineExpressions(expressions) ?? F.NewOmittedExpression();
    }

    internal async ValueTask<SyntaxNode?> BindingAsync(SyntaxNode node, SyntaxNode? value, bool hoistTemporaries, bool skipInitializer)
    {
        binding = true;
        hoist = hoistTemporaries;
        if (node is VariableDeclarationNode variable && Initializer(node) is { } initializer
            && (initializer is IdentifierNode name && AssignsTo(node, name.Text) || HasNonliteralComputedName(node)))
        {
            var replacement = await EnsureIdentifierAsync((await visit(initializer))!, false, Range(initializer));
            var updated = context.Clone(variable);
            updated.ExclamationToken = null;
            updated.Type = null;
            updated.Initializer = replacement;
            node = updated;
        }
        await FlattenAsync(node, value, Range(node), skipInitializer);
        if (expressions.Count != 0)
        {
            var temp = context.NewTempVariable();
            if (hoist)
            {
                value = context.InlineExpressions(expressions)!;
                expressions.Clear();
                await EmitAsync(temp, value, new(0, 0), null);
            }
            else
            {
                context.AddVariableDeclaration(temp);
                var last = declarations[^1];
                last.Expressions.Add(context.Binary(temp, K.EqualsToken, last.Value));
                last.Expressions.AddRange(expressions);
                last.Value = temp;
            }
        }
        List<SyntaxNode> result = [];
        foreach (var pending in declarations)
        {
            var initialValue = pending.Expressions.Count == 0 ? pending.Value : context.InlineExpressions([.. pending.Expressions, pending.Value]);
            var declaration = At(F.NewVariableDeclaration(pending.Name, null, null, initialValue), pending.Range);
            if (pending.Original is not null) context.SetOriginal(declaration, pending.Original);
            result.Add(declaration);
        }
        return result.Count switch { 0 => null, 1 => result[0], _ => F.NewSyntaxList(result.ToArray()) };
    }

    private async ValueTask EmitAsync(SyntaxNode target, SyntaxNode value, EmitRange range, SyntaxNode? original)
    {
        if (binding)
        {
            if (expressions.Count != 0)
            {
                value = context.InlineExpressions([.. expressions, value])!;
                expressions.Clear();
            }
            declarations.Add(new(target, value, range, original));
            return;
        }
        var expression = assignment is not null && target is IdentifierNode name ? assignment(name, value, range)
            : At(context.Binary((await visit(target))!, K.EqualsToken, value), range);
        if (original is not null) context.SetOriginal(expression, original);
        expressions.Add(expression);
    }

    private async ValueTask<SyntaxNode> EnsureIdentifierAsync(SyntaxNode value, bool reuse, EmitRange range)
    {
        if (reuse && value is IdentifierNode) return value;
        var temp = context.NewTempVariable();
        if (hoist)
        {
            context.AddVariableDeclaration(temp);
            expressions.Add(At(context.Binary(temp, K.EqualsToken, value), range));
        }
        else await EmitAsync(temp, value, range, null);
        return temp;
    }

    private async ValueTask FlattenAsync(SyntaxNode element, SyntaxNode? value, EmitRange range, bool skipInitializer)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var target = Target(element);
        if (target is null) return;
        if (!skipInitializer)
        {
            var initializer = await visit(Initializer(element));
            if (initializer is not null)
            {
                if (value is not null)
                {
                    value = await EnsureIdentifierAsync(value, true, range);
                    value = F.NewConditionalExpression(context.Binary(value, K.EqualsEqualsEqualsToken, context.VoidZero()), F.NewToken(K.QuestionToken), initializer, F.NewToken(K.ColonToken), value);
                    if (!TransformSyntax.SimpleCopiable(initializer) && IsPattern(target))
                        value = await EnsureIdentifierAsync(value, true, range);
                }
                else value = initializer;
            }
            else value ??= context.VoidZero();
        }
        if (target.Kind is K.ObjectBindingPattern or K.ObjectLiteralExpression)
            await ObjectAsync(element, target, value!, range);
        else if (target.Kind is K.ArrayBindingPattern or K.ArrayLiteralExpression)
            await ArrayAsync(element, target, value!, range);
        else await EmitAsync(target, value!, range, element);
    }

    private SyntaxNode Pattern(List<SyntaxNode> elements, bool array) => binding
        ? F.NewBindingPattern(array ? K.ArrayBindingPattern : K.ObjectBindingPattern, new(elements.ToArray()))
        : array ? F.NewArrayLiteralExpression(new(elements.ToArray()), false) : F.NewObjectLiteralExpression(new(elements.ToArray()), false);

    private async ValueTask ObjectAsync(SyntaxNode parent, SyntaxNode pattern, SyntaxNode value, EmitRange range)
    {
        var elements = Elements(pattern);
        if (elements.Count != 1) value = await EnsureIdentifierAsync(value, !IsDeclaration(parent) || elements.Count != 0, range);
        List<SyntaxNode> retained = [], computed = [];
        for (int i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            if (!Rest(element))
            {
                var name = PropertyName(element)!;
                if (level == FlattenLevel.ObjectRest && !ContainsRest(element) && !ContainsRest(Target(element)) && name is not ComputedPropertyNameNode)
                {
                    retained.Add((await visit(element))!);
                    continue;
                }
                await FlushAsync();
                SyntaxNode access;
                if (name is ComputedPropertyNameNode property)
                {
                    var index = await EnsureIdentifierAsync((await visit(property.Expression))!, false, Range(property));
                    computed.Add(index);
                    access = F.NewElementAccessExpression(value, null, index, NodeFlags.None);
                }
                else if (IsStringOrNumber(name) || name is BigIntLiteralNode)
                    access = F.NewElementAccessExpression(value, null, context.Clone(name), NodeFlags.None);
                else access = F.NewPropertyAccessExpression(value, null, F.NewIdentifier(((IdentifierNode)name).Text), NodeFlags.None);
                await FlattenAsync(element, access, Range(element), false);
            }
            else if (i == elements.Count - 1)
            {
                await FlushAsync();
                await FlattenAsync(element, RestHelper(value, elements, computed, Range(pattern)), Range(element), false);
            }
        }
        await FlushAsync();

        async ValueTask FlushAsync()
        {
            if (retained.Count == 0) return;
            await EmitAsync(Pattern(retained, false), value, range, pattern);
            retained.Clear();
        }
    }

    private async ValueTask ArrayAsync(SyntaxNode parent, SyntaxNode pattern, SyntaxNode value, EmitRange range)
    {
        var elements = Elements(pattern);
        if (elements.Count != 1 && (level == FlattenLevel.All || elements.Count == 0) || elements.All(n => n.Kind == K.OmittedExpression))
            value = await EnsureIdentifierAsync(value, !IsDeclaration(parent) || elements.Count != 0, range);
        List<SyntaxNode> retained = [];
        List<(SyntaxNode Temp, SyntaxNode Element)> rest = [];
        for (int i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            if (level == FlattenLevel.ObjectRest)
            {
                if (ContainsObjectRest(element) || transformedPriorElement && !IsSimple(element))
                {
                    transformedPriorElement = true;
                    var temp = context.NewTempVariable();
                    if (hoist) context.AddVariableDeclaration(temp);
                    rest.Add((temp, element));
                    retained.Add(binding ? F.NewBindingElement(null, null, temp, null) : temp);
                }
                else retained.Add(element);
            }
            else if (element.Kind != K.OmittedExpression)
            {
                var index = F.NewNumericLiteral(Utf8String.Format(i), TokenFlags.None);
                if (!Rest(element)) await FlattenAsync(element, F.NewElementAccessExpression(value, null, index, NodeFlags.None), Range(element), false);
                else if (i == elements.Count - 1)
                {
                    var slice = F.NewPropertyAccessExpression(value, null, F.NewIdentifier("slice"u8), NodeFlags.None);
                    var call = F.NewCallExpression(slice, null, null, new(i == 0 ? [] : [index]), NodeFlags.None);
                    await FlattenAsync(element, call, Range(element), false);
                }
            }
        }
        if (retained.Count != 0) await EmitAsync(Pattern(retained, true), value, range, pattern);
        foreach (var pair in rest) await FlattenAsync(pair.Element, pair.Temp, Range(pair.Element), false);
    }

    private SyntaxNode RestHelper(SyntaxNode value, NodeList elements, List<SyntaxNode> computed, EmitRange range)
    {
        context.RequestHelper(EmitHelpers.Rest);
        List<SyntaxNode> names = [];
        int offset = 0;
        foreach (var element in elements.Take(elements.Count - 1))
        {
            var name = PropertyName(element);
            if (name is ComputedPropertyNameNode)
            {
                var temp = computed[offset++];
                var condition = context.Binary(F.NewTypeOfExpression(temp), K.EqualsEqualsEqualsToken, F.NewStringLiteral("symbol"u8, TokenFlags.None));
                names.Add(F.NewConditionalExpression(condition, F.NewToken(K.QuestionToken), temp, F.NewToken(K.ColonToken), context.Binary(temp, K.PlusToken, F.NewStringLiteral(default, TokenFlags.None))));
            }
            else if (name is not null)
            {
                Utf8String text = name switch { IdentifierNode n => n.Text, StringLiteralNode n => n.Text, NumericLiteralNode n => n.Text, BigIntLiteralNode n => n.Text, NoSubstitutionTemplateLiteralNode n => n.Text, _ => default };
                var literal = F.NewStringLiteral(text, TokenFlags.None);
                context.SetTextSource(literal, name);
                names.Add(literal);
            }
        }
        var helper = F.NewIdentifier("__rest"u8);
        context.SetFlags(helper, EmitFlags.HelperName);
        return F.NewCallExpression(helper, null, null, new([value, At(F.NewArrayLiteralExpression(new(names.ToArray()), false), range)]), NodeFlags.None);
    }

    private static bool WalkTargets(SyntaxNode element, Func<SyntaxNode, SyntaxNode?, bool> predicate)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(element);
        while (pending.TryPop(out var item))
        {
            var target = Target(item);
            if (predicate(item, target)) return true;
            if (IsPattern(target)) foreach (var child in Elements(target!)) pending.Push(child);
        }
        return false;
    }
    private static bool AssignsTo(SyntaxNode element, Utf8String name) => WalkTargets(element, (_, target) => target is IdentifierNode id && id.Text == name);
    private static bool HasNonliteralComputedName(SyntaxNode element) => WalkTargets(element, (item, _) =>
        PropertyName(item) is ComputedPropertyNameNode { Expression: { } expression } && expression.Kind is not (>= K.FirstLiteralToken and <= K.LastLiteralToken));
    private static bool ContainsRest(SyntaxNode? node) => node?.DescendantsAndSelf().Any(Rest) == true;
    internal static bool ContainsObjectRest(SyntaxNode node) => WalkTargets(node, (item, target) => item is SpreadAssignmentNode
        || target is BindingPatternNode { Kind: K.ObjectBindingPattern, Elements: { } elements } && elements.Any(n => n is BindingElementNode { DotDotDotToken: not null }));
    private static bool IsSimple(SyntaxNode node) => !WalkTargets(node, (element, target) => target is not null && target.Kind != K.OmittedExpression
        && (PropertyName(element) is { } property && property is not (IdentifierNode or StringLiteralNode or NumericLiteralNode)
            || Initializer(element) is { } initializer && (initializer is IdentifierNode || !TransformSyntax.SimpleCopiable(initializer))
            || !IsPattern(target) && target is not IdentifierNode));
}
