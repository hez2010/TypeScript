using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerEmitQueryTests
{
    internal static async Task<int> LinkedSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Emit linking assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("reactNamespace", "\"Custom.Nested\"");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode("import {named as first,named as second} from './dep';first;second;"),
            ["/project/dep.ts"] = Wtf8.Encode("export const named=1;")
        }), "/project", new("/project/tsconfig.json", options, ["/project/main.ts", "/project/dep.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts")!.Syntax;
        var aliases = source.DescendantsAndSelf().OfType<ImportSpecifierNode>().ToArray();
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        Check(!await checker.IsReferencedAliasForEmitAsync(aliases[0]) && !await checker.IsReferencedAliasForEmitAsync(aliases[1]));
        using var stop = new CancellationTokenSource();
        bool reached = false;
        checker.BeforeEmitLinkedReference = node =>
        {
            if (node is IdentifierNode { Text: "second", Parent: ExpressionStatementNode })
            {
                reached = true;
                stop.Cancel();
            }
        };
        try
        {
            await checker.MarkLinkedReferencesForEmitAsync(source, stop.Token);
            throw new InvalidOperationException("Canceled linking completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        checker.BeforeEmitLinkedReference = null;
        Check(reached);
        Check(!await checker.IsReferencedAliasForEmitAsync(aliases[0]) && !await checker.IsReferencedAliasForEmitAsync(aliases[1]));
        await checker.MarkLinkedReferencesForEmitAsync(source);
        Check(await checker.IsReferencedAliasForEmitAsync(aliases[0]) && await checker.IsReferencedAliasForEmitAsync(aliases[1]));
        await checker.MarkLinkedReferencesForEmitAsync(source);
        Check(await checker.IsReferencedAliasForEmitAsync(aliases[0]) && await checker.IsReferencedAliasForEmitAsync(aliases[1]));
        var other = await program.CreateCheckerAsync();
        var firstUse = source.DescendantsAndSelf().OfType<IdentifierNode>().Single(
            n => n.Text == "first" && n.Parent is ExpressionStatementNode);
        await other.GetExpressionTypeAsync(firstUse);
        Check(await other.IsReferencedAliasForEmitAsync(aliases[0]));
        using var secondStop = new CancellationTokenSource();
        other.BeforeEmitLinkedReference = node =>
        {
            if (node is IdentifierNode { Text: "second", Parent: ExpressionStatementNode })
                secondStop.Cancel();
        };
        try
        {
            await other.MarkLinkedReferencesForEmitAsync(source, secondStop.Token);
            throw new InvalidOperationException("Canceled linking completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        other.BeforeEmitLinkedReference = null;
        Check(await other.IsReferencedAliasForEmitAsync(aliases[0]) && !await other.IsReferencedAliasForEmitAsync(aliases[1]));
        Check(await checker.GetJsxFactoryForEmitAsync(null) is null);
        var factory = await checker.GetJsxFactoryForEmitAsync(source);
        Check(
            factory is QualifiedNameNode { Left: IdentifierNode { Text: "Custom.Nested" }, Right: IdentifierNode { Text: "createElement" } });
        Check(await checker.GetJsxFactoryForEmitAsync(null) == factory);
        Check(await checker.GetJsxFactoryForEmitAsync(source, true) is null);
        Check(snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        return checks;
    }

    internal static async Task WriteServicesAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, Checker checker, Func<SyntaxNode?, int> nodeId)
    {
        writer.WriteStartArray("emitQueries");
        foreach (var node in nodes.Where(
            n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) == true))
        {
            if (QuerySyntax.Declaration(node) && node.Parent is not null)
                foreach (var mask in new[]
                {
                ModifierFlags.All,
                ModifierFlags.Export | ModifierFlags.Ambient,
                ModifierFlags.Private | ModifierFlags.Protected
            })
                {
                    Start(0, node);
                    writer.WriteNumberValue((uint)mask);
                    writer.WriteNumberValue((uint)await checker.GetEffectiveDeclarationFlagsForEmitAsync(node, mask));
                    writer.WriteEndArray();
                }
            if (node is EnumMemberNode member)
            {
                var value = await checker.GetEnumMemberValueForEmitAsync(member);
                Start(1, node);
                Scalar(value.Value);
                writer.WriteBooleanValue(value.IsSyntacticallyString);
                writer.WriteBooleanValue(value.ResolvedOtherFiles);
                writer.WriteBooleanValue(value.HasExternalReferences);
                writer.WriteEndArray();
            }
            if (node is EnumMemberNode or PropertyAccessExpressionNode or ElementAccessExpressionNode)
            {
                Start(2, node);
                Scalar(await checker.GetConstantValueForEmitAsync(node));
                writer.WriteEndArray();
            }
            if (node is PropertyDeclarationNode || node is BinaryExpressionNode && (node.Flags & NodeFlags.JavaScriptFile) != 0)
            {
                Start(3, node);
                writer.WriteBooleanValue(await checker.IsThisPropertyAssignmentRedundantForEmitAsync(node));
                writer.WriteEndArray();
            }
        }
        writer.WriteEndArray();
        void Start(int operation, SyntaxNode node)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(operation);
            writer.WriteNumberValue(nodeId(node));
        }
        void Scalar(object? value)
        {
            if (value is string text)
            {
                writer.WriteStartArray();
                writer.WriteStringValue("string");
                writer.WriteBase64StringValue(Wtf8.Encode(text));
                writer.WriteEndArray();
            }
            else if (value is double number)
            {
                writer.WriteStartArray();
                writer.WriteStringValue("number");
                writer.WriteStringValue(
                    BitConverter.DoubleToUInt64Bits(number).ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteEndArray();
            }
            else
                writer.WriteNullValue();
        }
    }

    internal static async Task WriteJsxAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, Checker checker, Func<SyntaxNode?, int> nodeId)
    {
        SyntaxNode?[] locations = [null,..nodes.Where(
            n=>SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main.",StringComparison.Ordinal)==true
            && n is SourceFileNode or JsxOpeningFragmentNode or JsxOpeningElementNode or JsxSelfClosingElementNode),null];
        writer.WriteStartArray("emitQueries");
        foreach (var location in locations)
            foreach (bool fragment in new[] { false, true })
            {
                var node = await checker.GetJsxFactoryForEmitAsync(location, fragment);
                writer.WriteStartArray();
                writer.WriteNumberValue(nodeId(location));
                writer.WriteBooleanValue(fragment);
                if (node is null)
                    writer.WriteNullValue();
                else
                    writer.WriteStringValue(
                        Checker.PrintDiagnosticNode(node, sourceFile: location is null ? null : SemanticSyntax.Source(location)));
                writer.WriteEndArray();
            }
        writer.WriteEndArray();
    }

    internal static async Task WriteLinksAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, Checker checker, Func<SyntaxNode?, int> nodeId)
    {
        var main = nodes.Where(
            n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) == true).ToArray();
        writer.WriteStartArray("emitQueries");
        for (int pass = 0; pass < 3; pass++)
        {
            if (pass != 0)
                foreach (var file in main.OfType<SourceFileNode>())
                    await checker.MarkLinkedReferencesForEmitAsync(file);
            foreach (var node in main.Where(ReferenceResolver.IsAliasDeclaration))
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(pass);
                writer.WriteNumberValue(nodeId(node));
                writer.WriteBooleanValue(await checker.IsReferencedAliasForEmitAsync(node));
                writer.WriteEndArray();
            }
        }
        writer.WriteEndArray();
    }

    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Emit query assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode(
                "import {named as value} from './dep';function f(x:string):string;function f(x:any){return x}function g(x:number=1,y:string,z?:number){}const literal=1;const widened:number=1;type Num=number;let n:Num;value;"),
            ["/project/dep.ts"] = Wtf8.Encode("export const named=1;")
        }), "/project", new("/project/tsconfig.json", options, ["/project/main.ts", "/project/dep.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts")!.Syntax;
        var nodes = source.DescendantsAndSelf().ToArray();
        var import = nodes.OfType<ImportDeclarationNode>().Single();
        var alias = nodes.OfType<ImportSpecifierNode>().Single();
        var f = nodes.OfType<FunctionDeclarationNode>().Last(n => n.Name?.Text == "f");
        var g = nodes.OfType<FunctionDeclarationNode>().Single(n => n.Name?.Text == "g");
        Check(await checker.IsImplementationOfOverloadAsync(f));
        Check(!await checker.IsImplementationOfOverloadAsync(g));
        Check(
            await checker.IsLiteralConstDeclarationAsync(
                nodes.OfType<VariableDeclarationNode>().Single(n => n.Name is IdentifierNode { Text: "literal" })));
        Check(
            !await checker.IsLiteralConstDeclarationAsync(
                nodes.OfType<VariableDeclarationNode>().Single(n => n.Name is IdentifierNode { Text: "widened" })));
        Check(await checker.IsValueAliasForEmitAsync(alias));
        Check((await checker.GetExternalModuleFileForEmitAsync(import))?.FileName == "/project/dep.ts");
        Check(!await checker.IsOptionalParameterForEmitAsync(g.Parameters![0]));
        Check(await checker.RequiresImplicitUndefinedForEmitAsync(g.Parameters[0], null, source));
        Check(await checker.IsOptionalParameterForEmitAsync(g.Parameters[2]));
        Check(
            await checker.GetTypeReferenceSerializationKindAsync(
                nodes.OfType<TypeReferenceNode>().Single().TypeName,
                source) == TypeReferenceSerializationKind.NumberLikeType);
        var synthetic = new NodeFactory().NewIdentifier("generated");
        synthetic.Flags |= NodeFlags.Synthesized;
        Check(await checker.GetReferencedImportForEmitAsync(synthetic) is null);
        await checker.SetReferencedImportForEmitAsync(synthetic, alias);
        Check(await checker.GetReferencedImportForEmitAsync(synthetic) == alias);
        var other = await program.CreateCheckerAsync();
        Check(await other.GetReferencedImportForEmitAsync(synthetic) is null);
        var foreign = Parser.ParseSourceFile(new("/foreign.ts"), new SourceText("let foreign=1;"));
        try
        {
            await checker.SetReferencedImportForEmitAsync(synthetic, foreign);
            throw new InvalidOperationException("Foreign declaration accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Check(await checker.GetReferencedImportForEmitAsync(synthetic) == alias);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            await checker.GetExternalModuleFileForEmitAsync(import, stop.Token);
            throw new InvalidOperationException("Canceled emit query completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check((await checker.GetExternalModuleFileForEmitAsync(import))?.FileName == "/project/dep.ts");
        Check(!await checker.IsLateBoundDeclarationAsync(synthetic) && await checker.IsValueAliasForEmitAsync(synthetic));
        return checks;
    }

    internal static async Task WriteSerializationAsync(
        Utf8JsonWriter writer,
        SyntaxNode[] nodes,
        Checker checker,
        Func<SyntaxNode?, int> nodeId)
    {
        var main = nodes.Where(
            n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) == true).ToArray();
        SyntaxNode?[] locations = [null, .. main.Where(n => n is SourceFileNode or ClassDeclarationNode or FunctionDeclarationNode)];
        writer.WriteStartArray("emitQueries");
        foreach (var node in main.OfType<TypeReferenceNode>())
            foreach (var location in locations)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(nodeId(node));
                writer.WriteNumberValue(nodeId(location));
                writer.WriteNumberValue((int)await checker.GetTypeReferenceSerializationKindAsync(node.TypeName, location));
                writer.WriteEndArray();
            }
        writer.WriteEndArray();
    }

    internal static async Task WriteReferencesAsync(
        Utf8JsonWriter writer,
        SyntaxNode[] nodes,
        Checker checker,
        Func<SyntaxNode?, int> nodeId,
        Func<Symbol?, int> symbolId)
    {
        writer.WriteStartArray("emitQueries");
        foreach (var node in nodes.Where(
            n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) == true))
        {
            if (node is IdentifierNode identifier)
            {
                var values = await checker.GetReferencedValuesForEmitAsync(identifier);
                Start(0, node);
                writer.WriteNumberValue(nodeId(await checker.GetReferencedExportContainerForEmitAsync(identifier, false)));
                writer.WriteNumberValue(nodeId(await checker.GetReferencedExportContainerForEmitAsync(identifier, true)));
                writer.WriteNumberValue(nodeId(await checker.GetReferencedImportForEmitAsync(identifier)));
                writer.WriteNumberValue(nodeId(await checker.GetReferencedValueForEmitAsync(identifier)));
                writer.WriteStartArray();
                foreach (var value in values)
                    writer.WriteNumberValue(nodeId(value));
                writer.WriteEndArray();
                writer.WriteEndArray();
            }
            if (node is PropertyAccessExpressionNode or ElementAccessExpressionNode or QualifiedNameNode)
            {
                var before = await checker.GetReferencedMemberForEmitAsync(node);
                await checker.GetSymbolAtLocationAsync(node);
                var after = await checker.GetReferencedMemberForEmitAsync(node);
                Start(1, node);
                writer.WriteNumberValue(nodeId(before));
                writer.WriteNumberValue(nodeId(after));
                writer.WriteEndArray();
            }
            if (node is ElementAccessExpressionNode element)
            {
                Start(2, node);
                writer.WriteStringValue(await checker.GetElementAccessNameForEmitAsync(element));
                writer.WriteEndArray();
            }
            if (SemanticSyntax.FunctionDeclarationLike(node))
            {
                var properties = await checker.GetContainerFunctionPropertiesForEmitAsync(node);
                Start(3, node);
                writer.WriteStartArray();
                foreach (var property in properties)
                    writer.WriteNumberValue(symbolId(property));
                writer.WriteEndArray();
                writer.WriteBooleanValue(await checker.IsExpandoFunctionForEmitAsync(node));
                writer.WriteEndArray();
            }
        }
        writer.WriteEndArray();
        void Start(int operation, SyntaxNode node)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(operation);
            writer.WriteNumberValue(nodeId(node));
        }
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, Checker checker, Func<SyntaxNode?, int> nodeId)
    {
        var main = nodes.Where(
            n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) == true).ToArray();
        SyntaxNode?[] locations = [null, .. main.Where(n => n is SourceFileNode or FunctionDeclarationNode or ClassDeclarationNode)];
        writer.WriteStartArray("emitQueries");
        foreach (var node in main)
        {
            if (QuerySyntax.Declaration(node) || node is BinaryExpressionNode && (node.Flags & NodeFlags.JavaScriptFile) != 0)
            {
                Start(0, node);
                writer.WriteBooleanValue(await checker.IsLateBoundDeclarationAsync(node));
                writer.WriteBooleanValue(await checker.IsLiteralConstDeclarationAsync(node));
                writer.WriteBooleanValue(await checker.IsReferencedAliasForEmitAsync(node));
                writer.WriteBooleanValue(await checker.IsValueAliasForEmitAsync(node));
                writer.WriteBooleanValue(await checker.IsTopLevelValueImportEqualsAsync(node));
                writer.WriteEndArray();
            }
            if (Signatures.FunctionLike(node))
            {
                Start(1, node);
                writer.WriteBooleanValue(await checker.IsImplementationOfOverloadAsync(node));
                writer.WriteEndArray();
            }
            if (node is ParameterDeclarationNode)
            {
                Start(2, node);
                writer.WriteBooleanValue(await checker.IsOptionalParameterForEmitAsync(node));
                writer.WriteEndArray();
            }
            if (node is PropertyAccessExpressionNode)
            {
                Start(3, node);
                writer.WriteBooleanValue(await checker.IsGlobalSymbolObjectReferenceAsync(node));
                writer.WriteEndArray();
            }
            if (node is ImportDeclarationNode { ModuleSpecifier: not null } or ExportDeclarationNode { ModuleSpecifier: not null }
                or ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode }
                or ImportTypeNode { Argument: LiteralTypeNode { Literal: StringLiteralNode } }
                or ModuleDeclarationNode { Name: StringLiteralNode })
            {
                Start(4, node);
                writer.WriteStringValue((await checker.GetExternalModuleFileForEmitAsync(node))?.FileName ?? "");
                writer.WriteBooleanValue(node is ImportDeclarationNode import && await checker.IsImportRequiredByAugmentationAsync(import));
                writer.WriteEndArray();
            }
            if (node is ParameterDeclarationNode or PropertyDeclarationNode or PropertySignatureDeclarationNode)
                foreach (var location in locations)
                {
                    Start(5, node);
                    writer.WriteNumberValue(nodeId(location));
                    writer.WriteBooleanValue(await checker.RequiresImplicitUndefinedForEmitAsync(node, null, location));
                    writer.WriteEndArray();
                }
            if (node is SourceFileNode or FunctionDeclarationNode or ClassDeclarationNode)
                foreach (string name in new[] { "Symbol", "globalThis", "value", "T", "Missing" })
                {
                    Start(6, node);
                    writer.WriteStringValue(name);
                    writer.WriteBooleanValue(await checker.IsNameResolvableForEmitAsync(node, name));
                    writer.WriteEndArray();
                }
        }
        writer.WriteEndArray();
        void Start(int operation, SyntaxNode node)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(operation);
            writer.WriteNumberValue(nodeId(node));
        }
    }
}
