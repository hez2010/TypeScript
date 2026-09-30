using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IIteratorProtocolHost, IIterationElementHost, IGeneratorTypeHost, IYieldExpressionHost
{
    internal IteratorProtocols Iterators { get; }
    internal IterationElements Iteration { get; }
    internal GeneratorTypes Generators { get; }
    internal YieldExpressions Yields { get; }
    internal Action<Utf8String>? BeforeIterationGlobal { get; set; }
    private readonly Dictionary<(Utf8String Name, int Arity, bool Report), Type> iterationGlobals = [];
    internal List<(SyntaxNode Node, Type Type, bool Async, IReadOnlyList<IterationDiagnostic> Related)> DeferredIterationDiagnostics { get; } = [];
    internal List<(SyntaxNode Node, DiagnosticCode Code)> IterationAwaitHints { get; } = [];

    public bool StrictBuiltinIteratorReturn => program.Symbols.Program.Configuration.Options.EffectiveStrictBuiltinIteratorReturn;

    public async ValueTask<Type> IterationGlobalAsync(Utf8String name, int arity, bool report, CancellationToken cancellation)
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
        foreach (Utf8String name in async
            ? new Utf8String[] { Utf8Literals.ReadableStreamAsyncIterator }
            : [Utf8String.Copy("ArrayIterator"u8), Utf8String.Copy("MapIterator"u8), Utf8String.Copy("SetIterator"u8), Utf8String.Copy("StringIterator"u8)])
            types.Add(await IterationGlobalAsync(name, 1, false, cancellation));
        return types;
    }

    public async ValueTask<Utf8String> KnownSymbolNameAsync(Utf8String name, CancellationToken cancellation)
    {
        if (program.Symbols.Lookup(program.Symbols.Globals, Utf8Literals.Symbol, SymbolFlags.Value) is { } symbol
            && await Properties.PropertyAsync(await Values.GetAsync(symbol, cancellation), name, cancellation: cancellation) is { } property
            && await Values.GetAsync(property, cancellation) is { } type && (type.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0)
            return MappedMembers.PropertyName(type);
        return Utf8String.Concat(Symbol.InternalUnique, name);
    }

    public async ValueTask IterationDiagnosticAsync(IterationDiagnostic diagnostic, CancellationToken cancellation)
        => Error(diagnostic.Node, await IteratorDiagnosticAsync(diagnostic, cancellation));

    private async ValueTask<Diagnostic> IteratorDiagnosticAsync(IterationDiagnostic diagnostic, CancellationToken cancellation)
    {
        if (diagnostic.Source is { } source && diagnostic.Target is { } target)
        {
            var previous = relationDiagnosticOutput;
            var output = new List<(SyntaxNode Node, Diagnostic Diagnostic)>();
            relationDiagnosticOutput = output;
            try
            {
                await ReportRelationMessageAsync(diagnostic.Node, diagnostic.Code, source, target, RelationKind.Assignable, cancellation);
                return output.Single().Diagnostic;
            }
            finally
            {
                relationDiagnosticOutput = previous;
            }
        }
        return CheckerDiagnostic.Create(diagnostic.Node, DiagnosticLocalization.GetMessage(diagnostic.Code),
            diagnostic.Code is DiagnosticCode.AnIteratorMustHaveANextMethod
                or DiagnosticCode.TheTypeReturnedByThe0MethodOfAnIteratorMustHaveAValueProperty
                or DiagnosticCode.AnAsyncIteratorMustHaveANextMethod
                or DiagnosticCode.TheTypeReturnedByThe0MethodOfAnAsyncIteratorMustBeAPromiseForATypeWithAValueProperty
                or DiagnosticCode.The0PropertyOfAnIteratorMustBeAMethod or DiagnosticCode.The0PropertyOfAnAsyncIteratorMustBeAMethod
                ? [diagnostic.Member!.Value]
                : []);
    }

    public ValueTask<bool> ReportGeneratorReturnAsync(Type source, Type target, SyntaxNode node, CancellationToken cancellation)
        => RelationDiagnostics.CheckAsync(source, target, RelationKind.Assignable, node, null, cancellation: cancellation);

    public ValueTask<Type> CheckGeneratorOperandAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation) =>
        Expressions.CheckAsync(node, mode, cancellation);

    public ValueTask AsyncYieldHelpersAsync(SyntaxNode node, CancellationToken cancellation)
        =>
            TargetYear < 2018
                ? ExternalHelpersAsync(node, [Utf8Literals.Await, Utf8Literals.AsyncDelegator, Utf8Literals.AsyncValues], cancellation)
                : ValueTask.CompletedTask;

    public void DeferIteratorDiagnostic(SyntaxNode node, Type type, bool async, IReadOnlyList<IterationDiagnostic> related) =>
        DeferredIterationDiagnostics.Add((node, type, async, related));

    public async ValueTask IterationErrorAsync(SyntaxNode node, DiagnosticCode code, bool missingAwait, Type type, Type? other,
        CancellationToken cancellation, IReadOnlyList<IterationDiagnostic>? related = null)
    {
        Utf8String text = await TypeDisplay.GetAsync(type, cancellation);
        var information = new List<Diagnostic>();
        if (related is not null)
            foreach (var item in related)
                information.Add(await IteratorDiagnosticAsync(item, cancellation));
        Error(node, CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code),
            other is null ? [text] : [text, await TypeDisplay.GetAsync(other, cancellation)]) with
        { RelatedInformation = information });
        if (missingAwait)
            IterationAwaitHints.Add((node, code));
    }

    private async ValueTask<Type> ForOfElementAsync(ForInOrOfStatementNode node, CancellationToken cancellation) =>
        await Iteration.CheckAsync(node.AwaitModifier is null ? IterationUse.ForOf : IterationUse.ForAwaitOf,
            await NonNullExpressionAsync(node.Expression!, cancellation), context.UndefinedType, node.Expression, cancellation);
}
