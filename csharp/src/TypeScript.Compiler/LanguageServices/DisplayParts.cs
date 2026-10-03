using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.LanguageServices;

public sealed record ClassifiedTextRun(Utf8String Classification, Utf8String Text);
public sealed record HoverDisplay(int ImageId, IReadOnlyList<ClassifiedTextRun> Runs, Utf8String Documentation);

internal sealed class DisplayParts(bool classified)
{
    private readonly Utf8StringBuilder text = new();
    internal List<ClassifiedTextRun> Runs { get; } = [];
    internal int Length => text.Length;
    internal Utf8String ToUtf8String() => text.ToUtf8String();
    internal DisplayParts Append(Utf8String value) => Write("text"u8, value);
    internal DisplayParts Append(ReadOnlySpan<byte> value) => Append((Utf8String)value);
    internal DisplayParts Keyword(Utf8String value) => Write("keyword"u8, value);
    internal DisplayParts Punctuation(Utf8String value) => Write("punctuation"u8, value);
    internal DisplayParts Operator(Utf8String value) => Write("operator"u8, value);
    internal DisplayParts Literal(Utf8String value) => Write("string"u8, value);
    internal DisplayParts Label(Utf8String value) => Punctuation("("u8).Append(value).Punctuation(") "u8);
    internal DisplayParts Symbol(Utf8String value, Symbol symbol) => Write(Classify(symbol), value);
    internal DisplayParts Write(Utf8String classification, Utf8String value)
    {
        if (value.IsEmpty) return this;
        text.Append(value);
        if (classified) Runs.Add(new(classification, value));
        return this;
    }
    internal void Copy(IReadOnlyList<ClassifiedTextRun> runs)
    { foreach (var run in runs) Write(run.Classification, run.Text); }
    internal static Utf8String Classify(Symbol? symbol)
    {
        if (symbol is null) return "text"u8;
        var flags = symbol.Flags;
        if ((flags & SymbolFlags.Variable) != 0) return symbol.Declarations.FirstOrDefault() is ParameterDeclarationNode ? "parameter name"u8 : "local name"u8;
        if ((flags & (SymbolFlags.Property | SymbolFlags.Accessor)) != 0) return "property name"u8;
        if ((flags & SymbolFlags.EnumMember) != 0) return "field name"u8;
        if ((flags & SymbolFlags.Function) != 0) return "method name"u8;
        if ((flags & SymbolFlags.Class) != 0) return "class name"u8;
        if ((flags & SymbolFlags.Interface) != 0) return "interface name"u8;
        if ((flags & SymbolFlags.Enum) != 0) return "enum name"u8;
        if ((flags & SymbolFlags.Module) != 0) return "module name"u8;
        if ((flags & SymbolFlags.Method) != 0) return "method name"u8;
        if ((flags & SymbolFlags.TypeParameter) != 0) return "type parameter name"u8;
        return (flags & (SymbolFlags.TypeAlias | SymbolFlags.Alias)) != 0 ? "identifier"u8 : "text"u8;
    }
}
