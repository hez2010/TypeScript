using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed class HoverVerbosity(int level, int maximumLength)
{
    internal int Level { get; } = level;
    internal int MaximumLength { get; } = maximumLength;
    internal bool CanIncrease { get; set; }
    internal bool Truncated { get; set; }
    internal void Include(HoverVerbosity other) { CanIncrease |= other.CanIncrease; Truncated |= other.Truncated; }
}

internal sealed partial class Checker
{
    internal async ValueTask<Utf8String> GetHoverTypeDisplayAsync(Type type, SyntaxNode? enclosing, TypeFormatFlags flags,
        HoverVerbosity verbosity, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(enclosing, cancellation);
        context.RequireOwned(type);
        bool noTruncation = ((verbosity.MaximumLength == 0 && program.Symbols.Program.Configuration.Options.NoErrorTruncation == true)
            || (flags & TypeFormatFlags.NoTruncation) != 0);
        var builderFlags = (NodeBuilderFlags)(flags & TypeFormatFlags.NodeBuilderFlagsMask) | (noTruncation ? NodeBuilderFlags.NoTruncation : 0);
        var text = await DiagnosticSyntaxAsync(enclosing, builderFlags, false,
            state => TypeSyntaxAsync(type, state, cancellation, (flags & TypeFormatFlags.InTypeAlias) != 0), cancellation, verbosity);
        long maximum = noTruncation ? 2 * TypeDisplay.NoTruncationMaximumTruncationLength
            : verbosity.MaximumLength > 0 ? (long)verbosity.MaximumLength * 10 : 2 * TypeDisplay.DefaultMaximumTruncationLength;
        if (text.Length < maximum) return text;
        verbosity.Truncated = true;
        return text[..((int)maximum - 3)] + "..."u8;
    }

    internal async ValueTask<Utf8String> GetHoverSignatureDisplayAsync(Signature signature, SyntaxNode? enclosing, TypeFormatFlags flags,
        HoverVerbosity verbosity, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(enclosing, cancellation);
        bool construct = (signature.Flags & SignatureFlags.Construct) != 0 && (flags & TypeFormatFlags.WriteCallStyleSignature) == 0;
        var kind = (flags & TypeFormatFlags.WriteArrowStyleSignature) != 0
            ? construct ? K.ConstructorType : K.FunctionType : construct ? K.ConstructSignature : K.CallSignature;
        return await DiagnosticSyntaxAsync(enclosing, (NodeBuilderFlags)(flags & TypeFormatFlags.NodeBuilderFlagsMask)
            | NodeBuilderFlags.WriteTypeParametersInQualifiedName, true, state => SignatureSyntaxAsync(signature, kind, state, cancellation), cancellation, verbosity);
    }

    internal async ValueTask<Utf8String> GetHoverTypeParameterDisplayAsync(TypeParameter parameter, SyntaxNode? enclosing,
        HoverVerbosity verbosity, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(enclosing, cancellation);
        return await DiagnosticSyntaxAsync(enclosing, NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope | NodeBuilderFlags.MultilineObjectLiterals,
            false, async state => await TypeParameterSyntaxAsync(parameter,
                await Instantiation.Constraints.ConstraintAsync(parameter, cancellation) is { } constraint
                    ? await ConstraintSyntaxAsync(parameter, constraint, state, cancellation) : null, state, cancellation), cancellation, verbosity);
    }

    internal bool IsLibrarySymbolForHover(Symbol? symbol) => symbol is not null && symbol.Declarations.Any(declaration =>
        SemanticSyntax.Source(declaration) is { } file && program.Symbols.Program.GetFile(file.FileName)?.Library == true);
    internal bool IsLibraryTypeForHover(Type type) => type is TupleType or TypeReference { Target: TupleType }
        || IsLibrarySymbolForHover(type is TypeReference reference && (type.ObjectFlags & ObjectFlags.Reference) != 0 ? reference.Target?.Symbol : type.Symbol);

    private bool ShouldExpandHoverType(Type type, bool alias, TypeSyntaxContext state)
    {
        if (state.Verbosity is not { Level: >= 0 } verbosity) return false;
        if (alias ? IsLibrarySymbolForHover(type.Alias?.Symbol) : IsLibraryTypeForHover(type)) return false;
        if (!alias && (type.Flags & TypeFlags.EnumLike) == 0 && (type.ObjectFlags & (ObjectFlags.Reference | ObjectFlags.ClassOrInterface)) == 0
            && !((type.ObjectFlags & ObjectFlags.Anonymous) != 0 && type.Symbol is { } symbol
                && (symbol.Flags & (SymbolFlags.Class | SymbolFlags.Enum | SymbolFlags.ValueModule | SymbolFlags.Function | SymbolFlags.Method)) != 0)) return false;
        for (int i = 0; i < state.TypeStack.Count - 1; i++) if (state.TypeStack[i] == type) return false;
        if (state.ExpansionDepth < verbosity.Level) return true;
        verbosity.CanIncrease = true;
        return false;
    }

    private async ValueTask ProbeHoverTypeAsync(Type type, TypeSyntaxContext state, CancellationToken cancellation)
    {
        if (state.Verbosity is not { Level: >= 0, CanIncrease: false }) return;
        if (state.TypeStack.Contains(type)) return;
        state.TypeStack.Add(type);
        try
        {
            if (type.Alias is not null) ShouldExpandHoverType(type, true, state);
            if (!state.Verbosity.CanIncrease) ShouldExpandHoverType(type, false, state);
            if (!state.Verbosity.CanIncrease && type is TypeReference reference && (type.ObjectFlags & ObjectFlags.Reference) != 0)
                foreach (var argument in await References.TypeArgumentsAsync(reference, cancellation))
                { await ProbeHoverTypeAsync(argument, state, cancellation); if (state.Verbosity.CanIncrease) break; }
        }
        finally { state.TypeStack.RemoveAt(state.TypeStack.Count - 1); }
    }

    private async ValueTask ProbeHoverSyntaxAsync(SyntaxNode node, TypeSyntaxContext state, CancellationToken cancellation)
    {
        if (state.Verbosity is not { Level: >= 0, CanIncrease: false }) return;
        foreach (var child in node.DescendantsAndSelf())
        {
            if (child is TypeReferenceNode or ExpressionWithTypeArgumentsNode or TypePredicateNode or ImportTypeNode)
            {
                var type = await Nodes.FromNodeAsync(child, cancellation);
                type = (await Instantiation.Engine.InstantiateAsync(type, state.Mapper, cancellation: cancellation))!;
                await ProbeHoverTypeAsync(type, state, cancellation);
                if (state.Verbosity.CanIncrease) break;
            }
        }
    }
}
