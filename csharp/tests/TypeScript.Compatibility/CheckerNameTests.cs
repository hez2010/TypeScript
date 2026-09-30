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
        var compact = new SymbolTable();
        var firstValue = new Symbol(SymbolFlags.Property, "value"u8);
        var secondValue = new Symbol(SymbolFlags.Property, "other"u8);
        var keys = compact.Keys;
        compact.Add("alias"u8, firstValue);
        Check(compact[Utf8String.Copy("alias"u8)] == firstValue && compact.Count == 1);
        Check(!compact.TryAdd("alias"u8, secondValue) && compact["alias"u8] == firstValue);
        compact["alias"u8] = secondValue;
        Check(compact.Values.Single() == secondValue && compact.Single().Key == "alias"u8);
        compact.Add(Utf8String.Copy([0x00, 0xED, 0xA0, 0x80]), firstValue);
        Check(compact.Count == 2 && compact["alias"u8] == secondValue && compact[Utf8String.Copy([0x00, 0xED, 0xA0, 0x80])] == firstValue);
        Check(keys.SequenceEqual(new Utf8String[] { "alias"u8, Utf8String.Copy([0x00, 0xED, 0xA0, 0x80]) }));
        var copied = new Dictionary<Utf8String, Symbol>(compact);
        Check(copied.Count == 2 && copied["alias"u8] == secondValue);
        for (int i = 0; i < 8; i++)
            compact.Add(Utf8String.Format(i), firstValue);
        Check(compact.Count == 10 && compact["alias"u8] == secondValue && compact["7"u8] == firstValue);
        try
        {
            ((ICollection<KeyValuePair<Utf8String, Symbol>>)compact).Clear();
            throw new InvalidOperationException("Published symbol table is mutable");
        }
        catch (NotSupportedException)
        {
            assertions++;
        }
        compact.Clear();
        compact.Add("fresh"u8, firstValue);
        Check(compact.Count == 1 && keys.Single() == "fresh"u8 && !compact.ContainsKey("alias"u8));

        var file = Parser.ParseSourceFile(new("/scope.ts"u8), new SourceText("let x;"u8));
        var binding = Binder.Bind(file);
        SyntaxNode scope = file;
        const int depth = 20_000;
        for (int i = 0; i < depth; i++)
            scope = new BlockNode { Parent = scope };
        var use = new IdentifierNode { Text = "x"u8, Parent = scope };
        var resolver = new NameResolver(new(), _ => binding) { Globals = binding.Locals };
        Check(resolver.Resolve(use, "x"u8, SymbolFlags.Value) == binding.Locals["x"u8]);
        Check(resolver.Resolve(use, "x"u8, SymbolFlags.Value, excludeGlobals: true) is null);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledResolver = new NameResolver(new(), _ => binding) { Globals = binding.Locals, Cancellation = canceled.Token };
        try
        {
            canceledResolver.Resolve(null, "x"u8, SymbolFlags.Value);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }

        var deepFile = Parser.ParseSourceFile(
            new("/initializers.ts"u8),
            new SourceText(Utf8String.Concat("let x;function f(a="u8, new Utf8String('(', depth), "x??1"u8) + new Utf8String(')', depth) + "){var x;}"u8));
        var deepBinding = Binder.Bind(deepFile);
        var identifier = deepFile.DescendantsAndSelf().OfType<IdentifierNode>().First(
            n => n.Text == "x"u8 && n.Parent is BinaryExpressionNode);
        var function = deepFile.DescendantsAndSelf().OfType<FunctionDeclarationNode>().Single();
        var legacyOptions = new CompilerOptions();
        using var legacy = JsonDocument.Parse("\"es2015\"");
        legacyOptions.Set("target"u8, legacy.RootElement);
        var modernOptions = new CompilerOptions();
        using var modern = JsonDocument.Parse("\"es2020\"");
        modernOptions.Set("target"u8, modern.RootElement);
        Check(
            new NameResolver(
                legacyOptions,
                _ => deepBinding)
            { Globals = deepBinding.Locals }.Resolve(identifier, "x"u8, SymbolFlags.Value) == deepBinding.Get(function)!.Value.Locals["x"u8]);
        Check(
            new NameResolver(
                modernOptions,
                _ => deepBinding)
            { Globals = deepBinding.Locals }.Resolve(identifier, "x"u8, SymbolFlags.Value) == deepBinding.Locals["x"u8]);

        var unknown = new Symbol(SymbolFlags.Property, "unknown"u8);
        var globalThis = new Symbol(SymbolFlags.Module, "globalThis"u8);
        int conflicts = 0;
        var merger = new SymbolMerger(unknown, globalThis, s => s, (_, _, _) => conflicts++);
        var left = new Symbol(SymbolFlags.Interface, "I"u8);
        var right = new Symbol(SymbolFlags.Interface, "I"u8);
        var merged = merger.MergeAsync(left, right).GetAwaiter().GetResult();
        Check(merged != left && merged != right && (merged.Flags & SymbolFlags.Transient) != 0);
        Check(left.Flags == SymbolFlags.Interface && right.Flags == SymbolFlags.Interface);
        Check(merger.GetMergedSymbol(left) == merged && merger.GetMergedSymbol(right) == merged);
        var conflicting = new Symbol(SymbolFlags.TypeAlias, "I"u8);
        Check(merger.MergeAsync(merged, conflicting).GetAwaiter().GetResult() == merged && conflicts == 1);
        Check(
            merger.MergeAsync(globalThis, new(SymbolFlags.BlockScopedVariable, "globalThis"u8)).GetAwaiter().GetResult() == globalThis
                && conflicts == 1);

        var a = new Symbol(SymbolFlags.ValueModule, "N"u8);
        var b = new Symbol(SymbolFlags.ValueModule, "N"u8);
        var parentA = a;
        var parentB = b;
        for (int i = 0; i < depth; i++)
        {
            var nextA = new Symbol(SymbolFlags.ValueModule, "N"u8) { Parent = parentA };
            var nextB = new Symbol(SymbolFlags.ValueModule, "N"u8) { Parent = parentB };
            parentA.ExportTable.Add("N"u8, nextA);
            parentB.ExportTable.Add("N"u8, nextB);
            parentA = nextA;
            parentB = nextB;
        }
        var mergedRoot = merger.MergeAsync(a, b).GetAwaiter().GetResult();
        var node = mergedRoot;
        for (int i = 0; i < depth; i++)
        {
            var child = node.Exports["N"u8];
            if (child.Parent != node)
                throw new InvalidOperationException("Merged export parent was not repaired");
            node = child;
        }
        Check(merger.GetMergedSymbol(parentA) == node && merger.GetMergedSymbol(parentB) == node);
        Check(a.Exports["N"u8].Parent == a && b.Exports["N"u8].Parent == b);

        using var cancellation = new CancellationTokenSource();
        int resolutions = 0;
        var interruptible = new SymbolMerger(unknown, globalThis, s =>
        {
            if (++resolutions == 25)
                cancellation.Cancel();
            return s;
        }, (_, _, _) => { });
        var table = new SymbolTable { ["N"u8] = a };
        try
        {
            interruptible.MergeTableAsync(
                table,
                new Dictionary<Utf8String, Symbol> { ["N"u8] = b },
                cancellation: cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Merge cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }
        Check(table["N"u8] == a && interruptible.GetMergedSymbol(a) == a && interruptible.GetMergedSymbol(b) == b);
        var recovered = interruptible.MergeAsync(a, b).GetAwaiter().GetResult();
        Check(recovered != a && recovered != b && interruptible.GetMergedSymbol(a) == recovered);

        // A callback failure must also restore an already existing transient symbol.
        var before = new Symbol(SymbolFlags.Interface | SymbolFlags.Transient, "I"u8);
        before.MemberTable["x"u8] = new(SymbolFlags.BlockScopedVariable, "x"u8);
        var addition = new Symbol(SymbolFlags.Interface | SymbolFlags.ValueModule, "I"u8) { ValueDeclaration = use };
        addition.DeclarationList = addition.DeclarationList.Add(use);
        addition.MemberTable["x"u8] = new(SymbolFlags.BlockScopedVariable, "x"u8);
        var transactional = new SymbolMerger(unknown, globalThis, s => s, (_, _, _) => throw new InvalidDataException("conflict"));
        var originalMember = before.Members["x"u8];
        try
        {
            transactional.MergeAsync(before, addition).GetAwaiter().GetResult();
            throw new InvalidOperationException("Conflict ignored");
        }
        catch (InvalidDataException)
        {
            assertions++;
        }
        Check(before.Members["x"u8] == originalMember && transactional.GetMergedSymbol(addition) == addition
            && before.Flags == (SymbolFlags.Interface | SymbolFlags.Transient) && before.ValueDeclaration is null && before.Declarations.Length == 0);

        var importFile = Parser.ParseSourceFile(new("/imports.ts"u8), new SourceText("import {x as y} from 'p'; y;"u8));
        var importBinding = Binder.Bind(importFile);
        var alias = importBinding.Locals["y"u8];
        var aliasUse = importFile.DescendantsAndSelf().OfType<IdentifierNode>().Last(n => n.Text == "y"u8);
        var reference = new ReferenceResolver(new(), _ => importBinding, new() { GetResolvedSymbol = _ => alias });
        Check(reference.GetReferencedImportDeclaration(aliasUse) == alias.Declarations[0]);
        reference = new(
            new(),
            _ => importBinding,
            new() { GetResolvedSymbol = _ => alias, GetTypeOnlyAliasDeclaration = (_, _) => alias.Declarations[0] });
        Check(reference.GetReferencedImportDeclaration(aliasUse) is null);
        var exported = new Symbol(SymbolFlags.Function, "f"u8) { ValueDeclaration = use };
        reference = new(new(), _ => importBinding, new()
        {
            GetResolvedSymbol = _ => alias,
            GetExportSymbolOfValueSymbolIfExported = _ => exported,
            GetElementAccessExpressionName = _ => "computed"u8
        });
        Check(reference.GetReferencedValueDeclaration(aliasUse) == use && reference.GetReferencedMemberValueDeclaration(aliasUse) == use);
        Check(
            reference.GetElementAccessExpressionName(new ElementAccessExpressionNode()) == "computed"u8
                && reference.GetElementAccessExpressionName(null) == ""u8);
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
                if (document.RootElement.TryGetProperty("exportTree"u8, out var export) && export.GetBoolean())
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("data"u8);
                    BindingTests.Write(writer, data);
                    writer.WritePropertyName("tree"u8);
                    BindingSyntax.Write(writer, file);
                    writer.WriteString("syntaxFingerprint"u8, BindingTests.SyntaxFingerprint(file));
                    writer.WriteNumber("parseErrors"u8, file.ParseDiagnostics.Count);
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
            new(JsonStrings.GetString(input.GetProperty("fileName"u8))!),
            new SourceText(input.GetProperty("text"u8).GetBytesFromBase64()));
        var binding = Binder.Bind(file);
        var options = new CompilerOptions();
        if (input.TryGetProperty("options"u8, out var configuration))
            foreach (var property in configuration.EnumerateObject())
                options.Set(JsonStrings.GetName(property), property.Value);
        bool exclude = input.TryGetProperty("excludeGlobals"u8, out var e) && e.GetBoolean();
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
            RequireSymbol = new(SymbolFlags.Property, "require"u8),
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
