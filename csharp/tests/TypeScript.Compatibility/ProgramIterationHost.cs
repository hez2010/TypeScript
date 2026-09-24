using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal sealed partial class ProgramTypeHost : IIteratorProtocolHost, IIterationElementHost, IGeneratorTypeHost, IYieldExpressionHost
{
    internal IteratorProtocols Iterators { get; }
    internal IterationElements Iteration { get; }
    internal GeneratorTypes Generators { get; }
    internal YieldExpressions Yields { get; }
    internal Action<string>? BeforeIterationGlobal { get; set; }
    private readonly Dictionary<(string Name, int Arity, bool Report), Type> iterationGlobals = [];
    internal List<(SyntaxNode Node, Type Type, bool Async, IReadOnlyList<IterationDiagnostic> Related)> DeferredIterationDiagnostics { get; } = [];
    internal List<(SyntaxNode Node, int Code)> IterationAwaitHints { get; } = [];

    public bool StrictBuiltinIteratorReturn => program.Symbols.Program.Configuration.Options.StrictOption("strictBuiltinIteratorReturn");

    public async ValueTask<Type> IterationGlobalAsync(string name, int arity, bool report, CancellationToken cancellation)
    {
        BeforeIterationGlobal?.Invoke(name);
        cancellation.ThrowIfCancellationRequested();
        var key = (name, arity, report);
        if (iterationGlobals.TryGetValue(key, out var cached))
            return cached;
        var type = await program.Globals.GetAsync(name, arity, report, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return iterationGlobals[key] = type;
    }

    public async ValueTask<IReadOnlyList<Type>> BuiltinIteratorsAsync(bool async, CancellationToken cancellation)
    {
        var types = new List<Type>();
        foreach (string name in async
            ? new[] { "ReadableStreamAsyncIterator" }
            : ["ArrayIterator", "MapIterator", "SetIterator", "StringIterator"])
            types.Add(await IterationGlobalAsync(name, 1, false, cancellation));
        return types;
    }

    public async ValueTask<string> KnownSymbolNameAsync(string name, CancellationToken cancellation)
    {
        if (program.Symbols.Lookup(program.Symbols.Globals, "Symbol", SymbolFlags.Value) is { } symbol
            && await Properties.PropertyAsync(await Values.GetAsync(symbol, cancellation), name, cancellation: cancellation) is { } property
            && await Values.GetAsync(property, cancellation) is { } type && (type.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0)
            return MappedMembers.PropertyName(type);
        return Symbol.InternalPrefix + "@" + name;
    }

    public void IterationDiagnostic(IterationDiagnostic diagnostic) => Error(diagnostic.Node, diagnostic.Code);

    public ValueTask<Type> CheckGeneratorOperandAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation) =>
        Expressions.CheckAsync(node, mode, cancellation);

    public void AsyncYieldHelpers(SyntaxNode node)
    {
        if (TargetYear < 2018 && program.Symbols.Program.Configuration.Options.Boolean("importHelpers") == true)
            throw new InvalidOperationException("Probe requires async generator emit helper validation");
    }

    public void DeferIteratorDiagnostic(SyntaxNode node, Type type, bool async, IReadOnlyList<IterationDiagnostic> related) =>
        DeferredIterationDiagnostics.Add((node, type, async, related));

    public void IterationError(SyntaxNode node, int code, bool missingAwait)
    {
        Error(node, code);
        if (missingAwait)
            IterationAwaitHints.Add((node, code));
    }

    private async ValueTask<Type> ForOfElementAsync(ForInOrOfStatementNode node, CancellationToken cancellation) =>
        await Iteration.CheckAsync(node.AwaitModifier is null ? IterationUse.ForOf : IterationUse.ForAwaitOf,
            await NonNullExpressionAsync(node.Expression!, cancellation), context.UndefinedType, node.Expression, cancellation);
}
