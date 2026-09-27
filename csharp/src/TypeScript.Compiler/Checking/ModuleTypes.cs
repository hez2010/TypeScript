using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Checking;

internal sealed class ModuleTypes(TypeContext context, CheckerLinks links, AliasResolver aliases, TypeOrder order)
{
    internal async ValueTask<Symbol> CloneAsync(Symbol symbol, StructuredType resolved, SyntaxNode originatingImport,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(resolved);
        if ((resolved.ObjectFlags & ObjectFlags.MembersResolved) == 0)
            throw new ArgumentException("Module type members must be resolved before cloning", nameof(resolved));
        var result = new Symbol(symbol.Flags | S.Transient, symbol.Name)
        { Parent = symbol.Parent, ValueDeclaration = symbol.ValueDeclaration };
        result.DeclarationList = result.DeclarationList.AddRange(symbol.Declarations);
        foreach (var pair in symbol.Members)
            result.MemberTable.Add(pair.Key, pair.Value);
        foreach (var pair in symbol.Exports)
            result.ExportTable.Add(pair.Key, pair.Value);
        var declared = new List<Symbol>();
        var inherited = new List<Symbol>();
        foreach (var (name, member) in resolved.Members ?? new Dictionary<TextSlice, Symbol>())
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Named(name) || (member.Flags & S.Value) == 0
                && ((member.Flags & S.Alias) == 0
                    || (await aliases.FlagsAsync(
                        member,
                        excludeTypeOnly: true,
                        cancellation: cancellation).ConfigureAwait(false) & S.Value) == 0))
                continue;
            if ((symbol.Flags & (S.Class | S.Interface)) != 0 && member.ValueDeclaration is { } value
                && symbol.Declarations.Any(d => value.Pos >= d.Pos && value.End <= d.End))
                declared.Add(member);
            else
                inherited.Add(member);
        }
        declared.Sort(order.CompareSymbols);
        inherited.Sort(order.CompareSymbols);
        var type = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved, result);
        type.Members = resolved.Members;
        type.Properties = Array.AsReadOnly<Symbol>([.. declared, .. inherited]);
        type.IndexInfos = resolved.IndexInfos;
        var exports = links.ExportTypes.Get(result);
        exports.Target = symbol;
        exports.OriginatingImport = originatingImport;
        links.Values.Get(result).ResolvedType = type;
        return result;
    }

    private static bool Named(TextSlice name) => !name.Span.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal)
        || name.Span.StartsWith(Symbol.InternalPrefix + Symbol.InternalPrefix, StringComparison.Ordinal)
        || name.Length < 2 || name[1] is '@' or '#';
}
