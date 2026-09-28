using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private readonly Stack<List<SyntaxNode>> nodeLists = [];

    private List<SyntaxNode> RentNodeList() => nodeLists.TryPop(out var list) ? list : [];

    private void ReturnNodeList(List<SyntaxNode> list)
    {
        list.Clear();
        nodeLists.Push(list);
    }

    // BCL continuation scheduling provides a fresh stack only when the runtime's
    // stack-space check requires one. The common path completes synchronously.
    private ConfiguredTaskAwaitable ParseStack => Task.CompletedTask.ConfigureAwait(
        RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);

    // Every suspended production uses ConfigureAwait(false), so this facade also
    // works when its caller owns a single-threaded SynchronizationContext.
    internal static T RunParse<T>(ValueTask<T> operation) => operation.IsCompletedSuccessfully
        ? operation.Result : operation.AsTask().GetAwaiter().GetResult();

    private async ValueTask<NodeList> DelimitedCore(
        K end,
        Func<ValueTask<SyntaxNode>> element,
        bool semicolons = false,
        Func<bool>? stop = null,
        Func<bool>? startsElement = null,
        DiagnosticMessage? elementExpected = null,
        Action? reportInvalidElement = null,
        Func<bool>? recoveryBoundary = null)
    {
        await ParseStack;
        int start = Pos;
        var nodes = RentNodeList();
        try
        {
            // Speculative productions must keep their delimiters so a failed arrow
            // or type-argument parse can return to the surrounding expression.
            bool recoveringReparse = reparsingTopLevelAwait && speculationDepth == 0;
            while (Token != end && Token != K.EndOfFile && stop?.Invoke() != true)
            {
                if (startsElement?.Invoke() == false)
                {
                    if (reportInvalidElement is not null)
                        reportInvalidElement();
                    else
                        Error(elementExpected!);
                    if ((recoveryBoundary?.Invoke() ?? (!recoveringReparse && Token != K.SemicolonToken && StartsStatement()))
                        || variableDeclarationDepth != 0 && Token == K.EqualsGreaterThanToken
                        || !recoveringReparse && Token is K.CloseBraceToken or K.CloseParenToken or K.CloseBracketToken)
                        break;
                    Next();
                    continue;
                }
                int before = Pos;
                nodes.Add(await element().ConfigureAwait(false));
                if (Token == end || stop?.Invoke() == true)
                    break;
                if (!Take(K.CommaToken) && !(semicolons && (Take(K.SemicolonToken) || LineBreak)))
                {
                    Error(Messages.X_0_expected, semicolons ? ";" : ",");
                    if (!recoveringReparse && Token is K.CloseBraceToken or K.CloseParenToken or K.CloseBracketToken)
                        break;
                }
                if (Pos == before)
                    Next();
            }
            return new(nodes.ToArray(), start, Pos);
        }
        finally
        {
            ReturnNodeList(nodes);
        }
    }

    private async ValueTask<NodeList> ListCore(
        K end,
        Func<ValueTask<SyntaxNode>> element,
        bool statementList = false,
        Func<bool>? stop = null,
        bool typeMembers = false)
    {
        await ParseStack;
        int start = Pos;
        var nodes = RentNodeList();
        var outerReparses = reparsedStatements;
        reparsedStatements = RentNodeList();
        try
        {
            while (Token != end && Token != K.EndOfFile && stop?.Invoke() != true)
            {
                if (typeMembers && !Peek(static parser => parser.ScanTypeMemberStart()))
                {
                    Error(Messages.Property_or_signature_expected);
                    if (Token is K.CloseParenToken or K.CloseBracketToken || Token != K.SemicolonToken && StartsStatement())
                        break;
                    Next();
                    continue;
                }
                if (statementList && !StartsStatement())
                {
                    Error(Messages.Declaration_or_statement_expected);
                    if (classMemberBodyDepth != 0 && Peek(static parser => parser.StartsClassMember()))
                        break;
                    Next();
                    continue;
                }
                int before = Pos;
                SyntaxNode node = await element().ConfigureAwait(false);
                foreach (SyntaxNode reparse in reparsedStatements)
                    if (!statementList && reparse.Kind is K.JSTypeAliasDeclaration or K.JSImportDeclaration)
                        outerReparses.Add(reparse);
                    else
                        nodes.Add(reparse);
                reparsedStatements.Clear();
                nodes.Add(node);
                if (Pos == before)
                {
                    Error(Messages.Declaration_or_statement_expected);
                    Next();
                }
            }
            return new(nodes.ToArray(), start, Pos);
        }
        finally
        {
            ReturnNodeList(nodes);
            ReturnNodeList(reparsedStatements);
            reparsedStatements = outerReparses;
        }
    }

}
