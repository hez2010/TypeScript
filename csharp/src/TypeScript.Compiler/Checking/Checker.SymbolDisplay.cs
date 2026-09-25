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
    internal ValueTask<string> GetSymbolDisplayNameAsync(Symbol symbol, SyntaxNode? enclosing = null,
        SymbolFlags meaning = SymbolFlags.All, CancellationToken cancellation = default) =>
        GetSymbolDisplayNameAsync(symbol, enclosing, meaning, SymbolFormatFlags.AllowAnyNodeKind, cancellation);

    internal async ValueTask<string> GetSymbolDisplayNameAsync(Symbol symbol, SyntaxNode? enclosing, SymbolFlags meaning,
        SymbolFormatFlags flags, CancellationToken cancellation = default)
    {
        if ((flags & SymbolFormatFlags.AllowAnyNodeKind) != 0
            && (flags & SymbolFormatFlags.WriteTypeParametersOrArguments) != 0)
            throw new NotSupportedException("Type-argument serialization requires the general type-node builder");
        using var query = await EnterQueryAsync(enclosing, cancellation).ConfigureAwait(false);
        return await ChainOperationAsync(() => ContainerOperationAsync(
            () => SymbolDisplayNameAsync(symbol, enclosing, meaning, cancellation, flags), cancellation), cancellation);
    }

    private sealed class SymbolDisplayContext(SyntaxNode? enclosing, SymbolFormatFlags flags)
    {
        internal SyntaxNode? Enclosing { get; set; } = enclosing;
        internal SymbolFormatFlags Flags { get; } = flags;
        internal HashSet<(Symbol, SymbolFlags)> Parents { get; } = [];
        internal Dictionary<(Symbol, ReferenceResolutionMode), string> Modules { get; } = [];
    }

    private async ValueTask<string> SymbolDisplayNameAsync(Symbol symbol, SyntaxNode? enclosing, SymbolFlags meaning,
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
                return "[" + await SymbolDisplayNameAsync(nameSymbol, nameSymbol.ValueDeclaration, meaning, cancellation,
                    flags & ~SymbolFormatFlags.WriteComputedProps, !ascii) + "]";
        }
        var state = new SymbolDisplayContext(enclosing, flags);
        List<Symbol> chain = enclosing is null
            || (symbol.Flags & SymbolFlags.TypeParameter) != 0
            || (flags & SymbolFormatFlags.DoNotIncludeSymbolChain) != 0
            ? [symbol] : (await DisplaySymbolChainAsync(symbol, meaning, true, state, cancellation))!;
        var output = new StringBuilder();
        for (int i = 0; i < chain.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var part = chain[i];
            string name = DisplayNameAsWritten(part, state, i == 0, cancellation);
            if ((flags & SymbolFormatFlags.AllowAnyNodeKind) == 0)
            {
                if (i != 0)
                    output.Append('.');
                output.Append(name);
            }
            else if (Quoted(name) && part.Declarations.Any(NonGlobalExternalModule))
            {
                output.Clear();
                output.Append(
                    QuoteSymbolText(await DisplayModuleSpecifierAsync(part, state, cancellation), '"', ascii));
            }
            else if (i == 0 || IdentifierName(name.StartsWith('#') ? name[1..] : name))
            {
                if (i != 0)
                    output.Append('.');
                output.Append(name);
            }
            else
            {
                if (name.StartsWith('['))
                    name = name[1..^1];
                if (Quoted(name) && (part.Flags & SymbolFlags.EnumMember) == 0)
                    name = QuoteSymbolText(UnquoteSymbolText(name), name[0], ascii);
                output.Append('[').Append(name).Append(']');
            }
        }
        return output.ToString();
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
                var named = new List<(Symbol Symbol, string Name, int Order)>();
                for (int i = 0; i < parents.Count; i++)
                    named.Add((parents[i], parents[i].Declarations.Any(NonGlobalExternalModule)
                        ? await DisplayModuleSpecifierAsync(parents[i], state, cancellation) : "", i));
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
                    if (parent.Exports.GetValueOrDefault("export=") is { } exported
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

    private string DisplayNameAsWritten(Symbol symbol, SymbolDisplayContext state, bool first, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (symbol.Name == "default" && (state.Flags & SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope) == 0
            && (!first || symbol.Declarations.Count == 0 || state.Enclosing is not null
            && DefaultBindingContext(symbol.Declarations[0]) != DefaultBindingContext(state.Enclosing)))
            return "default";
        if (symbol.Declarations.Count != 0)
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
            if (declaration is ClassExpressionNode)
                return "(Anonymous class)";
            if (declaration is FunctionExpressionNode or ArrowFunctionNode)
                return "(Anonymous function)";
        }
        string fromType = DisplayNameFromType(symbol, state, first, cancellation);
        return fromType.Length != 0 ? fromType : Symbol.EscapeName(symbol.Name);
    }

    private string DisplayNameFromType(Symbol symbol, SymbolDisplayContext state, bool first, CancellationToken cancellation)
    {
        var nameType = links.Values.TryGet(symbol)?.NameType;
        if (nameType is LiteralType { Value: string or double } literal)
        {
            string name = literal.Value is string text ? text : TokenFacts.NumberText((double)literal.Value!);
            bool numeric = TokenFacts.NumberText(JsNumber.FromString(name)) == name;
            return !IdentifierName(name) && !numeric ? QuoteSymbolText(name, '"', false)
                : numeric && name.StartsWith('-') ? "[" + name + "]" : name;
        }
        return nameType is UniqueSymbolType unique ? "[" + DisplayNameAsWritten(unique.Symbol!, state, first, cancellation) + "]" : "";
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
            if (js && ModuleExportsAccess(binary.Left) && binary.Right is not IdentifierNode { Text: "exports" })
                return null;
            var receiver = FlowReferences.Receiver(binary.Left!);
            if (js && receiver?.Kind == SyntaxKind.ThisKeyword || DisplayEntityName(receiver, js)
                && (binary.Left is ElementAccessExpressionNode || binary.Left is PropertyAccessExpressionNode { Name: IdentifierNode }))
                return DisplayAccessName(binary.Left!) ?? binary.Left;
            return null;
        }
        if (js && declaration is CallExpressionNode
            {
                Expression: PropertyAccessExpressionNode { Expression: IdentifierNode { Text: "Object" }, Name: IdentifierNode { Text: "defineProperty" } },
                Arguments: { Count: 3 } arguments
            } && arguments[1] is StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode
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

    private static bool IdentifierName(string name)
    {
        int index = 0;
        foreach (var rune in name.EnumerateRunes())
            if (!(index++ == 0 ? TokenFacts.IsIdentifierStart(rune.Value) : TokenFacts.IsIdentifierPart(rune.Value)))
                return false;
        return index != 0;
    }

    private static bool Quoted(string text) => text.StartsWith('"') || text.StartsWith('\'');

    private static bool DisplayRelative(string path) =>
        path is "." or ".." || path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("../", StringComparison.Ordinal);

    private static int DisplayPathComponents(string path) =>
        path.AsSpan(path.StartsWith("./", StringComparison.Ordinal) ? 2 : 0).Count('/');

    private static SyntaxNode? DefaultBindingContext(SyntaxNode node) =>
        DeclarationOrder.Ancestor(node, n => n is SourceFileNode || AmbientModule(n));

    private static string UnquoteSymbolText(string text)
    {
        if (text.Length > 1 && text[0] == text[^1])
            text = text[1..^1];
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] != '\n')
                i++;
            result.Append(text[i]);
        }
        return result.ToString();
    }

    private static string QuoteSymbolText(string text, char quote, bool ascii)
    {
        var result = new StringBuilder().Append(quote);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!ascii && char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result.Append(c).Append(text[++i]);
                continue;
            }
            if (quote == '`' && c == '$' && i + 1 < text.Length && text[i + 1] == '{')
                result.Append("\\$");
            else if (c == quote)
                result.Append('\\').Append(c);
            else
                result.Append(c switch
                {
                    '\0' => i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]) ? "\\x00" : "\\0",
                    '\\' => "\\\\",
                    '\b' => "\\b",
                    '\t' => "\\t",
                    '\n' => quote == '`' ? "\n" : "\\n",
                    '\v' => "\\v",
                    '\f' => "\\f",
                    '\r' => "\\r",
                    < ' ' or '\u0085' or '\u2028' or '\u2029' or >= '\ud800' and <= '\udfff' => "\\u" + ((int)c).ToString(
                        "X4",
                        CultureInfo.InvariantCulture),
                    > '\x7f' when ascii => "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture),
                    _ => c.ToString()
                });
        }
        return result.Append(quote).ToString();
    }
}
