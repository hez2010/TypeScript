using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private bool emitPromiseLookedUp;
    private Symbol? emitPromiseSymbol;

    internal async ValueTask<TypeReferenceSerializationKind> GetTypeReferenceSerializationKindAsync(SyntaxNode? name, SyntaxNode? location,
        CancellationToken cancellation = default)
    {
        if (name is null || location is null)
            return TypeReferenceSerializationKind.Unknown;
        using var query = await EnterQueryAsync(location, cancellation);
        RequireNode(name);
        bool typeOnly = false;
        if (name is QualifiedNameNode)
        {
            var first = name;
            while (first is QualifiedNameNode qualified)
                first = qualified.Left!;
            var root = await program.EntityNames.ResolveAsync(first, SymbolFlags.Value, true, true, location, cancellation);
            typeOnly = root is { Declarations.Length: > 0 } && root.Declarations.All(AliasResolver.IsTypeOnly);
        }
        var valueSymbol = await program.EntityNames.ResolveAsync(name, SymbolFlags.Value, true, true, location, cancellation);
        var value = valueSymbol is not null && (valueSymbol.Flags & SymbolFlags.Alias) != 0
            ? await program.Aliases.ResolveAsync(valueSymbol, cancellation) : valueSymbol;
        typeOnly |= valueSymbol is not null
            && await program.Aliases.TypeOnlyAsync(valueSymbol, SymbolFlags.Value, cancellation) is not null;
        var typeSymbol = await program.EntityNames.ResolveAsync(name, SymbolFlags.Type, true, true, location, cancellation);
        var declaredSymbol = typeSymbol is not null && (typeSymbol.Flags & SymbolFlags.Alias) != 0
            ? await program.Aliases.ResolveAsync(typeSymbol, cancellation) : typeSymbol;
        typeOnly |= typeSymbol is not null && await program.Aliases.TypeOnlyAsync(typeSymbol, SymbolFlags.Type, cancellation) is not null;
        if (value is not null && value == declaredSymbol)
        {
            if (!emitPromiseLookedUp)
            {
                emitPromiseSymbol = program.Symbols.NameResolver(cancellation).Resolve(null, "Promise", SymbolFlags.Value,
                    Messages.Cannot_find_global_value_0);
                emitPromiseLookedUp = true;
            }
            if (value == emitPromiseSymbol)
                return TypeReferenceSerializationKind.Promise;
            if (await ClassBases.IsConstructorAsync(await Values.GetAsync(value, cancellation), cancellation))
                return typeOnly
                    ? TypeReferenceSerializationKind.TypeWithCallSignature
                    : TypeReferenceSerializationKind.TypeWithConstructSignatureAndValue;
        }
        if (declaredSymbol is null)
            return typeOnly ? TypeReferenceSerializationKind.ObjectType : TypeReferenceSerializationKind.Unknown;
        var type = await Declared.GetAsync(declaredSymbol, cancellation);
        if (type == context.ErrorType)
            return typeOnly ? TypeReferenceSerializationKind.ObjectType : TypeReferenceSerializationKind.Unknown;
        if ((type.Flags & TypeFlags.AnyOrUnknown) != 0)
            return TypeReferenceSerializationKind.ObjectType;
        if (await AssignableKindAsync(type, TypeFlags.Void | TypeFlags.Nullable | TypeFlags.Never, cancellation))
            return TypeReferenceSerializationKind.VoidNullableOrNeverType;
        if (await AssignableKindAsync(type, TypeFlags.BooleanLike, cancellation))
            return TypeReferenceSerializationKind.BooleanType;
        if (await AssignableKindAsync(type, TypeFlags.NumberLike, cancellation))
            return TypeReferenceSerializationKind.NumberLikeType;
        if (await AssignableKindAsync(type, TypeFlags.BigIntLike, cancellation))
            return TypeReferenceSerializationKind.BigIntLikeType;
        if (await AssignableKindAsync(type, TypeFlags.StringLike, cancellation))
            return TypeReferenceSerializationKind.StringLikeType;
        if (type is TypeReference { Target: TupleType })
            return TypeReferenceSerializationKind.ArrayLikeType;
        if (await AssignableKindAsync(type, TypeFlags.ESSymbolLike, cancellation))
            return TypeReferenceSerializationKind.ESSymbolType;
        if ((type.Flags & TypeFlags.Object) != 0 && (await SignaturesAsync(type, false, cancellation)).Count != 0)
            return TypeReferenceSerializationKind.TypeWithCallSignature;
        return IsArray(type) ? TypeReferenceSerializationKind.ArrayLikeType : TypeReferenceSerializationKind.ObjectType;
    }
}
