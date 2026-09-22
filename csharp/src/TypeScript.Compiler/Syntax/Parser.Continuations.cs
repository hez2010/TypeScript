using TypeScript.Compiler.Ast;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private async ValueTask<bool> PeekCore(Func<ValueTask<bool>> action)
    {
        await ParseStack;
        var state = scanner.Mark();
        int count = diagnostics.Count, scanned = scannedDiagnostics;
        bool error = hasError;
        NodeFlags flags = context, fileFlags = sourceFlags;
        bool possibleAwait = possibleTopLevelAwait;
        speculationDepth++;
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            scanner.Rewind(state);
            diagnostics.RemoveRange(count, diagnostics.Count - count);
            scannedDiagnostics = scanned;
            hasError = error;
            context = flags;
            sourceFlags = fileFlags;
            possibleTopLevelAwait = possibleAwait;
            speculationDepth--;
        }
    }

    private async ValueTask<SyntaxNode?> ReturnAnnotationCore()
    {
        await ParseStack;
        if (!Take(K.ColonToken))
            return null;
        NodeFlags saved = context;
        context &= ~NodeFlags.DisallowConditionalTypesContext;
        try
        {
            return await TypeOrPredicateCore().ConfigureAwait(false);
        }
        finally
        {
            context = saved;
        }
    }
}
