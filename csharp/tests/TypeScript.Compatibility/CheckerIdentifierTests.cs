using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerIdentifierTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Identifier assertion {checks + 1}");
            checks++;
        }
        static async ValueTask<CompilerProgram> Build(Utf8String source)
        {
            var options = new CompilerOptions();
            options.SetRaw("noLib"u8, "true"u8);
            options.SetRaw("strict"u8, "true"u8);
            return await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
            { ["/project/main.ts"u8] = source.Span.ToArray() }),
                "/project"u8,
                new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        }
        Utf8String source = "declare function __expr(value: unknown): void; function defaults(x:string|undefined='a'){__expr(x);} function unassigned(){let x:number;__expr(x);} function captured(x:string|number){x=1;const f=()=>{__expr(x);};} const fixed=1;__expr(fixed=2);"u8;
        var program = await Build(source);
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new CheckerEnvironment(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new Checker(context, links, scope);
        var nodes = program.SourceFiles[0].Syntax.DescendantsAndSelf().ToArray();
        var queries = nodes.OfType<CallExpressionNode>().Where(
            n => (n.Expression is IdentifierNode { Text: { Span: var matchedText } } && matchedText.SequenceEqual("__expr"u8))).Select(n => n.Arguments![0]).ToArray();
        var parameter = nodes.OfType<ParameterDeclarationNode>().Single(n => n.Initializer is not null);
        var type = await host.Values.GetAsync(symbols.Declaration(parameter)!);
        using (var cancellation = new CancellationTokenSource())
        {
            host.BeforeInitializer = _ => cancellation.Cancel();
            try
            {
                await host.Identifiers.RemoveOptionalityAsync(type, parameter, cancellation.Token);
                throw new InvalidOperationException("Initializer cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        Check(host.Instantiation.Resolutions.Count == 0);
        Check((links.Nodes.Get(parameter).Flags & NodeCheckFlags.InitializerIsUndefinedComputed) == 0);
        host.BeforeInitializer = null;
        Check(await host.Identifiers.RemoveOptionalityAsync(type, parameter) == context.StringType);
        Check((links.Nodes.Get(parameter).Flags & NodeCheckFlags.InitializerIsUndefinedComputed) != 0);
        Check((links.Nodes.Get(parameter).Flags & NodeCheckFlags.InitializerIsUndefined) == 0);
        Check(await host.Expressions.CheckAsync(queries[0]) == context.StringType);
        Check(
            await host.Expressions.CheckAsync(queries[1]) == context.NumberType
                && host.Diagnostics.Contains(DiagnosticCode.Variable0IsUsedBeforeBeingAssigned));
        Check(await host.Expressions.CheckAsync(queries[2]) == context.NumberType);
        Check(
            await host.Expressions.CheckAsync(queries[3]) is LiteralType { Value: 2d }
                && host.Diagnostics.Contains(DiagnosticCode.CannotAssignTo0BecauseItIsAConstant));
        Check(host.FlowTypes.ActiveLoopCount == 0 && host.FlowTypes.SharedCount == 0 && host.Instantiation.Resolutions.Count == 0);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await host.Identifiers.RemoveOptionalityAsync(type, parameter, cancelled.Token);
            throw new InvalidOperationException("Cached initializer cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        var generic = context.NewTypeParameter();
        generic.Constraint = type;
        var noInfer = await host.Instantiation.Engine.NoInferAsync(generic);
        Check(await host.ReferenceNarrowing.GetAsync(noInfer, queries[0], CheckMode.Inferential) == generic);
        Check(await host.ReferenceNarrowing.GetAsync(generic, queries[0], 0) == type);
        try
        {
            await host.ReferenceNarrowing.GetAsync(new TypeContext(true, true).StringType, queries[0], 0);
            throw new InvalidOperationException("Foreign type accepted");
        }
        catch (ArgumentException error) when (error.ParamName == "type")
        {
            checks++;
        }
        try
        {
            await host.ReferenceNarrowing.GetAsync(context.StringType, queries[0], 0, cancelled.Token);
            throw new InvalidOperationException("Reference cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        var reference = new IdentifierNode { Text = "value"u8 };
        SyntaxNode nested = reference;
        for (int i = 0; i < 20_000; i++)
            nested = new ParenthesizedExpressionNode { Expression = nested };
        var property = new PropertyDeclarationNode { Name = new IdentifierNode { Text = "property"u8 }, Initializer = nested };
        property.SetParents();
        Check(IdentifierTypes.PropertyInitializerOrStaticBlock(reference, true));
        Check(MissingNamePrefixes.ThisContainer(reference, false, false) == property);
        Check(
            host.AssignmentChecks.Reference(
                nested,
                DiagnosticCode.TheLeftHandSideOfAnAssignmentExpressionMustBeAVariableOrAPropertyAccess,
                DiagnosticCode.TheLeftHandSideOfAnAssignmentExpressionMayNotBeAnOptionalPropertyAccess));
        Check(
            !host.AssignmentChecks.Reference(
                new NumericLiteralNode { Text = "1"u8 },
                DiagnosticCode.TheTargetOfAnObjectRestAssignmentMustBeAVariableOrAPropertyAccess,
                DiagnosticCode.TheTargetOfAnObjectRestAssignmentMayNotBeAnOptionalPropertyAccess)
                && host.Diagnostics.Contains(DiagnosticCode.TheTargetOfAnObjectRestAssignmentMustBeAVariableOrAPropertyAccess));
        var optional = new PropertyAccessExpressionNode
        {
            Expression = new IdentifierNode { Text = "x"u8 },
            Name = new IdentifierNode { Text = "p"u8 },
            Flags = NodeFlags.OptionalChain
        };
        Check(
            !host.AssignmentChecks.Reference(
                optional,
                DiagnosticCode.TheOperandOfAnIncrementOrDecrementOperatorMustBeAVariableOrAPropertyAccess,
                DiagnosticCode.TheOperandOfAnIncrementOrDecrementOperatorMayNotBeAnOptionalPropertyAccess)
                && host.Diagnostics.Contains(DiagnosticCode.TheOperandOfAnIncrementOrDecrementOperatorMayNotBeAnOptionalPropertyAccess));

        var aliasProgram = await Build("namespace N { export class C {} } import A = N; import B = A.C; B;"u8);
        var aliasContext = new TypeContext(true, true);
        var aliasLinks = new CheckerLinks();
        var aliasScope = new CheckerEnvironment(aliasContext, aliasLinks);
        var aliasSymbols = await CheckerSymbols.CreateAsync(aliasProgram, aliasLinks, aliasScope);
        var references = new AliasReferences(aliasSymbols, aliasLinks, aliasScope.ReferenceSymbols, aliasScope.Aliases);
        var aliases = aliasProgram.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<ImportEqualsDeclarationNode>().ToArray();
        var a = aliasSymbols.Declaration(aliases[0])!;
        var b = aliasSymbols.Declaration(aliases[1])!;
        var use = aliasProgram.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<IdentifierNode>().Last(n => n.Text == "B"u8);
        using (var cancellation = new CancellationTokenSource())
        {
            aliasScope.BeforeValueResolution = () =>
            {
                if (aliasLinks.Aliases.Get(b).Referenced)
                    cancellation.Cancel();
            };
            try
            {
                await references.IdentifierAsync(use, cancellation.Token);
                throw new InvalidOperationException("Alias marking cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        Check(!aliasLinks.Aliases.Get(a).Referenced && !aliasLinks.Aliases.Get(b).Referenced);
        aliasScope.BeforeValueResolution = null;
        await references.IdentifierAsync(use);
        Check(aliasLinks.Aliases.Get(a).Referenced && aliasLinks.Aliases.Get(b).Referenced);
        await references.IdentifierAsync(use);
        Check(aliasScope.AliasResolutions.Count == 0);

        var deprecatedProgram = await Build("/** @deprecated */ const old = 1; const current = 2; old; current;"u8);
        var deprecatedLinks = new CheckerLinks();
        var deprecatedScope = new CheckerEnvironment(new(true, true), deprecatedLinks);
        var deprecatedSymbols = await CheckerSymbols.CreateAsync(deprecatedProgram, deprecatedLinks, deprecatedScope);
        Check(deprecatedScope.Deprecations.Symbol(deprecatedSymbols.Globals["old"u8]));
        Check(!deprecatedScope.Deprecations.Symbol(deprecatedSymbols.Globals["current"u8]));

        var bindingProgram = await Build(
            "declare const source:{readonly value:number; text?:string}; const {value,text='fallback',...rest}=source; __expr(text);"u8);
        var bindingContext = new TypeContext(true, true);
        var bindingLinks = new CheckerLinks();
        var bindingScope = new CheckerEnvironment(bindingContext, bindingLinks);
        var bindingSymbols = await CheckerSymbols.CreateAsync(bindingProgram, bindingLinks, bindingScope);
        var bindingHost = new Checker(bindingContext, bindingLinks, bindingScope);
        var elements = bindingProgram.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<BindingElementNode>().ToArray();
        var textSymbol = bindingSymbols.Declaration(elements[1])!;
        using (var cancellation = new CancellationTokenSource())
        {
            bindingHost.BeforeInitializer = _ => cancellation.Cancel();
            try
            {
                await bindingHost.Values.GetAsync(textSymbol, cancellation.Token);
                throw new InvalidOperationException("Binding default cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        Check(bindingHost.Instantiation.Resolutions.Count == 0 && bindingLinks.Values.Get(textSymbol).ResolvedType is null);
        Check(bindingHost.FlowTypes.SharedCount == 0 && bindingHost.FlowTypes.ActiveLoopCount == 0);
        bindingHost.BeforeInitializer = null;
        Check(await bindingHost.Values.GetAsync(textSymbol) == bindingContext.StringType);
        var parent = (await bindingHost.Bindings.ParentAsync(elements[0].Parent!.Parent!))!;
        var spread = await bindingHost.Bindings.RestAsync(parent, [], null);
        var original = (await bindingHost.Properties.PropertyAsync(parent, "value"u8))!;
        var copied = (await bindingHost.Properties.PropertyAsync(spread, "value"u8))!;
        Check(original != copied && bindingHost.IsReadonly(original) && !bindingHost.IsReadonly(copied));
        Check(bindingLinks.MappedSymbols.Get(copied).SyntheticOrigin == original);
        Check(await bindingHost.Values.GetAsync(copied) == bindingContext.NumberType);
        try
        {
            await bindingHost.Bindings.ParentAsync(elements[0].Parent!.Parent!, cancellation: cancelled.Token);
            throw new InvalidOperationException("Cached binding parent cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        try
        {
            await bindingHost.Bindings.FromParentAsync(elements[0], context.AnyType);
            throw new InvalidOperationException("Foreign binding parent accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Check(await bindingHost.Bindings.FromParentAsync(elements[0], bindingContext.AnyType) == bindingContext.AnyType);
        checks += await MissingNameSafety();
        Console.WriteLine(
            $"{checks} identifier/binding/default/alias/cancellation/ownership assertions; 20,000-level reference traversal.");
    }

    private static async Task<int> MissingNameSafety()
    {
        Utf8String source = """
            interface Shape { size: number; }
            const value = 1;
            type Value = value;
            type Size = Shape.size;
            type Wrong = Shape.missing;
            Shape;
            namespace OnlyTypes { export interface Item {} }
            OnlyTypes;
            let bad: OnlyTypes;
            const counter = 1;
            countr;
            type Text = strng;
            type Misspelled = OnlyTypez.Item;
            export { number };
            class Numeric extends number {}
            """u8;
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = source.Span.ToArray(),
            ["/project/globals.d.ts"u8] = Wtf8.Encode("interface String {}")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8, "/project/globals.d.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts"u8)!.Syntax;
        await checker.CheckSourceFileAsync(file);
        var codes = checker.DiagnosticCodesForFile(file);
        if (!codes.SequenceEqual(
            [
                    DiagnosticCode.CannotFindName0DidYouMean1,
                    DiagnosticCode.CannotFindName0DidYouMean1,
                    DiagnosticCode.CannotExport0OnlyLocalDeclarationsCanBeExportedFromAModule,
                    DiagnosticCode.X0OnlyRefersToATypeButIsBeingUsedAsAValueHere,
                    DiagnosticCode.X0OnlyRefersToATypeButIsBeingUsedAsANamespaceHere,
                    DiagnosticCode.CannotUseNamespace0AsAValue,
                    DiagnosticCode.CannotUseNamespace0AsAType,
                    DiagnosticCode.CannotAccess01Because0IsATypeButNotANamespaceDidYouMeanToRetrieveTheTypeOfTheProperty1In0With01,
                    DiagnosticCode.X0RefersToAValueButIsBeingUsedAsATypeHereDidYouMeanTypeof0,
                    DiagnosticCode.CannotFindNamespace0DidYouMean1,
                    DiagnosticCode.AClassCannotExtendAPrimitiveTypeLike0ClassesCanOnlyExtendConstructableValues
                ]))
            throw new InvalidOperationException($"Missing name diagnostics: {string.Join(',', codes)}");
        var spelling = file.DescendantsAndSelf().OfType<IdentifierNode>().Single(n => n.Text == "countr"u8);
        if (checker.SuggestedNameDeclarations.GetValueOrDefault(spelling)?.Name != "counter"u8)
            throw new InvalidOperationException("Name suggestion lost its declaration");
        return 2;
    }
}
