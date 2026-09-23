using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerNameTests
{
    internal static void Safety()
    {
        int assertions = 0;
        void Check(bool test)
        {
            if (!test)
                throw new InvalidOperationException($"Name/symbol ownership assertion {assertions + 1}");
            assertions++;
        }
        var file = Parser.ParseSourceFile(new("/scope.ts"), new SourceText("let x;"));
        var binding = Binder.Bind(file);
        SyntaxNode scope = file;
        const int depth = 20_000;
        for (int i = 0; i < depth; i++)
            scope = new BlockNode { Parent = scope };
        var use = new IdentifierNode { Text = "x", Parent = scope };
        var resolver = new NameResolver(new(), _ => binding) { Globals = binding.Locals };
        Check(resolver.Resolve(use, "x", SymbolFlags.Value) == binding.Locals["x"]);
        Check(resolver.Resolve(use, "x", SymbolFlags.Value, excludeGlobals: true) is null);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledResolver = new NameResolver(new(), _ => binding) { Globals = binding.Locals, Cancellation = canceled.Token };
        try
        {
            canceledResolver.Resolve(null, "x", SymbolFlags.Value);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }

        var deepFile = Parser.ParseSourceFile(
            new("/initializers.ts"),
            new SourceText("let x;function f(a=" + new string('(', depth) + "x??1" + new string(')', depth) + "){var x;}"));
        var deepBinding = Binder.Bind(deepFile);
        var identifier = deepFile.DescendantsAndSelf().OfType<IdentifierNode>().First(
            n => n.Text == "x" && n.Parent is BinaryExpressionNode);
        var function = deepFile.DescendantsAndSelf().OfType<FunctionDeclarationNode>().Single();
        var legacyOptions = new CompilerOptions();
        using var legacy = JsonDocument.Parse("\"es2015\"");
        legacyOptions.Set("target", legacy.RootElement);
        var modernOptions = new CompilerOptions();
        using var modern = JsonDocument.Parse("\"es2020\"");
        modernOptions.Set("target", modern.RootElement);
        Check(
            new NameResolver(
                legacyOptions,
                _ => deepBinding)
            { Globals = deepBinding.Locals }.Resolve(identifier, "x", SymbolFlags.Value) == deepBinding.Get(function)!.Locals["x"]);
        Check(
            new NameResolver(
                modernOptions,
                _ => deepBinding)
            { Globals = deepBinding.Locals }.Resolve(identifier, "x", SymbolFlags.Value) == deepBinding.Locals["x"]);

        var unknown = new Symbol(SymbolFlags.Property, "unknown");
        var globalThis = new Symbol(SymbolFlags.Module, "globalThis");
        int conflicts = 0;
        var merger = new SymbolMerger(unknown, globalThis, s => s, (_, _, _) => conflicts++);
        var left = new Symbol(SymbolFlags.Interface, "I");
        var right = new Symbol(SymbolFlags.Interface, "I");
        var merged = merger.MergeAsync(left, right).GetAwaiter().GetResult();
        Check(merged != left && merged != right && (merged.Flags & SymbolFlags.Transient) != 0);
        Check(left.Flags == SymbolFlags.Interface && right.Flags == SymbolFlags.Interface);
        Check(merger.GetMergedSymbol(left) == merged && merger.GetMergedSymbol(right) == merged);
        var conflicting = new Symbol(SymbolFlags.TypeAlias, "I");
        Check(merger.MergeAsync(merged, conflicting).GetAwaiter().GetResult() == merged && conflicts == 1);
        Check(
            merger.MergeAsync(globalThis, new(SymbolFlags.BlockScopedVariable, "globalThis")).GetAwaiter().GetResult() == globalThis
                && conflicts == 1);

        var a = new Symbol(SymbolFlags.ValueModule, "N");
        var b = new Symbol(SymbolFlags.ValueModule, "N");
        var parentA = a;
        var parentB = b;
        for (int i = 0; i < depth; i++)
        {
            var nextA = new Symbol(SymbolFlags.ValueModule, "N") { Parent = parentA };
            var nextB = new Symbol(SymbolFlags.ValueModule, "N") { Parent = parentB };
            parentA.ExportTable.Add("N", nextA);
            parentB.ExportTable.Add("N", nextB);
            parentA = nextA;
            parentB = nextB;
        }
        var mergedRoot = merger.MergeAsync(a, b).GetAwaiter().GetResult();
        var node = mergedRoot;
        for (int i = 0; i < depth; i++)
        {
            var child = node.Exports["N"];
            if (child.Parent != node)
                throw new InvalidOperationException("Merged export parent was not repaired");
            node = child;
        }
        Check(merger.GetMergedSymbol(parentA) == node && merger.GetMergedSymbol(parentB) == node);
        Check(a.Exports["N"].Parent == a && b.Exports["N"].Parent == b);

        using var cancellation = new CancellationTokenSource();
        int resolutions = 0;
        var interruptible = new SymbolMerger(unknown, globalThis, s =>
        {
            if (++resolutions == 25)
                cancellation.Cancel();
            return s;
        }, (_, _, _) => { });
        var table = new Dictionary<string, Symbol> { ["N"] = a };
        try
        {
            interruptible.MergeTableAsync(
                table,
                new Dictionary<string, Symbol> { ["N"] = b },
                cancellation: cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Merge cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }
        Check(table["N"] == a && interruptible.GetMergedSymbol(a) == a && interruptible.GetMergedSymbol(b) == b);
        var recovered = interruptible.MergeAsync(a, b).GetAwaiter().GetResult();
        Check(recovered != a && recovered != b && interruptible.GetMergedSymbol(a) == recovered);

        // A callback failure must also restore an already existing transient symbol.
        var before = new Symbol(SymbolFlags.Interface | SymbolFlags.Transient, "I");
        before.MemberTable["x"] = new(SymbolFlags.BlockScopedVariable, "x");
        var addition = new Symbol(SymbolFlags.Interface | SymbolFlags.ValueModule, "I") { ValueDeclaration = use };
        addition.DeclarationList.Add(use);
        addition.MemberTable["x"] = new(SymbolFlags.BlockScopedVariable, "x");
        var transactional = new SymbolMerger(unknown, globalThis, s => s, (_, _, _) => throw new InvalidDataException("conflict"));
        var originalMember = before.Members["x"];
        try
        {
            transactional.MergeAsync(before, addition).GetAwaiter().GetResult();
            throw new InvalidOperationException("Conflict ignored");
        }
        catch (InvalidDataException)
        {
            assertions++;
        }
        Check(before.Members["x"] == originalMember && transactional.GetMergedSymbol(addition) == addition
            && before.Flags == (SymbolFlags.Interface | SymbolFlags.Transient) && before.ValueDeclaration is null && before.Declarations.Count == 0);

        var importFile = Parser.ParseSourceFile(new("/imports.ts"), new SourceText("import {x as y} from 'p'; y;"));
        var importBinding = Binder.Bind(importFile);
        var alias = importBinding.Locals["y"];
        var aliasUse = importFile.DescendantsAndSelf().OfType<IdentifierNode>().Last(n => n.Text == "y");
        var reference = new ReferenceResolver(new(), _ => importBinding, new() { GetResolvedSymbol = _ => alias });
        Check(reference.GetReferencedImportDeclaration(aliasUse) == alias.Declarations[0]);
        reference = new(
            new(),
            _ => importBinding,
            new() { GetResolvedSymbol = _ => alias, GetTypeOnlyAliasDeclaration = (_, _) => alias.Declarations[0] });
        Check(reference.GetReferencedImportDeclaration(aliasUse) is null);
        var exported = new Symbol(SymbolFlags.Function, "f") { ValueDeclaration = use };
        reference = new(new(), _ => importBinding, new()
        {
            GetResolvedSymbol = _ => alias,
            GetExportSymbolOfValueSymbolIfExported = _ => exported,
            GetElementAccessExpressionName = _ => "computed"
        });
        Check(reference.GetReferencedValueDeclaration(aliasUse) == use && reference.GetReferencedMemberValueDeclaration(aliasUse) == use);
        Check(
            reference.GetElementAccessExpressionName(new ElementAccessExpressionNode()) == "computed"
                && reference.GetElementAccessExpressionName(null) == "");
        Console.WriteLine($"{assertions} name/symbol state assertions; scope, initializer and merge depth {depth}");
    }

    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                var data = Process(document.RootElement, out var file);
                if (document.RootElement.TryGetProperty("exportTree", out var export) && export.GetBoolean())
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("data");
                    BindingTests.Write(writer, data);
                    writer.WritePropertyName("tree");
                    BindingSyntax.Write(writer, file);
                    writer.WriteString("syntaxFingerprint", BindingTests.SyntaxFingerprint(file));
                    writer.WriteNumber("parseErrors", file.ParseDiagnostics.Count);
                    writer.WriteEndObject();
                }
                else
                    BindingTests.Write(writer, data);
            }
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private static object[] Process(JsonElement input, out SourceFileNode file)
    {
        file = Parser.ParseSourceFile(
            new(input.GetProperty("fileName").GetString()!),
            new SourceText(input.GetProperty("text").GetBytesFromBase64()));
        var binding = Binder.Bind(file);
        var options = new CompilerOptions();
        if (input.TryGetProperty("options", out var configuration))
            foreach (var property in configuration.EnumerateObject())
                options.Set(property.Name, property.Value);
        bool exclude = input.TryGetProperty("excludeGlobals", out var e) && e.GetBoolean();
        var nodes = file.DescendantsAndSelf().ToArray();
        var ids = nodes.Select((n, i) => (n, i: i + 1)).ToDictionary(p => p.n, p => p.i);
        int Node(SyntaxNode? n) => n is null ? 0 : ids.GetValueOrDefault(n);
        var symbols = new List<Symbol>();
        var symbolIds = new Dictionary<Symbol, int>();
        int Symbol(Symbol? s)
        {
            if (s is null)
                return 0;
            if (!symbolIds.TryGetValue(s, out int id))
            {
                symbols.Add(s);
                symbolIds.Add(s, id = symbols.Count);
            }
            return id;
        }
        var events = new List<object>();
        var cache = new Dictionary<SyntaxNode, bool>();
        var resolver = new NameResolver(options, _ => binding)
        {
            Globals = binding.IsModule ? null : binding.Locals,
            RequireSymbol = new(SymbolFlags.Property, "require"),
            Error = (n, m, args) => events.Add(new object[] { 0, Node(n), m.Code, args }),
            SymbolReferenced = (s, m) => events.Add(new object[] { 1, Symbol(s), (uint)m }),
            GetRequiresScopeChangeCache = n => cache.TryGetValue(n, out bool value) ? value : null,
            SetRequiresScopeChangeCache = (n, value) =>
            {
                cache[n] = value;
                events.Add(new object[] { 5, Node(n), value ? 1 : 0 });
            },
            OnFailedToResolveSymbol = (n, name, m, message) => events.Add(new object[] { 3, name, (uint)m, message.Code }),
            OnSuccessfullyResolvedSymbol = (n, s, m, last, associated, deferred) => events.Add(
                new object[] { 2, Symbol(s), (uint)m, Node(last), Node(associated), deferred ? 1 : 0 }),
            OnPropertyWithInvalidInitializer = (n, name, declaration, s) =>
            {
                events.Add(new object[] { 4, Node(declaration), Symbol(s) });
                return false;
            }
        };
        var rows = new List<object>();
        foreach (var node in nodes.OfType<IdentifierNode>())
            foreach (var meaning in new[] { SymbolFlags.Value, SymbolFlags.Type, SymbolFlags.Namespace,
                SymbolFlags.Value | SymbolFlags.Alias | SymbolFlags.ExportValue, SymbolFlags.All, SymbolFlags.None })
            {
                events = [];
                var result = resolver.Resolve(node, node.Text, meaning, Messages.Cannot_find_name_0, true, exclude);
                rows.Add(new object[] { Node(node), (uint)meaning, Symbol(result), events.ToArray() });
            }
        var references = new ReferenceResolver(options, _ => binding, new() { ResolveName = resolver.Resolve });
        var rr = nodes.OfType<IdentifierNode>().Select(node => new object[] { Node(node),
            Node(references.GetReferencedExportContainer(node, false)), Node(references.GetReferencedExportContainer(node, true)),
            Node(references.GetReferencedImportDeclaration(node)), Node(references.GetReferencedValueDeclaration(node)),
            references.GetReferencedValueDeclarations(node).Select(Node).ToArray(), Node(
                references.GetReferencedMemberValueDeclaration(node)) }).ToArray();
        var sr = symbols.Select(s => new object[] { TypeScript.Compiler.Binding.Symbol.EscapeName(s.Name), (uint)s.Flags,
            Node(s.ValueDeclaration), s.Declarations.Select(Node).ToArray() }).ToArray();
        return [rows, sr, rr];
    }
}
