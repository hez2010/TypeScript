using TypeScript.Compiler.Text;
using System.Globalization;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed class GlobalTypes(TypeContext context, CheckerLinks links, CheckerSymbols symbols,
    TypeParameterScopes scopes, Action<SyntaxNode?, DiagnosticMessage, Utf8String[]> error)
{
    private readonly Dictionary<Utf8String, Type> types = new();
    private readonly Dictionary<(Utf8String Name, int Arity), Symbol?> aliases = [];
    private IReadOnlyDictionary<Utf8String, Type>? typesView;
    internal IReadOnlyDictionary<Utf8String, Type> Types => typesView ??= types.AsReadOnly();
    internal Type? AnyArrayType { get; private set; }
    internal Type? AutoArrayType { get; private set; }
    internal Type? AnyReadonlyArrayType { get; private set; }

    private static Utf8String[] MissingArguments(Utf8String name)
        => LibraryFeatures.NameLibrary(name) is { } library ? [name, library] : [name];

    internal async ValueTask<Symbol?> AliasAsync(Utf8String name, int arity, DeclaredTypes declared, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (aliases.TryGetValue((name, arity), out var cached))
            return cached;
        var symbol = symbols.Lookup(symbols.Globals, name, SymbolFlags.TypeAlias);
        if (symbol is null)
            error(null, Messages.Cannot_find_global_type_0, MissingArguments(name));
        else
        {
            var declaration = symbol.Declarations.OfType<TypeAliasDeclarationNode>().First();
            await declared.GetAsync(symbol, cancellation).ConfigureAwait(false);
            if (links.TypeAliases.Get(symbol).TypeParameters?.Count != arity)
            {
                error(declaration, Messages.Global_type_0_must_have_1_type_parameter_s,
                    [name, Utf8String.Format(arity)]);
                symbol = null;
            }
        }
        cancellation.ThrowIfCancellationRequested();
        aliases[(name, arity)] = symbol;
        return symbol;
    }

    internal async ValueTask<Type> GetAsync(Utf8String name, int arity, bool reportErrors, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var symbol = symbols.Lookup(symbols.Globals, name, SymbolFlags.Type);
        if (symbol is null)
        {
            if (reportErrors)
                error(null, Messages.Cannot_find_global_type_0, MissingArguments(name));
        }
        else if ((symbol.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) != 0)
        {
            var type = await scopes.ClassOrInterfaceAsync(symbol, cancellation).ConfigureAwait(false);
            int count = type.AllTypeParameters.Count - (type.ThisType is null ? 0 : 1);
            if (count == arity)
                return type;
            if (reportErrors)
                error(Declaration(symbol), Messages.Global_type_0_must_have_1_type_parameter_s,
                    [name, Utf8String.Format(arity)]);
        }
        else if (reportErrors)
            error(Declaration(symbol), Messages.Global_type_0_must_be_a_class_or_interface_type, [name]);
        return arity == 0 ? context.EmptyObjectType : context.EmptyGenericType;
    }

    internal async ValueTask InitializeAsync(CancellationToken cancellation = default)
    {
        links.Values.Get(symbols.UndefinedSymbol).ResolvedType = context.UndefinedWideningType;
        links.Values.Get(symbols.ArgumentsSymbol).ResolvedType = await GetAsync(Utf8Literals.IArguments, 0, true, cancellation).ConfigureAwait(false);
        links.Values.Get(symbols.UnknownSymbol).ResolvedType = context.ErrorType;
        links.Values.Get(symbols.GlobalThisSymbol).ResolvedType = context.NewObjectType(ObjectFlags.Anonymous, symbols.GlobalThisSymbol);
        types[Utf8Literals.Array] = await GetAsync(Utf8Literals.Array, 1, true, cancellation).ConfigureAwait(false);
        types[Utf8Literals.ObjectType] = await GetAsync(Utf8Literals.ObjectType, 0, true, cancellation).ConfigureAwait(false);
        types[Utf8Literals.FunctionType] = await GetAsync(Utf8Literals.FunctionType, 0, true, cancellation).ConfigureAwait(false);
        bool strict = symbols.Program.Configuration.Options.EffectiveStrictBindCallApply;
        types[Utf8Literals.CallableFunction] = strict
            ? await GetAsync(Utf8Literals.CallableFunction, 0, true, cancellation).ConfigureAwait(false)
            : types[Utf8Literals.FunctionType];
        types[Utf8Literals.NewableFunction] = strict
            ? await GetAsync(Utf8Literals.NewableFunction, 0, true, cancellation).ConfigureAwait(false)
            : types[Utf8Literals.FunctionType];
        foreach (Utf8String name in new Utf8String[] { Utf8Literals.String, Utf8Literals.Number, Utf8Literals.Boolean, Utf8Literals.RegExp })
            types[name] = await GetAsync(name, 0, true, cancellation).ConfigureAwait(false);
        AnyArrayType = Reference(types[Utf8Literals.Array], context.AnyType);
        AutoArrayType = Reference(types[Utf8Literals.Array], context.AutoType);
        if (AutoArrayType == context.EmptyObjectType)
            AutoArrayType = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved);
        var readonlyArray = await GetAsync(Utf8Literals.ReadonlyArray, 1, false, cancellation).ConfigureAwait(false);
        types[Utf8Literals.ReadonlyArray] = readonlyArray == context.EmptyGenericType ? types[Utf8Literals.Array] : readonlyArray;
        AnyReadonlyArrayType = Reference(types[Utf8Literals.ReadonlyArray], context.AnyType);
        types[Utf8Literals.ThisType] = await GetAsync(Utf8Literals.ThisType, 1, false, cancellation).ConfigureAwait(false);
    }

    private Type Reference(Type target, Type argument)
        => target == context.EmptyGenericType ? context.EmptyObjectType : context.CreateTypeReference((InterfaceType)target, [argument]);

    private static SyntaxNode? Declaration(Symbol symbol)
        =>
            symbol.Declarations.FirstOrDefault(
                node => node.Kind is K.ClassDeclaration or K.InterfaceDeclaration or K.EnumDeclaration or K.TypeAliasDeclaration);
}
