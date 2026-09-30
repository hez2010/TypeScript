using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerQueryTests
{
    internal static async Task<int> ScopeSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Checker scope query assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        Utf8String source = Utf8String.Concat(Utf8String.Concat("import {value as alias} from './dep'; export {alias as forwarded}; export * from './dep'; "u8, "const shadow=1; class C<T> { constructor(public value:T){} "u8, "method<U>(this:C<T>,parameter:U){const shadow='inner';return {shadow,parameter};} "u8), "static staticMethod<V>(parameter:V){return parameter;} }"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = source.Span.ToArray(),
            ["/project/dep.ts"u8] = Wtf8.Encode("export const value=1;")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts"u8)!.Syntax;
        var nodes = file.DescendantsAndSelf().ToArray();
        var method = nodes.OfType<MethodDeclarationNode>().Single(n => (n.Name is IdentifierNode { Text: { Span: var matchedText } } && matchedText.SequenceEqual("method"u8)));
        var location = method.Body!;
        var scope = await checker.GetSymbolsInScopeAsync(location, TypeScript.Compiler.Binding.SymbolFlags.All);
        Check(scope.Any(s => s.Name == "T"u8) && scope.Any(s => s.Name == "U"u8) && scope.Any(s => s.Name == "arguments"u8));
        Check(scope.All(s => s.Name != "this"u8));
        var shadow = scope.Single(s => s.Name == "shadow"u8);
        Check(shadow.ValueDeclaration?.Parent?.Parent?.Parent == method.Body);
        var staticMethod = nodes.OfType<MethodDeclarationNode>().Single(n => (n.Name is IdentifierNode { Text: { Span: var matchedText2 } } && matchedText2.SequenceEqual("staticMethod"u8)));
        var staticScope = await checker.GetSymbolsInScopeAsync(staticMethod.Body!, TypeScript.Compiler.Binding.SymbolFlags.Type);
        Check(staticScope.Any(s => s.Name == "V"u8) && staticScope.All(s => (!s.Name.Span.SequenceEqual("T"u8) && !s.Name.Span.SequenceEqual("U"u8))));
        var alias = scope.Single(s => s.Name == "alias"u8);
        var target = await checker.GetAliasedSymbolAsync(alias);
        Check(target.Name == "value"u8 && target != alias);
        var export = nodes.OfType<ExportSpecifierNode>().Single();
        Check(await checker.GetExportSpecifierLocalTargetSymbolAsync(export) == alias);
        var shorthand = nodes.OfType<ShorthandPropertyAssignmentNode>().Single(n => (n.Name is IdentifierNode { Text: { Span: var matchedText3 } } && matchedText3.SequenceEqual("shadow"u8)));
        Check(await checker.GetShorthandAssignmentValueSymbolAsync(shorthand) == shadow);
        var shadowType = await checker.GetTypeOfSymbolAtLocationAsync(shadow, shorthand.Name);
        Check(shadowType is LiteralType { Value: Utf8String { Span: var matchedText4 } } && matchedText4.SequenceEqual("inner"u8));
        var exports = await checker.GetExportsOfModuleAsync(checker.Symbols.Declaration(file)!);
        Check(exports.Any(s => s.Name == "forwarded"u8) && exports.Any(s => s.Name == "value"u8));
        Check(exports.All(s => s.Name != "alias"u8));
        var parameter = nodes.OfType<ConstructorDeclarationNode>().Single().Parameters!.OfType<ParameterDeclarationNode>().Single();
        var pair = await checker.GetSymbolsOfParameterPropertyDeclarationAsync(parameter, "value"u8);
        Check(pair.Parameter != pair.Property && pair.Parameter.Name == "value"u8 && pair.Property.Name == "value"u8);
        Check(pair.Parameter.Declarations.Contains(parameter) && pair.Property.Declarations.Contains(parameter));
        Check(
            await checker.GetTypeOfSymbolAtLocationAsync(
                pair.Parameter,
                null) == await checker.GetTypeOfSymbolAtLocationAsync(pair.Property, null));
        Check((await checker.GetSymbolsInScopeAsync(location, 0)).Count == 0);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await checker.GetSymbolsInScopeAsync(location, TypeScript.Compiler.Binding.SymbolFlags.All, cancelled.Token);
            throw new InvalidOperationException("Cancelled scope query completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        try
        {
            await checker.GetSymbolsInScopeAsync(new IdentifierNode { Text = "outside"u8 }, TypeScript.Compiler.Binding.SymbolFlags.All);
            throw new InvalidOperationException("Scope query accepted foreign syntax");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        checker.BeforeMemberTable = _ => throw new InvalidOperationException("query callback");
        try
        {
            await checker.GetSymbolsInScopeAsync(location, TypeScript.Compiler.Binding.SymbolFlags.All);
            throw new InvalidOperationException("Query callback not reached");
        }
        catch (InvalidOperationException error) when (error.Message == "query callback")
        {
            checks++;
        }
        finally
        {
            checker.BeforeMemberTable = null;
        }
        Check((await checker.GetSymbolsInScopeAsync(location, TypeScript.Compiler.Binding.SymbolFlags.All)).Count == scope.Count);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        checker.BeforeMemberTable = _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Scope query was not released");
        };
        var active = Task.Run(async () => await checker.GetSymbolsInScopeAsync(location, TypeScript.Compiler.Binding.SymbolFlags.All));
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(30)));
            using var stop = new CancellationTokenSource();
            var queued = checker.GetExportsOfModuleAsync(checker.Symbols.Declaration(file)!, stop.Token);
            Check(!queued.IsCompleted);
            stop.Cancel();
            try
            {
                await queued;
                throw new InvalidOperationException("Queued module query ignored cancellation");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        finally
        {
            release.Set();
        }
        Check((await active).Count == scope.Count);
        checker.BeforeMemberTable = null;
        Check((await checker.GetExportsOfModuleAsync(checker.Symbols.Declaration(file)!)).Count == exports.Count);
        return checks;
    }

    internal static async Task<int> SymbolSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Checker symbol query assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        Utf8String source = Utf8String.Concat(Utf8String.Concat("import {value as alias} from './dep'; import './missing'; "u8, "interface Foo { bar:string; } declare const foo:Foo; foo.bar; foo['bar']; alias; "u8, "declare const dict:{[key:string]:number}; dict.key; dict.key; "u8), "let a:Missing.Member; let b:Missing.Member; unknownValue;"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = source.Span.ToArray(),
            ["/project/dep.ts"u8] = Wtf8.Encode("export const value=1;")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts"u8)!.Syntax;
        var nodes = file.DescendantsAndSelf().ToArray();
        var import = nodes.OfType<ImportSpecifierNode>().Single();
        var alias = await checker.GetSymbolAtLocationAsync(import.Name!);
        var target = await checker.GetSymbolAtLocationAsync(import.PropertyName!);
        Check(alias?.Name == "alias"u8 && target?.Name == "value"u8 && alias != target);
        Check(await checker.GetSymbolAtLocationAsync(nodes.OfType<IdentifierNode>().Last(n => n.Text == "alias"u8)) == alias);
        var property = nodes.OfType<PropertyAccessExpressionNode>().First();
        var symbol = await checker.GetSymbolAtLocationAsync(property);
        Check(symbol?.Name == "bar"u8 && symbol.ValueDeclaration is PropertySignatureDeclarationNode);
        Check(await checker.GetSymbolAtLocationAsync(property.Name!) == symbol);
        Check(await checker.GetSymbolAtLocationAsync(nodes.OfType<ElementAccessExpressionNode>().Single().ArgumentExpression!) == symbol);
        var keys = nodes.OfType<PropertyAccessExpressionNode>().Where(n => (n.Name is IdentifierNode { Text: { Span: var matchedText5 } } && matchedText5.SequenceEqual("key"u8))).ToArray();
        var index = await checker.GetSymbolAtLocationAsync(keys[0]);
        Check(index is not null && (index.CheckFlags & TypeScript.Compiler.Binding.CheckFlags.IndexSymbol) != 0);
        Check(index!.Declarations is [IndexSignatureDeclarationNode]);
        Check(await checker.GetSymbolAtLocationAsync(keys[1]) == index);
        var names = nodes.OfType<QualifiedNameNode>().ToArray();
        var unresolved = await checker.GetSymbolAtLocationAsync(names[0]);
        Check(unresolved?.Name == "Member"u8 && unresolved.Parent?.Name == "Missing"u8
            && (unresolved.CheckFlags & TypeScript.Compiler.Binding.CheckFlags.Unresolved) != 0);
        Check(await checker.GetSymbolAtLocationAsync(names[1]) == unresolved);
        int diagnostics = checker.Diagnostics.Count + checker.Environment.Diagnostics.Count;
        Check(await checker.GetSymbolAtLocationAsync(nodes.OfType<IdentifierNode>().Single(n => n.Text == "unknownValue"u8)) is null);
        Check(await checker.GetSymbolAtLocationAsync(nodes.OfType<StringLiteralNode>().Single(n => n.Text == "./missing"u8)) is null);
        Check(checker.Diagnostics.Count + checker.Environment.Diagnostics.Count == diagnostics);
        Check(await checker.GetSymbolAtLocationAsync(file) == checker.Symbols.Declaration(file));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await checker.GetSymbolAtLocationAsync(property, cancelled.Token);
            throw new InvalidOperationException("Cancelled symbol query completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(await checker.GetSymbolAtLocationAsync(property) == symbol);
        try
        {
            await checker.GetSymbolAtLocationAsync(new IdentifierNode { Text = "foo"u8 });
            throw new InvalidOperationException("Symbol query accepted foreign syntax");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        var other = await program.CreateCheckerAsync();
        Check(await other.GetSymbolAtLocationAsync(keys[0]) != index);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        other.BeforeExpressionFinish = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Symbol query was not released");
        };
        var active = Task.Run(async () => await other.GetSymbolAtLocationAsync(property));
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(30)));
            using var stop = new CancellationTokenSource();
            var queued = other.GetSymbolAtLocationAsync(import.Name!, stop.Token);
            Check(!queued.IsCompleted);
            stop.Cancel();
            try
            {
                await queued;
                throw new InvalidOperationException("Queued symbol query ignored cancellation");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        finally
        {
            release.Set();
        }
        Check((await active)?.Name == "bar"u8);
        other.BeforeExpressionFinish = null;
        Check(await other.GetSymbolAtLocationAsync(import.Name!) == alias);
        await checker.CheckProgramAsync();
        Check(checker.Environment.Diagnostics.Contains(DiagnosticCode.CannotFindName0));
        Check(checker.Environment.Diagnostics.Contains(DiagnosticCode.CannotFindModuleOrTypeDeclarationsForSideEffectImportOf0));
        return checks;
    }

    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Checker location query assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        Utf8String source = Utf8String.Concat("const value=1; class C { field=1; } interface I { field:number; } "u8, "let input: string|number; if(typeof input === 'string'){ input; }"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        { ["/project/main.ts"u8] = source.Span.ToArray() }), "/project"u8,
            new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts"u8)!.Syntax;
        var literal = file.DescendantsAndSelf().OfType<NumericLiteralNode>().First();
        var type = await checker.GetTypeAtLocationAsync(literal);
        Check(type is LiteralType { Value: 1d } && type == checker.Context.GetNumberLiteralType(1));
        var declared = await checker.GetTypeAtLocationAsync(literal.Parent!);
        Check(declared is LiteralType { Value: 1d } declarationLiteral && declarationLiteral.RegularType == type);
        Check(await checker.GetTypeAtLocationAsync(((VariableDeclarationNode)literal.Parent!).Name!) == declared);
        Check(await checker.GetTypeAtLocationAsync(file) == checker.Context.ErrorType);
        var declaration = file.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single();
        var classType = await checker.GetTypeAtLocationAsync(declaration);
        Check(classType is InterfaceType && await checker.GetTypeAtLocationAsync(declaration.Name!) == classType);
        var narrowed = file.DescendantsAndSelf().OfType<IdentifierNode>().Last(n => n.Text == "input"u8);
        Check(await checker.GetTypeAtLocationAsync(narrowed) == checker.Context.StringType);
        var other = await program.CreateCheckerAsync();
        Check(await other.GetTypeAtLocationAsync(literal) != type);
        var repeated = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ => await checker.GetTypeAtLocationAsync(literal)));
        Check(repeated.All(t => t == type));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await checker.GetTypeAtLocationAsync(literal, cancelled.Token);
            throw new InvalidOperationException("Cancelled location query completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(await checker.GetTypeAtLocationAsync(literal) == type);
        try
        {
            await checker.GetTypeAtLocationAsync(new NumericLiteralNode { Text = "1"u8 });
            throw new InvalidOperationException("Location query accepted foreign syntax");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        checker.BeforeExpressionFinish = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Location query was not released");
        };
        var active = Task.Run(async () => await checker.GetTypeAtLocationAsync(literal));
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(30)));
            using var stop = new CancellationTokenSource();
            var queued = checker.GetTypeAtLocationAsync(literal, stop.Token);
            Check(!queued.IsCompleted);
            stop.Cancel();
            try
            {
                await queued;
                throw new InvalidOperationException("Queued location query ignored cancellation");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        finally
        {
            release.Set();
        }
        Check(await active == type);
        checker.BeforeExpressionFinish = null;
        Check(await checker.GetTypeAtLocationAsync(literal) == type);
        var leaf = new IdentifierNode { Text = "T"u8 };
        SyntaxNode nested = leaf;
        for (int i = 0; i < 20_000; i++)
        {
            var parent = new QualifiedNameNode { Left = new IdentifierNode { Text = "N"u8 }, Right = nested };
            nested.Parent = parent;
            nested = parent;
        }
        Check(!QuerySyntax.Expression(leaf.Parent!));
        var jsOptions = new CompilerOptions();
        jsOptions.SetRaw("noLib"u8, "true"u8);
        jsOptions.SetRaw("allowJs"u8, "true"u8);
        var jsProgram = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        { ["/project/main.js"u8] = Wtf8.Encode("/** @type {number} */ let value=1;") }), "/project"u8,
            new("/project/tsconfig.json"u8, jsOptions, ["/project/main.js"u8], [], [], []));
        var jsFile = jsProgram.GetFile("/project/main.js"u8)!.Syntax;
        var owner = jsFile.DescendantsAndSelf().OfType<VariableStatementNode>().Single();
        var documentation = await jsFile.GetDocumentationAsync(owner);
        var original = documentation.SelectMany(d => d.DescendantsAndSelf()).Single(n => n.Kind == SyntaxKind.NumberKeyword);
        var reparsed = QuerySyntax.Reparsed(original);
        Check(reparsed != original && (reparsed.Flags & NodeFlags.Reparsed) != 0);
        var jsChecker = await jsProgram.CreateCheckerAsync();
        Check(await jsChecker.GetTypeAtLocationAsync(original) == jsChecker.Context.NumberType);
        Check(await jsChecker.GetTypeAtLocationAsync(reparsed) == jsChecker.Context.NumberType);
        return checks;
    }
}
