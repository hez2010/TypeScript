using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.LanguageServices;

internal enum ScriptElementKind
{
    Unknown, Warning, Keyword, Script, Module, Class, LocalClass, Interface, Type, Enum, EnumMember,
    Variable, LocalVariable, Using, AwaitUsing, Function, LocalFunction, Method, Getter, Setter, Property, AccessorProperty,
    Constructor, CallSignature, IndexSignature, ConstructSignature, Parameter, TypeParameter, PrimitiveType, Label, Alias,
    Const, Let, Directory, ExternalModule, String, Link, LinkName, LinkText,
}

internal static class SymbolClassification
{
    internal static async ValueTask<ScriptElementKind> KindAsync(Checker? checker, Symbol symbol, SyntaxNode? location, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var roots = checker is null ? (IReadOnlyList<Symbol>)[symbol] : await checker.GetRootSymbolsAsync(symbol, cancellation);
        if (roots.Count == 1 && (roots[0].Flags & S.Method) != 0 && (checker is null || (await checker.SignaturesAsync(
            await checker.NonNullableAsync(await checker.GetTypeOfSymbolAtLocationAsync(symbol, location!, cancellation), cancellation), false, cancellation)).Count > 0)) return ScriptElementKind.Method;
        if (checker is not null)
        {
            if (checker.IsUndefinedSymbol(symbol)) return ScriptElementKind.Variable;
            if (checker.IsArgumentsSymbol(symbol)) return ScriptElementKind.LocalVariable;
            if (location?.Kind == K.ThisKeyword) return ScriptElementKind.Parameter;
        }
        var flags = symbol.CombinedFlags;
        if ((flags & S.Variable) != 0)
        {
            var declaration = symbol.Declarations.FirstOrDefault();
            while (declaration is BindingElementNode or BindingPatternNode) declaration = declaration.Parent;
            if (declaration is ParameterDeclarationNode) return ScriptElementKind.Parameter;
            var value = symbol.ValueDeclaration is { } node ? SemanticSyntax.RootDeclaration(node) : null;
            var variableFlags = value?.Parent is VariableDeclarationListNode list ? list.Flags : NodeFlags.None;
            if ((variableFlags & NodeFlags.BlockScoped) == NodeFlags.Const) return ScriptElementKind.Const;
            if ((variableFlags & NodeFlags.BlockScoped) == NodeFlags.Using) return ScriptElementKind.Using;
            if ((variableFlags & NodeFlags.BlockScoped) == NodeFlags.AwaitUsing) return ScriptElementKind.AwaitUsing;
            if (symbol.Declarations.Any(node => SemanticSyntax.RootDeclaration(node).Parent is VariableDeclarationListNode list && (list.Flags & NodeFlags.Let) != 0)) return ScriptElementKind.Let;
            return Local(symbol) ? ScriptElementKind.LocalVariable : ScriptElementKind.Variable;
        }
        if ((flags & S.Function) != 0) return Local(symbol) ? ScriptElementKind.LocalFunction : ScriptElementKind.Function;
        if ((flags & S.GetAccessor) != 0) return ScriptElementKind.Getter;
        if ((flags & S.SetAccessor) != 0) return ScriptElementKind.Setter;
        if ((flags & S.Method) != 0) return ScriptElementKind.Method;
        if ((flags & S.Constructor) != 0) return ScriptElementKind.Constructor;
        if ((flags & S.Signature) != 0) return ScriptElementKind.IndexSignature;
        if ((flags & S.Property) != 0)
        {
            if (checker is not null && (flags & S.Transient) != 0 && (symbol.CheckFlags & CheckFlags.Synthetic) != 0
                && roots.All(root => (root.Flags & (S.Property | S.Accessor | S.Variable)) == 0)
                && (await checker.SignaturesAsync(await checker.GetTypeOfSymbolAtLocationAsync(symbol, location!, cancellation), false, cancellation)).Count > 0) return ScriptElementKind.Method;
            return ScriptElementKind.Property;
        }
        if ((flags & S.Class) != 0) return symbol.Declarations.Any(node => node is ClassExpressionNode) ? ScriptElementKind.LocalClass : ScriptElementKind.Class;
        if ((flags & S.Enum) != 0) return ScriptElementKind.Enum;
        if ((flags & S.TypeAlias) != 0) return ScriptElementKind.Type;
        if ((flags & S.Interface) != 0) return ScriptElementKind.Interface;
        if ((flags & S.TypeParameter) != 0) return ScriptElementKind.TypeParameter;
        if ((flags & S.EnumMember) != 0) return ScriptElementKind.EnumMember;
        if ((flags & S.Alias) != 0) return ScriptElementKind.Alias;
        if ((flags & S.Module) != 0) return ScriptElementKind.Module;
        return ScriptElementKind.Unknown;
    }

    private static bool Local(Symbol symbol)
    {
        if (symbol.Parent is not null) return false;
        foreach (var declaration in symbol.Declarations)
        {
            if (declaration is FunctionExpressionNode) return true;
            if (declaration is not (VariableDeclarationNode or FunctionDeclarationNode)) continue;
            for (var parent = declaration.Parent; parent is not null && parent is not (SourceFileNode or ModuleBlockNode); parent = parent.Parent)
                if (parent is BlockNode && parent.Parent is { } function && SemanticSyntax.FunctionDeclarationLike(function)) return true;
        }
        return false;
    }

    internal static int CompletionKind(ScriptElementKind kind) => kind switch
    {
        ScriptElementKind.PrimitiveType or ScriptElementKind.Keyword => 14,
        ScriptElementKind.Const or ScriptElementKind.Let or ScriptElementKind.Variable or ScriptElementKind.LocalVariable or ScriptElementKind.Alias or ScriptElementKind.Parameter => 6,
        ScriptElementKind.Property or ScriptElementKind.Getter or ScriptElementKind.Setter => 5,
        ScriptElementKind.Function or ScriptElementKind.LocalFunction => 3,
        ScriptElementKind.Method or ScriptElementKind.ConstructSignature or ScriptElementKind.CallSignature or ScriptElementKind.IndexSignature => 2,
        ScriptElementKind.Enum => 13, ScriptElementKind.EnumMember => 20,
        ScriptElementKind.Module or ScriptElementKind.ExternalModule => 9,
        ScriptElementKind.Class or ScriptElementKind.Type => 7, ScriptElementKind.Interface => 8,
        ScriptElementKind.Warning => 1, ScriptElementKind.Script => 17, ScriptElementKind.Directory => 19, ScriptElementKind.String => 21,
        _ => 10,
    };
}
