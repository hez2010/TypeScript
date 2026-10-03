using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private static async ValueTask<int> HoverImageAsync(Checker checker, Symbol? symbol, SyntaxNode location, CancellationToken cancellation)
    {
        if (symbol is null) return 0x635;
        var original = symbol;
        if ((symbol.Flags & SymbolFlags.Alias) != 0) symbol = await checker.GetAliasedSymbolAsync(symbol, cancellation);
        bool Modified(SyntaxKind kind) => new[] { original, symbol }.Any(value => value.Declarations.FirstOrDefault() is { } declaration
            && SemanticSyntax.HasModifier(declaration, kind));
        int Pick(int privateId, int protectedId, int publicId) => Modified(SyntaxKind.PrivateKeyword) ? privateId
            : Modified(SyntaxKind.ProtectedKeyword) ? protectedId : publicId;
        var roots = await checker.GetRootSymbolsAsync(symbol, cancellation);
        var flags = symbol.Flags | (symbol.ExportSymbol?.Flags ?? 0);
        if (roots is [var root] && (root.Flags & SymbolFlags.Method) != 0
            && (await checker.SignaturesAsync(checker.Algebra.Filter(await checker.GetTypeOfSymbolAtLocationAsync(symbol, location, cancellation),
                type => (type.Flags & TypeFlags.Nullable) == 0), false, cancellation)).Count > 0) return Pick(0x756, 0x757, 0x758);
        if (location.Kind == SyntaxKind.ThisKeyword && QuerySyntax.Expression(location) || SymbolDisplay.IsThisInTypeQuery(location)) return 0x6d3;
        if ((flags & SymbolFlags.Variable) != 0)
        {
            if (symbol.Declarations.FirstOrDefault() is { } declaration)
            {
                while (declaration is BindingElementNode or BindingPatternNode && declaration.Parent is not null) declaration = declaration.Parent;
                if (declaration is ParameterDeclarationNode) return 0x6d3;
            }
            var value = symbol.ValueDeclaration is { } owner ? SemanticSyntax.RootDeclaration(owner) : null;
            if (value is VariableDeclarationNode && (value.Parent!.Flags & NodeFlags.Const) != 0) return Pick(0x26a, 0x26b, 0x26c);
            return 0x6d3;
        }
        if ((flags & SymbolFlags.Function) != 0) return Pick(0x756, 0x757, 0x758);
        if ((flags & SymbolFlags.Accessor) != 0) return Pick(0x982, 0x983, 0x984);
        if ((flags & SymbolFlags.Method) != 0) return Pick(0x756, 0x757, 0x758);
        if ((flags & SymbolFlags.Constructor) != 0) return Pick(0x1d7, 0x1d8, 0x1d9);
        if ((flags & SymbolFlags.Signature) != 0) return Pick(0x756, 0x757, 0x758);
        if ((flags & SymbolFlags.Property) != 0)
        {
            if ((flags & SymbolFlags.Transient) != 0 && (symbol.CheckFlags & CheckFlags.Synthetic) != 0
                && roots.All(root => (root.Flags & (SymbolFlags.Property | SymbolFlags.Accessor | SymbolFlags.Variable)) == 0)
                && (await checker.SignaturesAsync(await checker.GetTypeOfSymbolAtLocationAsync(symbol, location, cancellation), false, cancellation)).Count != 0)
                return Pick(0x756, 0x757, 0x758);
            return Pick(0x982, 0x983, 0x984);
        }
        if ((flags & SymbolFlags.Class) != 0) return Pick(0x1d7, 0x1d8, 0x1d9);
        if ((flags & SymbolFlags.Enum) != 0) return Pick(0x469, 0x46a, 0x46b);
        if ((flags & SymbolFlags.TypeAlias) != 0) return Pick(0x1d7, 0x1d8, 0x1d9);
        if ((flags & SymbolFlags.Interface) != 0) return Pick(0x646, 0x647, 0x648);
        if ((flags & SymbolFlags.TypeParameter) != 0) return 0xca1;
        if ((flags & SymbolFlags.EnumMember) != 0) return 0x465;
        if ((flags & SymbolFlags.Alias) != 0) return 0x77f;
        return (flags & SymbolFlags.Module) != 0 ? 0x79f : 0xc4;
    }
}
