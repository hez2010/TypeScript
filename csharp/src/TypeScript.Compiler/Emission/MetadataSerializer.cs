using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed class MetadataSerializer(EmitContext context, Checker checker, CompilerOptions options,
    SyntaxNode lexicalScope, SyntaxNode? nameScope, CancellationToken cancellation)
{
    private NodeFactory F => context.Factory;
    private IdentifierNode Id(Utf8String text) => F.NewIdentifier(text);
    private bool StrictNullChecks => options.StrictNullChecks ?? options.Strict == true;

    internal ValueTask<SyntaxNode> TypeOfNodeAsync(SyntaxNode node, SyntaxNode? container) => node switch
    {
        PropertyDeclarationNode or ParameterDeclarationNode => TypeAsync(((ITypedNode)node).Type),
        GetAccessorDeclarationNode or SetAccessorDeclarationNode => TypeAsync(AccessorType(node, container)),
        ClassDeclarationNode or ClassExpressionNode or MethodDeclarationNode => ValueTask.FromResult<SyntaxNode>(Id("Function"u8)),
        _ => ValueTask.FromResult<SyntaxNode>(context.VoidZero())
    };

    private static SyntaxNode? AccessorType(SyntaxNode node, SyntaxNode? container)
    {
        var pair = DecoratorSyntax.Accessors(DecoratorSyntax.Members(container), node);
        if (pair.Setter is { } setter)
            return SetterParameter(setter)?.Type;
        return pair.Getter?.Type;
    }

    internal static ParameterDeclarationNode? SetterParameter(SetAccessorDeclarationNode setter)
    {
        if (setter.Parameters is not { Count: > 0 } parameters) return null;
        return (ParameterDeclarationNode)parameters[parameters.Count >= 2 && DecoratorSyntax.ThisParameter(parameters[0]) ? 1 : 0];
    }

    internal async ValueTask<SyntaxNode> ParameterTypesAsync(SyntaxNode node, SyntaxNode? container)
    {
        var declaration = node is ClassDeclarationNode or ClassExpressionNode ? DecoratorSyntax.Constructor(node)
            : node is IFunctionSignature && DecoratorSyntax.Body(node) is not null ? node : null;
        List<SyntaxNode> values = [];
        if (declaration is not null)
        {
            var parameters = ((IFunctionSignature)declaration).Parameters;
            if (declaration is GetAccessorDeclarationNode && container is not null
                && DecoratorSyntax.Accessors(DecoratorSyntax.Members(container), declaration).Setter is { } setter)
                parameters = setter.Parameters;
            for (int i = 0; i < parameters!.Count; i++)
            {
                var parameter = (ParameterDeclarationNode)parameters[i];
                if (i == 0 && DecoratorSyntax.ThisParameter(parameter)) continue;
                values.Add(parameter.DotDotDotToken is not null ? await TypeAsync(parameter.Type switch
                {
                    ArrayTypeNode array => array.ElementType,
                    TypeReferenceNode { TypeArguments.Count: > 0 } reference => reference.TypeArguments[0],
                    _ => null
                }) : await TypeOfNodeAsync(parameter, container));
            }
        }
        return F.NewArrayLiteralExpression(new(values.ToArray()), false);
    }

    internal ValueTask<SyntaxNode> ReturnTypeAsync(SyntaxNode node)
    {
        if (node is IFunctionSignature { Type: { } type }) return TypeAsync(type);
        bool async = node is FunctionDeclarationNode { AsteriskToken: null } or FunctionExpressionNode { AsteriskToken: null }
            or ArrowFunctionNode or MethodDeclarationNode { AsteriskToken: null };
        return ValueTask.FromResult<SyntaxNode>(async && DecoratorSyntax.Body(node) is not null && SemanticSyntax.HasModifier(node, K.AsyncKeyword)
            ? Id("Promise"u8) : context.VoidZero());
    }

    private async ValueTask<SyntaxNode> TypeAsync(SyntaxNode? node, bool conditionalBranch = false)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (node is null) return Id("Object"u8);
        while (node is ParenthesizedTypeNode paren) node = paren.Type!;
        switch (node.Kind)
        {
            case K.VoidKeyword: case K.UndefinedKeyword: case K.NeverKeyword: return context.VoidZero();
            case K.FunctionType: case K.ConstructorType: return Id("Function"u8);
            case K.ArrayType: case K.TupleType: return Id("Array"u8);
            case K.TypePredicate: return ((TypePredicateNode)node).AssertsModifier is not null ? context.VoidZero() : Id("Boolean"u8);
            case K.BooleanKeyword: return Id("Boolean"u8);
            case K.TemplateLiteralType: case K.StringKeyword: return Id("String"u8);
            case K.LiteralType: return LiteralType(((LiteralTypeNode)node).Literal!);
            case K.NumberKeyword: return Id("Number"u8);
            case K.BigIntKeyword: return BigIntConstructor();
            case K.SymbolKeyword: return Id("Symbol"u8);
            case K.TypeReference: return await TypeReferenceAsync((TypeReferenceNode)node, conditionalBranch);
            case K.IntersectionType: return await ConstituentsAsync(((IntersectionTypeNode)node).Types!, true, conditionalBranch);
            case K.UnionType: return await ConstituentsAsync(((UnionTypeNode)node).Types!, false, conditionalBranch);
            case K.ConditionalType:
                var conditional = (ConditionalTypeNode)node;
                return await ConstituentsAsync([conditional.TrueType!, conditional.FalseType!], false, true);
            case K.TypeOperator when ((TypeOperatorNode)node).Operator == K.ReadonlyKeyword:
            case K.JSDocNullableType: case K.JSDocNonNullableType: case K.JSDocOptionalType:
                return await TypeAsync(((ITypedNode)node).Type, conditionalBranch);
            default: return Id("Object"u8);
        }
    }

    private async ValueTask<SyntaxNode> ConstituentsAsync(IEnumerable<SyntaxNode> types, bool intersection, bool conditionalBranch)
    {
        SyntaxNode? serialized = null;
        foreach (var item in types)
        {
            var type = item;
            while (type is ParenthesizedTypeNode paren) type = paren.Type!;
            if (type.Kind == K.NeverKeyword)
            {
                if (intersection) return context.VoidZero();
                continue;
            }
            if (type.Kind == K.UnknownKeyword)
            {
                if (!intersection) return Id("Object"u8);
                continue;
            }
            if (type.Kind == K.AnyKeyword) return Id("Object"u8);
            if (!StrictNullChecks && (type is LiteralTypeNode { Literal.Kind: K.NullKeyword } || type.Kind == K.UndefinedKeyword)) continue;
            var value = await TypeAsync(type, conditionalBranch);
            if (value is IdentifierNode id && id.Text == "Object"u8) return value;
            if (serialized is not null && !Equivalent(serialized, value)) return Id("Object"u8);
            serialized ??= value;
        }
        return serialized ?? context.VoidZero();
    }

    private SyntaxNode LiteralType(SyntaxNode literal)
    {
        while (literal is PrefixUnaryExpressionNode unary) literal = unary.Operand!;
        return literal.Kind switch
        {
            K.StringLiteral or K.NoSubstitutionTemplateLiteral => Id("String"u8),
            K.NumericLiteral => Id("Number"u8), K.BigIntLiteral => BigIntConstructor(),
            K.TrueKeyword or K.FalseKeyword => Id("Boolean"u8), K.NullKeyword => context.VoidZero(),
            _ => throw new InvalidOperationException($"Invalid metadata literal {literal.Kind}")
        };
    }

    private async ValueTask<SyntaxNode> TypeReferenceAsync(TypeReferenceNode node, bool conditionalBranch)
    {
        var kind = await checker.GetTypeReferenceSerializationKindAsync(ContextNode(node.TypeName), ContextNode(nameScope ?? lexicalScope), cancellation);
        switch (kind)
        {
            case TypeReferenceSerializationKind.Unknown:
                if (conditionalBranch) return Id("Object"u8);
                var value = EntityFallback(node.TypeName!);
                var temp = context.NewTempVariable();
                context.AddVariableDeclaration(temp);
                return Conditional(TypeCheck(context.Binary(temp, K.EqualsToken, value), "function"u8), temp, Id("Object"u8));
            case TypeReferenceSerializationKind.TypeWithConstructSignatureAndValue: return Entity(node.TypeName!);
            case TypeReferenceSerializationKind.VoidNullableOrNeverType: return context.VoidZero();
            case TypeReferenceSerializationKind.BigIntLikeType: return BigIntConstructor();
            case TypeReferenceSerializationKind.BooleanType: return Id("Boolean"u8);
            case TypeReferenceSerializationKind.NumberLikeType: return Id("Number"u8);
            case TypeReferenceSerializationKind.StringLikeType: return Id("String"u8);
            case TypeReferenceSerializationKind.ArrayLikeType: return Id("Array"u8);
            case TypeReferenceSerializationKind.ESSymbolType: return Id("Symbol"u8);
            case TypeReferenceSerializationKind.TypeWithCallSignature: return Id("Function"u8);
            case TypeReferenceSerializationKind.Promise: return Id("Promise"u8);
            case TypeReferenceSerializationKind.ObjectType: return Id("Object"u8);
            default: throw new InvalidOperationException($"Invalid metadata serialization kind {kind}");
        }
    }
    private SyntaxNode? ContextNode(SyntaxNode? node) => node is null ? null : context.ParseNode(node);
    private SyntaxNode TypeCheck(SyntaxNode expression, Utf8String text) => context.Binary(F.NewTypeOfExpression(expression), K.EqualsEqualsEqualsToken, F.NewStringLiteral(text, TokenFlags.None));
    private ConditionalExpressionNode Conditional(SyntaxNode condition, SyntaxNode whenTrue, SyntaxNode whenFalse) =>
        F.NewConditionalExpression(condition, F.NewToken(K.QuestionToken), whenTrue, F.NewToken(K.ColonToken), whenFalse);
    private SyntaxNode BigIntConstructor() => options.EmitTargetYear >= 2020 ? Id("BigInt"u8)
        : Conditional(TypeCheck(Id("BigInt"u8), "function"u8), Id("BigInt"u8), Id("Object"u8));

    private SyntaxNode Entity(SyntaxNode node)
    {
        var names = new Stack<QualifiedNameNode>();
        while (node is QualifiedNameNode qualified) { names.Push(qualified); node = qualified.Left!; }
        var result = context.Clone(node);
        context.UnsetOriginal(result);
        result.Parent = ContextNode(lexicalScope);
        while (names.TryPop(out var name))
            result = F.NewPropertyAccessExpression(result, null, name.Right, NodeFlags.None);
        return result;
    }

    private SyntaxNode EntityFallback(SyntaxNode node)
    {
        var pending = new Stack<QualifiedNameNode>();
        while (node is QualifiedNameNode { Left: QualifiedNameNode } qualified)
        {
            pending.Push(qualified);
            node = qualified.Left!;
        }
        var left = node is QualifiedNameNode pair ? Entity(pair.Left!) : Entity(node);
        var right = node is QualifiedNameNode ? Entity(node) : left;
        var result = context.Binary(context.Binary(F.NewTypeOfExpression(left), K.ExclamationEqualsEqualsToken, F.NewStringLiteral("undefined"u8, TokenFlags.None)), K.AmpersandAmpersandToken, right);
        while (pending.TryPop(out var name))
        {
            var temp = context.NewTempVariable();
            context.AddVariableDeclaration(temp);
            var defined = context.Binary(context.Binary(temp, K.EqualsToken, result.Right!), K.ExclamationEqualsEqualsToken, context.VoidZero());
            result = context.Binary(context.Binary(result.Left!, K.AmpersandAmpersandToken, defined), K.AmpersandAmpersandToken,
                F.NewPropertyAccessExpression(temp, null, name.Right, NodeFlags.None));
        }
        return result;
    }

    private bool Equivalent(SyntaxNode left, SyntaxNode right)
    {
        var pending = new Stack<(SyntaxNode, SyntaxNode)>();
        pending.Push((left, right));
        while (pending.TryPop(out var pair))
        {
            (left, right) = pair;
            if (context.GetAutoGenerateInfo(left) is not null)
            {
                if (context.GetAutoGenerateInfo(right) is null) return false;
                continue;
            }
            if (left.Kind != right.Kind) return false;
            switch (left)
            {
                case IdentifierNode id: if (id.Text != ((IdentifierNode)right).Text) return false; break;
                case StringLiteralNode literal: if (literal.Text != ((StringLiteralNode)right).Text) return false; break;
                case PropertyAccessExpressionNode access:
                    pending.Push((access.Expression!, ((PropertyAccessExpressionNode)right).Expression!));
                    pending.Push((access.Name!, ((PropertyAccessExpressionNode)right).Name!));
                    break;
                case VoidExpressionNode expression:
                    if (expression.Expression is not NumericLiteralNode a || a.Text != "0"u8 || ((VoidExpressionNode)right).Expression is not NumericLiteralNode b || b.Text != "0"u8) return false;
                    break;
                case TypeOfExpressionNode expression: pending.Push((expression.Expression!, ((TypeOfExpressionNode)right).Expression!)); break;
                case ParenthesizedExpressionNode expression: pending.Push((expression.Expression!, ((ParenthesizedExpressionNode)right).Expression!)); break;
                case ConditionalExpressionNode conditional:
                    var other = (ConditionalExpressionNode)right;
                    pending.Push((conditional.Condition!, other.Condition!));
                    pending.Push((conditional.WhenTrue!, other.WhenTrue!));
                    pending.Push((conditional.WhenFalse!, other.WhenFalse!));
                    break;
                case BinaryExpressionNode binary:
                    var otherBinary = (BinaryExpressionNode)right;
                    if (binary.OperatorToken!.Kind != otherBinary.OperatorToken!.Kind) return false;
                    pending.Push((binary.Left!, otherBinary.Left!));
                    pending.Push((binary.Right!, otherBinary.Right!));
                    break;
                default: return false;
            }
        }
        return true;
    }
}
