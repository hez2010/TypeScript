using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

// Type-node substitutions are distinct from control-flow analysis of expressions.
// Resolve the surrounding true branches and homomorphic key constraints lazily.
internal sealed class TypeNodeFlow(TypeContext context, TypeAlgebra algebra, MappedTypes mapped, IMappedTypeHost host)
{
    internal async ValueTask<Type> ApplyAsync(Type type, SyntaxNode? node, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        var constraints = new List<Type>();
        bool covariant = true;
        while (node is not null && !Statement(node) && node.Kind != K.JSDoc)
        {
            cancellation.ThrowIfCancellationRequested();
            var parent = node.Parent;
            if (parent is ParameterDeclarationNode)
                covariant = !covariant;
            if ((covariant || (type.Flags & TypeFlags.TypeVariable) != 0)
                && parent is ConditionalTypeNode conditional && node == conditional.TrueType)
            {
                var constraint = await ImpliedAsync(
                    type,
                    conditional.CheckType!,
                    conditional.ExtendsType!,
                    cancellation).ConfigureAwait(false);
                if (constraint is not null)
                    constraints.Add(constraint);
            }
            else if (type is TypeParameter && parent is MappedTypeNode { NameType: null } mappedNode && node == mappedNode.Type)
            {
                var mappedType = (MappedType)await host.TypeFromNodeAsync(mappedNode, cancellation).ConfigureAwait(false);
                if (await mapped.ParameterAsync(mappedType, cancellation).ConfigureAwait(false)
                    == await mapped.ActualVariableAsync(type, cancellation).ConfigureAwait(false))
                {
                    var parameter = await mapped.HomomorphicVariableAsync(mappedType, cancellation).ConfigureAwait(false);
                    if (parameter is TypeParameter typeParameter)
                    {
                        var constraint = await host.ParameterConstraintAsync(typeParameter, cancellation).ConfigureAwait(false);
                        if (constraint is not null
                            && (constraint is UnionType union ? union.Types.All(ArrayOrTuple) : ArrayOrTuple(constraint)))
                            constraints.Add(
                                await algebra.UnionAsync(
                                    [context.NumberType, context.NumericStringType],
                                    cancellation: cancellation).ConfigureAwait(false));
                    }
                }
            }
            node = parent;
        }
        return constraints.Count == 0 ? type : context.GetSubstitutionType(type,
            await algebra.IntersectionAsync(constraints, cancellation: cancellation).ConfigureAwait(false));
    }

    internal async ValueTask<Type?> ImpliedAsync(Type type, SyntaxNode check, SyntaxNode extends,
        CancellationToken cancellation = default)
    {
        while (check is TupleTypeNode { Elements.Count: 1 } tupleCheck && extends is TupleTypeNode { Elements.Count: 1 } tupleExtends)
        {
            cancellation.ThrowIfCancellationRequested();
            check = tupleCheck.Elements[0];
            extends = tupleExtends.Elements[0];
        }
        var checkType = await host.TypeFromNodeAsync(check, cancellation).ConfigureAwait(false);
        if (await mapped.ActualVariableAsync(checkType, cancellation).ConfigureAwait(false)
            == await mapped.ActualVariableAsync(type, cancellation).ConfigureAwait(false))
        {
            var result = await host.TypeFromNodeAsync(extends, cancellation).ConfigureAwait(false);
            context.RequireOwned(result);
            return result;
        }
        return null;
    }

    private bool ArrayOrTuple(Type type) => host.IsArrayType(type) || type is TypeReference { Target: TupleType };

    internal static bool Statement(SyntaxNode node) => node.Kind switch
    {
        K.BreakStatement or K.ContinueStatement or K.DebuggerStatement or K.DoStatement or K.ExpressionStatement
            or K.EmptyStatement or K.ForInStatement or K.ForOfStatement or K.ForStatement or K.IfStatement
            or K.LabeledStatement or K.ReturnStatement or K.SwitchStatement or K.ThrowStatement or K.TryStatement
            or K.VariableStatement or K.WhileStatement or K.WithStatement or K.NotEmittedStatement
            or K.FunctionDeclaration or K.MissingDeclaration or K.ClassDeclaration or K.InterfaceDeclaration
            or K.TypeAliasDeclaration or K.JSTypeAliasDeclaration or K.EnumDeclaration or K.ModuleDeclaration
            or K.ImportDeclaration or K.JSImportDeclaration or K.ImportEqualsDeclaration or K.ExportDeclaration
            or K.ExportAssignment or K.NamespaceExportDeclaration => true,
        K.Block => node.Parent?.Kind is not (K.TryStatement or K.CatchClause)
            && !(node.Parent is { } parent && (SemanticSyntax.FunctionDeclarationLike(parent)
                || parent.Kind is K.MethodSignature or K.CallSignature or K.JSDocSignature or K.ConstructSignature
                    or K.IndexSignature or K.FunctionType or K.ConstructorType)),
        _ => false
    };
}
