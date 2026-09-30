using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerSymbolTests
{
    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                BindingTests.Write(writer, Process(document.RootElement));
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private static object[] Process(JsonElement input)
    {
        var file = Parser.ParseSourceFile(
            new("/declarations.ts"u8),
            new SourceText("namespace N{} function f(){} let x=1; x=y; x.z; x['z']; f();"u8));
        var declarations = file.DescendantsAndSelf().GroupBy(n => n.Kind).ToDictionary(g => g.Key, g => g.First());
        var definitions = input.GetProperty("symbols"u8).EnumerateArray().ToArray();
        var symbols = new List<Symbol?> { null };
        foreach (var definition in definitions)
        {
            var symbol = new Symbol((SymbolFlags)definition.GetProperty("flags"u8).GetUInt32(), JsonStrings.GetString(definition.GetProperty("name"u8))!);
            if (definition.TryGetProperty("declaration"u8, out var kind) && kind.GetInt32() != 0)
            {
                symbol.ValueDeclaration = declarations[(SyntaxKind)kind.GetInt32()];
                symbol.DeclarationList = symbol.DeclarationList.Add(symbol.ValueDeclaration);
            }
            symbols.Add(symbol);
        }
        var unknown = new Symbol(SymbolFlags.Property | SymbolFlags.Transient, "unknown"u8);
        var aliases = new Dictionary<Symbol, Symbol>();
        for (int i = 0; i < definitions.Length; i++)
        {
            var definition = definitions[i];
            var symbol = symbols[i + 1]!;
            if (definition.TryGetProperty("parent"u8, out var parent))
                symbol.Parent = symbols[parent.GetInt32()];
            if (definition.TryGetProperty("aliasTarget"u8, out var target) && target.GetInt32() != 0)
                aliases.Add(symbol, target.GetInt32() < 0 ? unknown : symbols[target.GetInt32()]!);
            foreach (Utf8String key in new Utf8String[] { "members"u8, "exports"u8 })
                if (definition.TryGetProperty(key, out var entries))
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var member = symbols[entry.GetInt32()]!;
                        (key == "members"u8 ? symbol.MemberTable : symbol.ExportTable).Add(member.Name, member);
                    }
        }
        var merger = new SymbolMerger(unknown, new(SymbolFlags.Module, "globalThis"u8),
            symbol => aliases.GetValueOrDefault(symbol, symbol),
            (_, _, _) => throw new InvalidOperationException("Merge fixture contains an untested diagnostic conflict"));
        foreach (var operation in input.GetProperty("operations"u8).EnumerateArray())
            symbols.Add(
                merger.MergeAsync(
                symbols[operation.GetProperty("left"u8).GetInt32()]!,
                symbols[operation.GetProperty("right"u8).GetInt32()]!,
                operation.TryGetProperty("unidirectional"u8, out var oneWay) && oneWay.GetBoolean()).GetAwaiter().GetResult());
        var ids = new Dictionary<Symbol, int>();
        var queue = new List<Symbol>();
        int Ref(Symbol? symbol)
        {
            if (symbol is null)
                return 0;
            if (!ids.TryGetValue(symbol, out int id))
            {
                queue.Add(symbol);
                ids.Add(symbol, id = queue.Count);
            }
            return id;
        }
        var results = new List<int>();
        var merges = new List<int>();
        foreach (var symbol in symbols.Skip(1))
        {
            results.Add(Ref(symbol));
            merges.Add(Ref(merger.GetMergedSymbol(symbol)));
        }
        object[] Table(IReadOnlyDictionary<Utf8String, Symbol> table) => table.OrderBy(p => p.Key, Utf8StringComparer.Ordinal)
            .Select(p => (object)new object[] { p.Key, Ref(p.Value) }).ToArray();
        var rows = new List<object>();
        for (int i = 0; i < queue.Count; i++)
        {
            var symbol = queue[i];
            rows.Add(new object[] { symbol.Name, (uint)symbol.Flags, Ref(symbol.Parent), (int)(symbol.ValueDeclaration?.Kind ?? 0),
                symbol.Declarations.Select(n => (int)n.Kind).ToArray(), Table(symbol.Members), Table(symbol.Exports) });
        }
        uint[] flags = input.TryGetProperty("flags"u8, out var list)
            ? list.EnumerateArray().Select(v => (uint)SymbolMerger.ExcludedFlags((SymbolFlags)v.GetUInt32())).ToArray()
            : [];
        return [results, merges, rows, flags];
    }
}
