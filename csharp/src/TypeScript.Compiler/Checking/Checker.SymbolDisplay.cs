using TypeScript.Compiler.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<Utf8String> FullyQualifiedNameAsync(Symbol symbol, SyntaxNode? location, CancellationToken cancellation)
    {
        var names = new Stack<Utf8String>();
        while (symbol.Parent is { } parent)
        {
            cancellation.ThrowIfCancellationRequested();
            names.Push(await SymbolDisplayNameAsync(symbol, null, SymbolFlags.All, cancellation));
            symbol = parent;
        }
        names.Push(await SymbolDisplayNameAsync(symbol, location, SymbolFlags.All, cancellation,
            SymbolFormatFlags.DoNotIncludeSymbolChain | SymbolFormatFlags.AllowAnyNodeKind));
        return Utf8String.Join((byte)'.', names);
    }

    internal ValueTask<Utf8String> GetSymbolDisplayNameAsync(Symbol symbol, SyntaxNode? enclosing = null,
        SymbolFlags meaning = SymbolFlags.All, CancellationToken cancellation = default) =>
        GetSymbolDisplayNameAsync(symbol, enclosing, meaning, SymbolFormatFlags.AllowAnyNodeKind, cancellation);

    internal ValueTask<Utf8String> GetSymbolDisplayNameAsync(Symbol symbol, SyntaxNode? enclosing, SymbolFlags meaning,
        SymbolFormatFlags flags, CancellationToken cancellation = default) =>
        VisibilityQueryAsync(enclosing, () => ChainOperationAsync(() => ContainerOperationAsync(
            () => SymbolDisplayNameAsync(symbol, enclosing, meaning, cancellation, flags), cancellation), cancellation), cancellation);

    private sealed class SymbolDisplayContext(SyntaxNode? enclosing, SymbolFormatFlags flags)
    {
        internal SyntaxNode? Enclosing { get; set; } = enclosing;
        internal SymbolFormatFlags Flags { get; } = flags;
        internal TypeSyntaxLength? Length { get; init; }
        internal bool ExpressionNames { get; init; }
        internal TokenFlags StringLiteralFlags { get; init; }
        internal bool FullyQualified { get; set; }
        internal bool ForbidIndexedAccess { get; init; }
        internal bool InstantiationExpressions { get; init; }
        internal TypeSyntaxContext? Types { get; init; }
        internal HashSet<(Symbol, SymbolFlags)> Parents { get; } = [];
        internal Dictionary<(Symbol, ReferenceResolutionMode), Utf8String> Modules { get; } = [];
    }

    private async ValueTask<Utf8String> SymbolDisplayNameAsync(Symbol symbol, SyntaxNode? enclosing, SymbolFlags meaning,
        CancellationToken cancellation, SymbolFormatFlags flags = SymbolFormatFlags.AllowAnyNodeKind, bool? neverAsciiEscape = null)
    {
        bool ascii = !(neverAsciiEscape ?? enclosing is SourceFileNode);
        if ((flags & (SymbolFormatFlags.AllowAnyNodeKind | SymbolFormatFlags.WriteComputedProps))
            == (SymbolFormatFlags.AllowAnyNodeKind | SymbolFormatFlags.WriteComputedProps))
        {
            if (DisplayDeclarationName(symbol.ValueDeclaration!) is ComputedPropertyNameNode computed)
                return PrintDiagnosticNode(computed, !ascii, cancellation, enclosing is null ? null : SemanticSyntax.Source(enclosing));
            if (links.Values.TryGet(symbol)?.NameType is { Symbol: { } nameSymbol } nameType
                && (nameType.Flags & (TypeFlags.EnumLiteral | TypeFlags.UniqueESSymbol)) != 0)
            {
                var name = await SymbolDisplayNameAsync(nameSymbol, nameSymbol.ValueDeclaration, meaning, cancellation,
                    flags & ~SymbolFormatFlags.WriteComputedProps, !ascii);
                return Utf8String.Concat("["u8, name, "]"u8);
            }
        }
        var state = new SymbolDisplayContext(enclosing, flags);
        List<Symbol> chain = enclosing is null
            || (symbol.Flags & SymbolFlags.TypeParameter) != 0
            || (flags & SymbolFormatFlags.DoNotIncludeSymbolChain) != 0
            ? [symbol] : (await DisplaySymbolChainAsync(symbol, meaning, true, state, cancellation))!;
        var arguments = new Dictionary<int, Utf8String>();
        if ((flags & (SymbolFormatFlags.AllowAnyNodeKind | SymbolFormatFlags.WriteTypeParametersOrArguments))
            == (SymbolFormatFlags.AllowAnyNodeKind | SymbolFormatFlags.WriteTypeParametersOrArguments))
        {
            var types = new TypeSyntaxContext(enclosing, (flags & SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope) != 0,
                (flags & SymbolFormatFlags.UseOnlyExternalAliasing) != 0, NodeBuilderFlags.IgnoreErrors);
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (i < chain.Count - 1)
                {
                    var nodes = await SymbolDisplayArgumentsAsync(chain[i], chain[i + 1], types, cancellation);
                    if (nodes.Count != 0)
                        arguments[i] = Utf8String.Concat("<"u8, Utf8String.Join(", "u8, nodes.Select(n => PrintDiagnosticNode(n, !ascii, cancellation,
                            enclosing is null ? null : SemanticSyntax.Source(enclosing), types.NoAsciiEscape, types.SingleLine))), ">"u8);
                }
                AddExpressionNameLength(types.Length, DisplayNameAsWritten(chain[i], state, i == 0, cancellation), i == 0,
                    (chain[i].Flags & SymbolFlags.EnumMember) != 0);
            }
        }
        var output = new Utf8StringBuilder();
        for (int i = 0; i < chain.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var part = chain[i];
            Utf8String name = DisplayNameAsWritten(part, state, i == 0, cancellation);
            if ((flags & SymbolFormatFlags.AllowAnyNodeKind) == 0)
            {
                if (i != 0)
                    output.Append((byte)'.');
                output.Append(name.Span);
            }
            else if (Quoted(name) && part.Declarations.Any(NonGlobalExternalModule))
            {
                output.Clear();
                output.Append(
                    QuoteSymbolText(await DisplayModuleSpecifierAsync(part, state, cancellation), (byte)'"', ascii).Span);
            }
            else if (i == 0 || IdentifierName(name.Span.StartsWith((byte)'#') ? name[1..] : name))
            {
                if (i != 0)
                    output.Append((byte)'.');
                output.Append(name.Span);
            }
            else
            {
                if (name.Span.StartsWith((byte)'['))
                    name = name[1..^1];
                if (Quoted(name) && (part.Flags & SymbolFlags.EnumMember) == 0)
                    name = QuoteSymbolText(UnquoteSymbolText(name), name[0], ascii);
                output.Append((byte)'[').Append(name.Span).Append((byte)']');
            }
            if (arguments.TryGetValue(i, out var typeArguments))
                output.Append(typeArguments.Span);
        }
        return Utf8String.FromBuilder(output);
    }

    private async ValueTask<List<Symbol>?> DisplaySymbolChainAsync(Symbol symbol, SymbolFlags meaning, bool last,
        SymbolDisplayContext state, CancellationToken cancellation, bool yieldModule = false)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (!state.Parents.Add((symbol, meaning)))
            throw new InvalidOperationException("Cyclic symbol qualification");
        try
        {
            var accessible = await AccessibleChainAsync(new(symbol, state.Enclosing, meaning,
                (state.Flags & SymbolFormatFlags.UseOnlyExternalAliasing) != 0, []), cancellation);
            if (accessible is null || await NeedsQualificationAsync(accessible[0], state.Enclosing,
                accessible.Count > 1 ? QualifiedLeftMeaning(meaning) : meaning, cancellation))
            {
                var parents = await ContainersOfSymbolAsync(
                    accessible is { Count: > 0 } ? accessible[0] : symbol,
                    state.Enclosing,
                    meaning,
                    cancellation);
                var named = new List<(Symbol Symbol, Utf8String Name, int Order)>();
                for (int i = 0; i < parents.Count; i++)
                    named.Add((parents[i], parents[i].Declarations.Any(NonGlobalExternalModule)
                        ? await DisplayModuleSpecifierAsync(parents[i], state, cancellation) : Utf8String.Empty, i));
                named.Sort((a, b) =>
                {
                    int result;
                    if (a.Name.Length != 0 && b.Name.Length != 0)
                    {
                        bool aRelative = DisplayRelative(a.Name), bRelative = DisplayRelative(b.Name);
                        result = aRelative == bRelative
                            ? DisplayPathComponents(a.Name).CompareTo(DisplayPathComponents(b.Name))
                            : aRelative ? 1 : -1;
                    }
                    else
                        result = Algebra.Order.CompareSymbols(a.Symbol, b.Symbol);
                    return result != 0 ? result : a.Order.CompareTo(b.Order);
                });
                foreach (var (parent, _, _) in named)
                {
                    var parentChain = await DisplaySymbolChainAsync(
                        parent,
                        QualifiedLeftMeaning(meaning),
                        false,
                        state,
                        cancellation,
                        yieldModule);
                    if (parentChain is null)
                        continue;
                    if (parent.Exports.GetValueOrDefault(Utf8Literals.ExportEquals) is { } exported
                        && await SameSymbolReferenceAsync(exported, symbol, cancellation))
                        return parentChain;
                    if (accessible is { Count: > 0 })
                        parentChain.AddRange(accessible);
                    else
                        parentChain.Add(await AliasInContainerAsync(parent, symbol, cancellation) ?? symbol);
                    return parentChain;
                }
            }
            if (accessible is { Count: > 0 })
                return [.. accessible];
            return last
                || (symbol.Flags & (SymbolFlags.TypeLiteral | SymbolFlags.ObjectLiteral)) == 0
                    && (yieldModule || !symbol.Declarations.Any(NonGlobalExternalModule))
                ? [symbol] : null;
        }
        finally
        {
            state.Parents.Remove((symbol, meaning));
        }
    }

    private Utf8String DisplayNameAsWritten(Symbol symbol, SymbolDisplayContext state, bool first, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (symbol.Name == Utf8Literals.Default && (state.Flags & SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope) == 0
            && (!first || symbol.Declarations.Length == 0 || state.Enclosing is not null
            && DefaultBindingContext(symbol.Declarations[0]) != DefaultBindingContext(state.Enclosing)))
            return Utf8Literals.Default;
        if (symbol.Declarations.Length != 0)
        {
            var name = symbol.Declarations.Select(DisplayDeclarationName).FirstOrDefault(n => n is not null);
            if (name is not null)
            {
                if (name is ComputedPropertyNameNode && (symbol.CheckFlags & Binding.CheckFlags.Late) == 0
                    && links.Values.TryGet(symbol)?.NameType is LiteralType { Flags: var flags }
                    && (flags & TypeFlags.StringOrNumberLiteral) != 0 && DisplayNameFromType(
                        symbol,
                        state,
                        first,
                        cancellation) is { Length: > 0 } literal)
                    return literal;
                return CheckerDiagnostic.DeclarationName(name);
            }
            var declaration = symbol.Declarations[0];
            if (declaration.Parent is VariableDeclarationNode { Name: { } variable })
                return CheckerDiagnostic.DeclarationName(variable);
            if (declaration is ClassExpressionNode or FunctionExpressionNode or ArrowFunctionNode
                && state.Types is { } types && (types.Flags & NodeBuilderFlags.AllowAnonymousIdentifier) == 0)
                types.EncounteredError = true;
            if (declaration is ClassExpressionNode)
                return Utf8Literals.AnonymousClass;
            if (declaration is FunctionExpressionNode or ArrowFunctionNode)
                return Utf8Literals.AnonymousFunction;
        }
        Utf8String fromType = DisplayNameFromType(symbol, state, first, cancellation);
        return fromType.Length != 0 ? fromType : Symbol.EscapeName(symbol.Name);
    }

    private Utf8String DisplayNameFromType(Symbol symbol, SymbolDisplayContext state, bool first, CancellationToken cancellation)
    {
        var nameType = links.Values.TryGet(symbol)?.NameType;
        if (nameType is LiteralType { Value: Utf8String or double } literal)
        {
            Utf8String name = literal.Value is Utf8String text ? text : TokenFacts.NumberText((double)literal.Value!);
            bool numeric = TokenFacts.NumberText(JsNumber.FromString(name)) == name;
            return !IdentifierName(name) && !numeric ? QuoteSymbolText(name, '"', false)
                : numeric && name.Span.StartsWith((byte)'-') ? Utf8String.Concat("["u8, name, "]"u8) : name;
        }
        return nameType is UniqueSymbolType unique ? Utf8String.Concat("["u8, DisplayNameAsWritten(unique.Symbol!, state, first, cancellation), "]"u8) : Utf8String.Empty;
    }

    private static SyntaxNode? DisplayDeclarationName(SyntaxNode? declaration)
    {
        if (declaration is null)
            return null;
        if (declaration is ExportAssignmentNode { Expression: IdentifierNode exported })
            return exported;
        bool js = (declaration.Flags & NodeFlags.JavaScriptFile) != 0;
        if (declaration is BinaryExpressionNode
            {
                OperatorToken.Kind: SyntaxKind.EqualsToken, Left: PropertyAccessExpressionNode
            or ElementAccessExpressionNode
            } binary)
        {
            if (js && ModuleExportsAccess(binary.Left) && !(binary.Right is IdentifierNode { Text.Span: var matchedText } && matchedText.SequenceEqual("exports"u8)))
                return null;
            var receiver = FlowReferences.Receiver(binary.Left!);
            if (js && receiver?.Kind == SyntaxKind.ThisKeyword || DisplayEntityName(receiver, js)
                && (binary.Left is ElementAccessExpressionNode || binary.Left is PropertyAccessExpressionNode { Name: IdentifierNode }))
                return DisplayAccessName(binary.Left!) ?? binary.Left;
            return null;
        }
        if (js && declaration is CallExpressionNode { Expression: PropertyAccessExpressionNode { Expression: IdentifierNode { Text.Span: var matchedText2 }, Name: IdentifierNode { Text.Span: var matchedText3 } }, Arguments: { Count: 3 } arguments } && matchedText2.SequenceEqual("Object"u8) && matchedText3.SequenceEqual("defineProperty"u8) && arguments[1] is StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode
            && DisplayEntityName(arguments[0], true, false))
            return arguments[1];
        if (SemanticSyntax.Name(declaration) is { } name)
            return name;
        if (declaration is not (ClassExpressionNode or FunctionExpressionNode or ArrowFunctionNode))
            return null;
        return declaration.Parent switch
        {
            PropertyAssignmentNode property => property.Name,
            BindingElementNode binding => binding.Name,
            VariableDeclarationNode { Name: IdentifierNode variable } => variable,
            BinaryExpressionNode assignment when assignment.Right == declaration => assignment.Left is IdentifierNode ? assignment.Left
                : assignment.Left is PropertyAccessExpressionNode access ? access.Name : DisplayAccessName(assignment.Left!),
            _ => null
        };
    }

    private static SyntaxNode? DisplayAccessName(SyntaxNode node)
    {
        if (node is PropertyAccessExpressionNode property)
            return property.Name is IdentifierNode ? property.Name : null;
        if (node is ElementAccessExpressionNode element)
        {
            var argument = element.ArgumentExpression;
            while (argument is ParenthesizedExpressionNode parenthesized)
                argument = parenthesized.Expression;
            return argument is StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode ? argument : null;
        }
        return null;
    }

    private static bool DisplayEntityName(SyntaxNode? node, bool js, bool allowThis = true)
    {
        while (node is PropertyAccessExpressionNode { Name: IdentifierNode } || js && node is ElementAccessExpressionNode
            { ArgumentExpression: StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode })
            node = FlowReferences.Receiver(node);
        return node is IdentifierNode || js && allowThis && node?.Kind == SyntaxKind.ThisKeyword;
    }

    private static bool IdentifierName(Utf8String name)
    {
        int index = 0;
        foreach (var rune in name.Span.EnumerateRunes())
            if (!(index++ == 0 ? TokenFacts.IsIdentifierStart(rune.Value) : TokenFacts.IsIdentifierPart(rune.Value)))
                return false;
        return index != 0;
    }

    private static bool Quoted(Utf8String text) => text.Span.StartsWith((byte)'"') || text.Span.StartsWith((byte)'\'');

    private static bool DisplayRelative(Utf8String path) =>
        path.Span.SequenceEqual("."u8) || path.Span.SequenceEqual(".."u8) || path.Span.StartsWith("./"u8, StringComparison.Ordinal) || path.Span.StartsWith("../"u8, StringComparison.Ordinal);

    private static int DisplayPathComponents(Utf8String path) =>
        path.Span.Slice(path.Span.StartsWith("./"u8, StringComparison.Ordinal) ? 2 : 0).Count((byte)'/');

    private static SyntaxNode? DefaultBindingContext(SyntaxNode node) =>
        DeclarationOrder.Ancestor(node, n => n is SourceFileNode || AmbientModule(n));

    private static Utf8String UnquoteSymbolText(Utf8String text)
    {
        if (text.Length > 1 && text[0] == text[^1])
            text = text[1..^1];
        var result = new Utf8StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] != '\n')
                i++;
            result.Append(text[i]);
        }
        return Utf8String.FromBuilder(result);
    }

    internal static Utf8String QuoteSymbolText(Utf8String text, int quote, bool ascii)
    {
        var result = new Utf8StringBuilder().AppendCodePoint(quote);
        Span<byte> escape = stackalloc byte[4];
        for (int i = 0; i < text.Length; i++)
        {
            int c = Wtf8.Decode(text.Span[i..], out int width);
            i += width - 1;
            if (ascii && c > 0xFFFF)
            {
                Escape(0xD800 + (c - 0x10000 >> 10));
                Escape(0xDC00 + (c - 0x10000 & 0x3FF));
                continue;
            }
            if (quote == '`' && c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                result.Append("\\r\\n"u8);
                i++;
                continue;
            }
            if (quote == '`' && c == '$' && i + 1 < text.Length && text[i + 1] == '{')
                result.Append("\\$"u8);
            else if (c == quote)
                result.Append((byte)'\\').AppendCodePoint(c);
            else
                switch (c)
                {
                    case '\0':
                        result.Append(i + 1 < text.Length && Utf8Ascii.IsDigit(text[i + 1]) ? "\\x00"u8 : "\\0"u8);
                        break;
                    case '\\':
                        result.Append("\\\\"u8);
                        break;
                    case '\b':
                        result.Append("\\b"u8);
                        break;
                    case '\t':
                        result.Append("\\t"u8);
                        break;
                    case '\n':
                        result.Append(quote == (byte)'`' ? "\n"u8 : "\\n"u8);
                        break;
                    case '\v':
                        result.Append("\\v"u8);
                        break;
                    case '\f':
                        result.Append("\\f"u8);
                        break;
                    case '\r':
                        result.Append("\\r"u8);
                        break;
                    case < ' ' or '\u0085' or '\u2028' or '\u2029' or >= '\ud800' and <= '\udfff':
                    case > '\x7f' when ascii:
                        result.Append("\\u"u8);
                        ((int)c).TryFormat(escape, out _, "X4", CultureInfo.InvariantCulture);
                        result.Append(escape);
                        break;
                    default:
                        result.AppendCodePoint(c);
                        break;
                }
        }
        return result.AppendCodePoint(quote).ToUtf8String();

        void Escape(int value)
        {
            Span<byte> digits = stackalloc byte[4];
            value.TryFormat(digits, out _, "X4", CultureInfo.InvariantCulture);
            result.Append("\\u"u8).Append(digits);
        }
    }
}
