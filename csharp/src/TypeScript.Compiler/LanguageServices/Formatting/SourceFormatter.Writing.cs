using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public static partial class SourceFormatter
{
    internal static async ValueTask<FormatCodeSettings> ForWritingAsync(FormatCodeSettings settings, SourceFileNode file, CancellationToken cancellation)
        => settings.Semicolons == SemicolonPreference.Ignore && !await ProbablyUsesSemicolonsAsync(file, cancellation)
            ? settings with { Semicolons = SemicolonPreference.Remove } : settings;

    internal static async ValueTask<bool> ProbablyUsesSemicolonsAsync(SourceFileNode file, CancellationToken cancellation)
    {
        int with = 0, without = 0;
        var scanner = new Scanner(file.Source);
        Stack<SyntaxNode> pending = [];
        for (int i = file.ChildCount - 1; i >= 0; i--) pending.Push(file.GetChild(i));
        while (pending.TryPop(out var node))
        {
            cancellation.ThrowIfCancellationRequested();
            if ((node.Flags & NodeFlags.Reparsed) != 0) continue;
            bool statement = node.Kind is K.VariableStatement or K.ExpressionStatement or K.DoStatement or K.ContinueStatement or K.BreakStatement
                or K.ReturnStatement or K.ThrowStatement or K.DebuggerStatement or K.PropertyDeclaration or K.TypeAliasDeclaration
                or K.ImportDeclaration or K.ImportEqualsDeclaration or K.ExportDeclaration or K.NamespaceExportDeclaration or K.ExportAssignment;
            bool signature = node.Kind is K.CallSignature or K.ConstructSignature or K.IndexSignature or K.PropertySignature or K.MethodSignature;
            if (statement || signature)
            {
                var last = await SyntaxNavigation.FindPrecedingTokenAsync(file, node.End, node, cancellation: cancellation);
                if (last?.Kind == K.SemicolonToken) with++;
                else if (statement || last is not null && last.Kind != K.CommaToken
                    && file.Source.GetLineAndCharacter(await SyntaxNavigation.GetStartAsync(last, file, cancellation: cancellation)).Line
                        != file.Source.GetLineAndCharacter(scanner.SkipTriviaAt(last.End)).Line) without++;
            }
            if (with + without >= 5) break;
            for (int i = node.ChildCount - 1; i >= 0; i--) pending.Push(node.GetChild(i));
        }
        return with == 0 && without <= 1 || without == 0 || with * 5 > without;
    }
}
