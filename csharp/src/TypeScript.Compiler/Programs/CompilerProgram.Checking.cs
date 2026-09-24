using TypeScript.Compiler.Checking;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    // Checker state is exclusive. Independent callers receive independent caches
    // while sharing this program's immutable syntax and binding.
    internal ValueTask<Checker> CreateCheckerAsync(CancellationToken cancellation = default) => Checker.CreateAsync(this, cancellation);
}
