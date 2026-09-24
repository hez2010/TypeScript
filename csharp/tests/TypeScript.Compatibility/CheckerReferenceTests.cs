using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerReferenceTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Reference assertion {checks + 1}");
            checks++;
        }
        const string source = "function f() { let x: number; x = 1; x++; x; missing; } __use(later); let later = 1; function p(a = a, b = c, c = 1) {}";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }),
            "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new CheckerEnvironment(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var nodes = program.SourceFiles[0].Syntax.DescendantsAndSelf().ToArray();
        var uses = nodes.OfType<IdentifierNode>().Where(n => n.Text == "x" && n.Parent is not VariableDeclarationNode).ToArray();
        var x = scope.ReferenceSymbols.Resolve(uses[0]);
        Check(symbols.ReferenceKinds(x) == 0 && ReferenceSyntax.AccessKind(uses[0]) == 1);
        Check(scope.ReferenceSymbols.Resolve(uses[1]) == x && symbols.ReferenceKinds(x) == (SymbolFlags.Value | SymbolFlags.ExportValue));
        Check(scope.ReferenceSymbols.Resolve(uses[2]) == x);
        int before = scope.Diagnostics.Count;
        var missing = nodes.OfType<IdentifierNode>().Single(n => n.Text == "missing");
        Check(scope.ReferenceSymbols.Resolve(missing) == symbols.UnknownSymbol && scope.Diagnostics.Count == before + 1);
        Check(scope.ReferenceSymbols.Resolve(missing) == symbols.UnknownSymbol && scope.Diagnostics.Count == before + 1);
        var use = nodes.OfType<IdentifierNode>().First(n => n.Text == "later");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            scope.ReferenceSymbols.Resolve(use, cancellation.Token);
            throw new InvalidOperationException("Resolution cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(links.SymbolNodes.Get(use).ResolvedSymbol is null);
        Check(scope.ReferenceSymbols.Resolve(use) == symbols.Globals["later"] && scope.Diagnostics.Contains(2448));
        try
        {
            scope.ReferenceSymbols.Resolve(use, cancellation.Token);
            throw new InvalidOperationException("Cached resolution cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        var declaration = symbols.Globals["later"].ValueDeclaration!;
        Check(!await scope.DeclarationOrder.BeforeUseAsync(declaration, use));
        try
        {
            await scope.DeclarationOrder.BeforeUseAsync(declaration, use, cancellation.Token);
            throw new InvalidOperationException("Declaration ordering cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        var otherLinks = new CheckerLinks();
        var otherScope = new CheckerEnvironment(new(true, true), otherLinks);
        var otherSymbols = await CheckerSymbols.CreateAsync(program, otherLinks, otherScope);
        Check(otherLinks.SymbolNodes.Get(use).ResolvedSymbol is null);
        Check(otherScope.ReferenceSymbols.Resolve(use) == otherSymbols.Globals["later"]);
        Check(otherLinks.SymbolNodes.Get(use) != links.SymbolNodes.Get(use));

        var identifier = new IdentifierNode { Text = "deep" };
        SyntaxNode nested = identifier;
        for (int i = 0; i < 20_000; i++)
            nested = new ParenthesizedExpressionNode { Expression = nested };
        var assignment = new BinaryExpressionNode
        {
            Left = nested,
            OperatorToken = new TokenNode(SyntaxKind.EqualsToken),
            Right = new NumericLiteralNode { Text = "1" }
        };
        assignment.SetParents();
        Check(ReferenceSyntax.AssignmentTarget(identifier) == assignment && ReferenceSyntax.AssignmentKind(identifier) == 1);
        Check(ReferenceSyntax.AccessKind(identifier) == 1 && ReferenceSyntax.IsExpression(identifier));
        Check(DeclarationOrder.SameScopeDescendant(identifier, assignment, null));
        Check(!ReferenceSyntax.ValidTypeOnlyUse(identifier));
        var compound = new BinaryExpressionNode
        {
            Left = identifier,
            OperatorToken = new TokenNode(SyntaxKind.BarBarEqualsToken),
            Right = new NumericLiteralNode { Text = "1" }
        };
        compound.SetParents();
        Check(ReferenceSyntax.AccessKind(identifier) == 2 && ReferenceSyntax.AssignmentKind(identifier) == 1);
        compound.OperatorToken = new TokenNode(SyntaxKind.PlusEqualsToken);
        Check(ReferenceSyntax.AccessKind(identifier) == 2 && ReferenceSyntax.AssignmentKind(identifier) == 2);
        var nonNull = new NonNullExpressionNode { Expression = identifier };
        assignment.Left = nonNull;
        assignment.SetParents();
        Check(ReferenceSyntax.AccessKind(identifier) == 0 && ReferenceSyntax.AssignmentTarget(identifier) == assignment);
        var typeQuery = new TypeQueryNode { ExprName = identifier };
        typeQuery.SetParents();
        Check(DeclarationOrder.InTypeQuery(identifier) && ReferenceSyntax.ValidTypeOnlyUse(identifier));
        Check(await scope.DeclarationOrder.BeforeUseAsync(declaration, identifier));
        Console.WriteLine($"{checks} reference safety assertions; 20,000-level syntax traversal.");
    }
}
