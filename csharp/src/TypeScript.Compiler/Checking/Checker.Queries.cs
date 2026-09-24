using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Programs;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly SemaphoreSlim queryGate = new(1, 1);
    internal TypeContext Context => context;
    internal CheckerLinks Links => links;
    internal CheckerEnvironment Environment => program;
    internal CheckerSymbols Symbols => program.Symbols;

    internal static async ValueTask<Checker> CreateAsync(CompilerProgram program, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var options = program.Configuration.Options;
        var context = new TypeContext(options.StrictOption("strictNullChecks"), options.Boolean("exactOptionalPropertyTypes") == true);
        var links = new CheckerLinks();
        var environment = new CheckerEnvironment(context, links);
        Checker? checker = null;
        await CheckerSymbols.CreateAsync(program, links, environment, cancellation,
            _ => checker = new(context, links, environment)).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return checker!;
    }

    internal async ValueTask<Type> GetExpressionTypeAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        await queryGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            RequireNode(node);
            RequireUsable();
            return await Expressions.CheckAsync(node, cancellation: cancellation).ConfigureAwait(false);
        }
        finally
        {
            queryGate.Release();
        }
    }

    internal async ValueTask<Type> GetTypeFromTypeNodeAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        await queryGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            RequireNode(node);
            RequireUsable();
            return await Nodes.FromNodeAsync(node, cancellation).ConfigureAwait(false);
        }
        finally
        {
            queryGate.Release();
        }
    }

    private void RequireNode(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (program.Symbols.Binding(node) is null)
            throw new ArgumentException("Syntax belongs to another program", nameof(node));
    }
}
