using System.Globalization;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed class GlobalTypes(TypeContext context, CheckerLinks links, CheckerSymbols symbols,
    TypeParameterScopes scopes, Action<SyntaxNode?, DiagnosticMessage, string[]> error)
{
    private readonly Dictionary<string, Type> types = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Name, int Arity), Symbol?> aliases = [];
    private IReadOnlyDictionary<string, Type>? typesView;
    internal IReadOnlyDictionary<string, Type> Types => typesView ??= types.AsReadOnly();
    internal Type? AnyArrayType { get; private set; }
    internal Type? AutoArrayType { get; private set; }
    internal Type? AnyReadonlyArrayType { get; private set; }

    private static string[] MissingArguments(string name)
        => LibraryFeatures.NameLibrary(name) is { } library ? [name, library] : [name];

    internal async ValueTask<Symbol?> AliasAsync(string name, int arity, DeclaredTypes declared, CancellationToken cancellation = default)
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
                    [name, arity.ToString(CultureInfo.InvariantCulture)]);
                symbol = null;
            }
        }
        cancellation.ThrowIfCancellationRequested();
        aliases[(name, arity)] = symbol;
        return symbol;
    }

    internal async ValueTask<Type> GetAsync(string name, int arity, bool reportErrors, CancellationToken cancellation = default)
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
                    [name, arity.ToString(CultureInfo.InvariantCulture)]);
        }
        else if (reportErrors)
            error(Declaration(symbol), Messages.Global_type_0_must_be_a_class_or_interface_type, [name]);
        return arity == 0 ? context.EmptyObjectType : context.EmptyGenericType;
    }

    internal async ValueTask InitializeAsync(CancellationToken cancellation = default)
    {
        links.Values.Get(symbols.UndefinedSymbol).ResolvedType = context.UndefinedWideningType;
        links.Values.Get(symbols.ArgumentsSymbol).ResolvedType = await GetAsync("IArguments", 0, true, cancellation).ConfigureAwait(false);
        links.Values.Get(symbols.UnknownSymbol).ResolvedType = context.ErrorType;
        links.Values.Get(symbols.GlobalThisSymbol).ResolvedType = context.NewObjectType(ObjectFlags.Anonymous, symbols.GlobalThisSymbol);
        types["Array"] = await GetAsync("Array", 1, true, cancellation).ConfigureAwait(false);
        types["Object"] = await GetAsync("Object", 0, true, cancellation).ConfigureAwait(false);
        types["Function"] = await GetAsync("Function", 0, true, cancellation).ConfigureAwait(false);
        bool strict = symbols.Program.Configuration.Options.StrictOption("strictBindCallApply");
        types["CallableFunction"] = strict
            ? await GetAsync("CallableFunction", 0, true, cancellation).ConfigureAwait(false)
            : types["Function"];
        types["NewableFunction"] = strict
            ? await GetAsync("NewableFunction", 0, true, cancellation).ConfigureAwait(false)
            : types["Function"];
        foreach (string name in new[] { "String", "Number", "Boolean", "RegExp" })
            types[name] = await GetAsync(name, 0, true, cancellation).ConfigureAwait(false);
        AnyArrayType = Reference(types["Array"], context.AnyType);
        AutoArrayType = Reference(types["Array"], context.AutoType);
        if (AutoArrayType == context.EmptyObjectType)
            AutoArrayType = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved);
        var readonlyArray = await GetAsync("ReadonlyArray", 1, false, cancellation).ConfigureAwait(false);
        types["ReadonlyArray"] = readonlyArray == context.EmptyGenericType ? types["Array"] : readonlyArray;
        AnyReadonlyArrayType = Reference(types["ReadonlyArray"], context.AnyType);
        types["ThisType"] = await GetAsync("ThisType", 1, false, cancellation).ConfigureAwait(false);
    }

    private Type Reference(Type target, Type argument)
        => target == context.EmptyGenericType ? context.EmptyObjectType : context.CreateTypeReference((InterfaceType)target, [argument]);

    private static SyntaxNode? Declaration(Symbol symbol)
        =>
            symbol.Declarations.FirstOrDefault(
                node => node.Kind is K.ClassDeclaration or K.InterfaceDeclaration or K.EnumDeclaration or K.TypeAliasDeclaration);
}
