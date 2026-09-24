using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : ITypePropertyHost, ITypeViewHost, ICompositeMemberHost
{
    internal TypeProperties Properties { get; }
    internal TypeViews Views { get; }
    internal CompositeMembers Composites { get; }
    public Type GlobalObject => program.Globals.Types["Object"];
    public Type GlobalFunction => program.Globals.Types["Function"];
    public Type GlobalCallableFunction => program.Globals.Types["CallableFunction"];
    public Type GlobalNewableFunction => program.Globals.Types["NewableFunction"];

    public ValueTask<Type> ReducedApparentAsync(Type type, CancellationToken cancellation) =>
        Views.ReducedApparentAsync(type, cancellation);

    public ValueTask<StructuredType> ResolveAsync(StructuredType type, CancellationToken cancellation) =>
        Members.ResolveAsync(type, cancellation);

    public ValueTask<Type> GlobalAsync(string name, CancellationToken cancellation) => program.Globals.Types.TryGetValue(name, out var type)
            ? ValueTask.FromResult(type) : program.Globals.GetAsync(name, 0, false, cancellation);

    public ValueTask<Type> WithThisAsync(Type type, Type argument, bool apparent, CancellationToken cancellation) =>
        Bases.WithThisAsync(type, argument, apparent, cancellation);

    public ValueTask<IReadOnlyList<Symbol>> CompositePropertiesAsync(UnionOrIntersectionType type, CancellationToken cancellation)
            => Properties.CompositePropertiesAsync(type, cancellation);

    public bool IsArray(Type type) => Instantiation.IsArrayType(type);

    public ValueTask<bool> UnknownLikeUnionAsync(Type type, CancellationToken cancellation) =>
        Views.UnknownLikeUnionAsync(type, cancellation);

    public async ValueTask<IndexInfo?> ApplicableIndexAsync(Type type, string name, CancellationToken cancellation)
            => await IndexSignatures.ApplicableAsync(await IndexesAsync(type, cancellation),
                name.StartsWith(Symbol.InternalPrefix + "@", StringComparison.Ordinal)
                    ? context.ESSymbolType
                    : context.GetStringLiteralType(name),
                cancellation);

    public ValueTask<Type?> TupleRestAsync(TypeReference type, CancellationToken cancellation)
            => Instantiation.Tuples.SliceElementAsync(type, ((TupleType)type.Target!).FixedLength, cancellation: cancellation);

    public ValueTask<Symbol?> PropertyAsync(Type type, string name, CancellationToken cancellation)
            => Properties.PropertyAsync(type, name, cancellation: cancellation);

    public async ValueTask<IndexInfo?> ApplicableIndexAsync(Type type, Type key, CancellationToken cancellation)
            => await IndexSignatures.ApplicableAsync(await IndexesAsync(type, cancellation), key, cancellation);

    public async ValueTask<Type> PropertyNameTypeAsync(Symbol symbol, CancellationToken cancellation)
    {
        symbol = await LateMembers.SymbolAsync(symbol, cancellation);
        if (links.Values.Get(symbol).NameType is { } cached)
            return (cached.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0 ? cached : context.NeverType;
        var name = symbol.ValueDeclaration is { } declaration ? LateMembers.Name(declaration) : null;
        var type = name switch
        {
            NumericLiteralNode numeric => ((LiteralType)await LiteralExpressionAsync(numeric, cancellation)).RegularType!,
            PrivateIdentifierNode => context.NeverType,
            StringLiteralNode text => context.GetStringLiteralType(text.Text),
            IdentifierNode identifier => context.GetStringLiteralType(identifier.Text),
            JsxNamespacedNameNode namespaced => context.GetStringLiteralType(JsxName(namespaced)),
            NoSubstitutionTemplateLiteralNode template => context.GetStringLiteralType(template.Text),
            ComputedPropertyNameNode computed => await Algebra.RegularTypeAsync(
                await ComputedNameAsync(computed, cancellation),
                cancellation),
            ElementAccessExpressionNode or PropertyAccessExpressionNode or BigIntLiteralNode => await Algebra.RegularTypeAsync(
                await Expressions.CheckAsync(name, cancellation: cancellation),
                cancellation),
            null when !symbol.Name.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal) => context.GetStringLiteralType(symbol.Name),
            _ => context.NeverType
        };
        return (type.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0 ? type : context.NeverType;
    }

    public ValueTask<Type> ParameterTypeAsync(Symbol parameter, CancellationToken cancellation) =>
        Parameters.ParameterAsync(parameter, cancellation);

    public async ValueTask<Type?> ArrayElementAsync(Type type, CancellationToken cancellation)
            => Instantiation.IsArrayType(type) ? (await References.TypeArgumentsAsync((TypeReference)type, cancellation))[0] : null;

    public ValueTask<IReadOnlyList<Signature>> UnionSignaturesAsync(
        IReadOnlyList<IReadOnlyList<Signature>> signatures,
        CancellationToken cancellation)
            => SignatureComposition.UnionAsync(signatures, cancellation);

    public ValueTask<IReadOnlyList<Signature>> ArrayMemberSignaturesAsync(UnionType type, CancellationToken cancellation)
            => SignatureComposition.ArrayMembersAsync(type, cancellation);

    public async ValueTask<bool> IdenticalSignaturesAsync(Signature left, Signature right, CancellationToken cancellation)
            => await SignatureComparison.CompareAsync(left, right, cancellation: cancellation) != Ternary.False;

    public bool IsReadonly(Symbol symbol)
    {
        return (symbol.CheckFlags & CheckFlags.Readonly) != 0
            || (symbol.Flags & SymbolFlags.Property) != 0
                && symbol.ValueDeclaration is { } declaration
                && SemanticSyntax.HasModifier(declaration, SyntaxKind.ReadonlyKeyword)
            || (symbol.Flags & SymbolFlags.Variable) != 0
                && AssignmentMarks.Constant(symbol)
            || (symbol.Flags & SymbolFlags.Accessor) != 0
                && (symbol.Flags & SymbolFlags.SetAccessor) == 0 || (symbol.Flags & SymbolFlags.EnumMember) != 0
            || symbol.Declarations.OfType<CallExpressionNode>().Any(d => ReadonlyDescriptorAsync(d).GetAwaiter().GetResult());
    }

    public CheckFlags AccessFlags(Symbol symbol, bool write)
    {
        var publicFlag = write ? CheckFlags.ContainsWritePublic : CheckFlags.ContainsPublic;
        var protectedFlag = write ? CheckFlags.ContainsWriteProtected : CheckFlags.ContainsProtected;
        var privateFlag = write ? CheckFlags.ContainsWritePrivate : CheckFlags.ContainsPrivate;
        if ((symbol.CheckFlags & CheckFlags.Synthetic) != 0)
            return ((symbol.CheckFlags & publicFlag) != 0 ? publicFlag : (symbol.CheckFlags & protectedFlag) != 0 ? protectedFlag
                : (symbol.CheckFlags & privateFlag) != 0 ? privateFlag : publicFlag) | symbol.CheckFlags & CheckFlags.ContainsStatic;
        SyntaxNode? declaration = null;
        if (symbol.ValueDeclaration is not null)
        {
            if (write)
                declaration = symbol.Declarations.OfType<SetAccessorDeclarationNode>().FirstOrDefault();
            if (declaration is null && (symbol.Flags & SymbolFlags.GetAccessor) != 0)
                declaration = symbol.Declarations.OfType<GetAccessorDeclarationNode>().FirstOrDefault();
            declaration ??= symbol.ValueDeclaration;
        }
        if (declaration is null)
            return publicFlag | ((symbol.Flags & SymbolFlags.Prototype) != 0 ? CheckFlags.ContainsStatic : 0);
        var access = publicFlag;
        if (symbol.Parent is { } parent
            && (parent.Flags & SymbolFlags.Class) != 0
            && !SemanticSyntax.HasModifier(declaration, SyntaxKind.PublicKeyword))
            access = SemanticSyntax.HasModifier(declaration, SyntaxKind.ProtectedKeyword) ? protectedFlag
                : SemanticSyntax.HasModifier(declaration, SyntaxKind.PrivateKeyword) ? privateFlag : publicFlag;
        return access | (SemanticSyntax.HasModifier(declaration, SyntaxKind.StaticKeyword) ? CheckFlags.ContainsStatic : 0);
    }
}
