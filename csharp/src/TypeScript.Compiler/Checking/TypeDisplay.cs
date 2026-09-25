using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Checking;

// Structural diagnostic displays. Declaration emit and accessibility-aware node
// building require the separate node-builder service.
internal sealed class TypeDisplay(
    TypeContext context,
    TypeAlgebra algebra,
    StructuredMembers members,
    TypeConstraints constraints,
    TypeReferences references,
    SymbolTypes values,
    Signatures signatures,
    SignatureParameters parameters,
    TypeNodes nodes,
    MappedTypes mapped,
    InferredConstraints inferredConstraints,
    TypeRelations relations,
    CheckerLinks links,
    bool noTruncation)
{
    private sealed class DisplayState
    {
        internal HashSet<Type> Active { get; } = [];
        internal IReadOnlyList<TypeParameter>? InferParameters { get; set; }
        internal List<Symbol> ReverseMapped { get; } = [];
        internal long WrittenBytes { get; set; }
    }

    private int serializationLevel;
    // The reference printer's absolute limits are twice its normal/no-truncation budgets.
    private int MaximumLength => noTruncation ? 2_000_000 : 320;

    internal ValueTask<string> GetSignatureAsync(Signature signature, CancellationToken cancellation = default)
    {
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
        return SignatureAsync(signature, (signature.Flags & SignatureFlags.Construct) != 0, false, new(), cancellation);
    }

    internal ValueTask<string> GetPredicateAsync(TypePredicate predicate, CancellationToken cancellation = default)
        => PredicateAsync(predicate, new(), cancellation);

    private async ValueTask<string> PredicateAsync(TypePredicate predicate, DisplayState active, CancellationToken cancellation)
    {
        string name = predicate.Kind is TypePredicateKind.This or TypePredicateKind.AssertsThis ? "this" : predicate.ParameterName;
        return (predicate.Kind is TypePredicateKind.AssertsThis or TypePredicateKind.AssertsIdentifier ? "asserts " : "") + name
            + (predicate.Type is null ? "" : " is " + await WriteAsync(predicate.Type, active, cancellation).ConfigureAwait(false));
    }

    internal async ValueTask<string> GetAsync(Type type, CancellationToken cancellation = default)
    {
        // Match the reference's guard for diagnostics raised during lazy type serialization.
        if (serializationLevel >= 2)
            return "?";
        serializationLevel++;
        try
        {
            string text = await WriteAsync(type, new(), cancellation).ConfigureAwait(false);
            return Encoding.UTF8.GetByteCount(text) >= MaximumLength ? Prefix(text, MaximumLength - 3) + "..." : text;
        }
        finally
        {
            serializationLevel--;
        }
    }

    private async ValueTask<string> WriteAsync(Type type, DisplayState active, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (active.WrittenBytes >= MaximumLength)
            return Elision;
        long before = active.WrittenBytes;
        string text = await WriteCoreAsync(type, active, cancellation).ConfigureAwait(false);
        int length = Encoding.UTF8.GetByteCount(text);
        // Child text is already counted. Count only the syntax added by this node.
        active.WrittenBytes += Math.Max(0, length - (active.WrittenBytes - before));
        return length > MaximumLength ? Prefix(text, MaximumLength) : text;
    }

    private static string Prefix(string text, int bytes) => Wtf8.DecodeString(Wtf8.Encode(text).AsSpan(0, bytes));

    private async ValueTask<string> WriteCoreAsync(Type type, DisplayState active, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type is IntrinsicType intrinsic)
            return intrinsic.IntrinsicName is "error" or "auto" or "wildcard" ? "any" : intrinsic.IntrinsicName;
        if ((type.Flags & TypeFlags.Boolean) != 0)
            return "boolean";
        if ((type.Flags & TypeFlags.EnumLike) != 0 && type.Symbol is { } enumSymbol)
            return (enumSymbol.Flags & SymbolFlags.EnumMember) != 0 ? enumSymbol.Parent!.Name + "." + enumSymbol.Name : enumSymbol.Name;
        if (type is LiteralType literal)
        {
            return literal.Value switch
            {
                string text => Quote(text),
                bool boolean => boolean ? "true" : "false",
                double number => TokenFacts.NumberText(number),
                BigInteger number => number.ToString(CultureInfo.InvariantCulture) + "n",
                _ => throw new InvalidOperationException("Unsupported diagnostic literal")
            };
        }
        if (type is TypeParameter parameter)
        {
            string name = parameter.IsThisType ? "this" : parameter.Symbol?.Name ?? "?";
            if (active.InferParameters?.Contains(parameter) != true)
                return name;
            if (await constraints.ConstraintAsync(parameter, cancellation).ConfigureAwait(false) is { } constraint)
            {
                var inferred = await inferredConstraints.GetAsync(parameter, omitReferences: true, cancellation).ConfigureAwait(false);
                if (inferred is null
                    || !await relations.RelatedAsync(constraint, inferred, RelationKind.Identity, cancellation).ConfigureAwait(false))
                    name += " extends " + await WriteAsync(constraint, active, cancellation).ConfigureAwait(false);
            }
            return "infer " + name;
        }
        if (type is UniqueSymbolType unique)
            return "unique symbol";
        if (!active.Active.Add(type))
            return type.Symbol is { Name: var name } && !name.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal) ? name : Elision;
        try
        {
            if (type.Alias is { } alias)
                return await NamedAsync(alias.Symbol.Name, alias.TypeArguments, active, cancellation).ConfigureAwait(false);
            if (type is SubstitutionType substitution)
            {
                string text = await WriteAsync(substitution.BaseType, active, cancellation).ConfigureAwait(false);
                return substitution.Constraint == context.UnknownType ? "NoInfer<" + text + ">" : text;
            }
            if (type is TemplateLiteralType template)
            {
                static string Escape(string text) => Quote(text, template: true)[1..^1];
                var text = new StringBuilder("`").Append(Escape(template.Texts[0]));
                for (int i = 0; i < template.Types.Count; i++)
                    text.Append("${").Append(await WriteAsync(template.Types[i], active, cancellation).ConfigureAwait(false))
                        .Append('}').Append(Escape(template.Texts[i + 1]));
                return text.Append('`').ToString();
            }
            if (type is StringMappingType mapping)
                return await NamedAsync(mapping.Symbol!.Name, [mapping.Target], active, cancellation).ConfigureAwait(false);
            if (type is ConditionalType conditional)
            {
                string check = await WriteAsync(conditional.CheckType, active, cancellation).ConfigureAwait(false);
                if (conditional.CheckType is ConditionalType)
                    check = "(" + check + ")";
                var previous = active.InferParameters;
                string extends;
                try
                {
                    active.InferParameters = conditional.Root.InferTypeParameters;
                    extends = await WriteAsync(conditional.ExtendsType, active, cancellation).ConfigureAwait(false);
                }
                finally
                {
                    active.InferParameters = previous;
                }
                if (conditional.ExtendsType is ConditionalType)
                    extends = "(" + extends + ")";
                return check + " extends " + extends + " ? "
                    + await WriteAsync(
                        await constraints.ConditionalTrueAsync(conditional, cancellation: cancellation).ConfigureAwait(false),
                        active,
                        cancellation).ConfigureAwait(false)
                    + " : " + await WriteAsync(
                        await constraints.ConditionalFalseAsync(conditional, cancellation).ConfigureAwait(false),
                        active,
                        cancellation).ConfigureAwait(false);
            }
            if (type is UnionOrIntersectionType composite)
            {
                var parts = new List<string>();
                IEnumerable<Type> ordered = type is UnionType
                    ? composite.Types.OrderBy(t => (t.Flags & TypeFlags.Undefined) != 0 ? 2 : (t.Flags & TypeFlags.Null) != 0 ? 1 : 0)
                    : composite.Types;
                foreach (var part in ordered)
                {
                    string text = await WriteAsync(part, active, cancellation).ConfigureAwait(false);
                    bool parentheses = type is IntersectionType && part is UnionType
                        || part is ObjectType && text.Contains("=>", StringComparison.Ordinal) && !text.StartsWith('{');
                    parts.Add(parentheses ? "(" + text + ")" : text);
                }
                return string.Join(type is UnionType ? " | " : " & ", parts);
            }
            if (type is TypeReference reference && (type.ObjectFlags & ObjectFlags.Reference) != 0)
            {
                var arguments = await references.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false);
                if (reference.Target is TupleType tuple)
                {
                    var items = new List<string>();
                    for (int i = 0; i < tuple.ElementInfos.Count; i++)
                    {
                        var info = tuple.ElementInfos[i];
                        var element = values.NonMissing(arguments[i], (info.Flags & ElementFlags.Optional) != 0);
                        string item = await WriteAsync(
                            element,
                            active,
                            cancellation).ConfigureAwait(false);
                        bool rest = (info.Flags & (ElementFlags.Rest | ElementFlags.Variadic)) != 0;
                        if (((info.Flags & ElementFlags.Rest) != 0
                            || (info.Flags & ElementFlags.Optional) != 0 && info.LabeledDeclaration is not NamedTupleMemberNode)
                            && (element is UnionOrIntersectionType or ConditionalType or IndexType
                                && element.Alias is null
                                && (element.Flags & TypeFlags.Boolean) == 0
                                || element is ObjectType && item.Contains("=>", StringComparison.Ordinal) && !item.StartsWith('{')))
                            item = "(" + item + ")";
                        if ((info.Flags & ElementFlags.Rest) != 0)
                            item += "[]";
                        if (info.LabeledDeclaration is NamedTupleMemberNode label)
                            item = (rest
                                ? "..."
                                : "") + ((IdentifierNode)label.Name!).Text + ((info.Flags & ElementFlags.Optional) != 0
                                    ? "?"
                                    : "") + ": " + item;
                        else
                            item = (rest ? "..." : "") + item + ((info.Flags & ElementFlags.Optional) != 0 ? "?" : "");
                        items.Add(item);
                    }
                    return (tuple.IsReadonly ? "readonly " : "") + "[" + string.Join(", ", items) + "]";
                }
                if (reference.Target?.Symbol?.Name is "Array" or "ReadonlyArray" && arguments.Count >= 1)
                {
                    string element = await WriteAsync(arguments[0], active, cancellation).ConfigureAwait(false);
                    if (arguments[0] is UnionOrIntersectionType || element.Contains("=>", StringComparison.Ordinal))
                        element = "(" + element + ")";
                    return (reference.Target.Symbol.Name == "ReadonlyArray" ? "readonly " : "") + element + "[]";
                }
                return await NamedAsync(
                    reference.Target?.Symbol?.Name ?? throw new InvalidOperationException("Unnamed diagnostic reference"),
                    arguments.Take(
                        ((InterfaceType)reference.Target).AllTypeParameters.Count - (((InterfaceType)reference.Target).ThisType is null
                            ? 0
                            : 1)).ToArray(),
                    active,
                    cancellation).ConfigureAwait(false);
            }
            if (type is IndexType index)
            {
                string target = await WriteAsync(index.Target, active, cancellation).ConfigureAwait(false);
                return "keyof " + (index.Target is UnionOrIntersectionType or ConditionalType ? "(" + target + ")" : target);
            }
            if (type is IndexedAccessType indexed)
            {
                string objectText = await WriteAsync(indexed.ObjectType, active, cancellation).ConfigureAwait(false);
                if (indexed.ObjectType is UnionOrIntersectionType or ConditionalType or IndexType)
                    objectText = "(" + objectText + ")";
                return objectText + "[" + await WriteAsync(
                        indexed.IndexType,
                        active,
                        cancellation).ConfigureAwait(false) + "]";
            }
            if (type is not ObjectType objectType)
                throw new InvalidOperationException($"Diagnostic display requires node building for {type.GetType().Name}");
            if ((type.ObjectFlags & ObjectFlags.ClassOrInterface) != 0 && type.Symbol is { } symbol)
                return symbol.Name;
            if (type is MappedType mappedType && await mapped.IsGenericAsync(mappedType, cancellation).ConfigureAwait(false))
            {
                var declaration = mappedType.Declaration!;
                string modifier = declaration.ReadonlyToken?.Kind switch
                {
                    SyntaxKind.MinusToken => "-readonly ",
                    SyntaxKind.PlusToken => "+readonly ",
                    SyntaxKind.ReadonlyKeyword => "readonly ",
                    _ => ""
                };
                string optional = declaration.QuestionToken?.Kind switch
                {
                    SyntaxKind.MinusToken => "-?",
                    SyntaxKind.PlusToken => "+?",
                    SyntaxKind.QuestionToken => "?",
                    _ => ""
                };
                var key = await mapped.ParameterAsync(mappedType, cancellation).ConfigureAwait(false);
                string constraint = await WriteAsync(
                    await mapped.ConstraintAsync(mappedType, cancellation).ConfigureAwait(false),
                    active,
                    cancellation).ConfigureAwait(false);
                string name = await mapped.NameAsync(mappedType, cancellation).ConfigureAwait(false) is { } nameType
                    ? " as " + await WriteAsync(nameType, active, cancellation).ConfigureAwait(false) : "";
                string value = await WriteAsync(
                    await mapped.TemplateAsync(mappedType, cancellation).ConfigureAwait(false),
                    active,
                    cancellation).ConfigureAwait(false);
                return "{ " + modifier + "[" + key.Symbol!.Name + " in " + constraint + name + "]" + optional + ": " + value + "; }";
            }
            var resolved = await members.ResolveAsync(objectType, cancellation).ConfigureAwait(false);
            bool emptyMembers = (resolved.Properties?.Count ?? 0) == 0 && resolved.IndexInfos.Count == 0;
            if (emptyMembers && resolved.CallSignatures.Count == 1 && resolved.ConstructSignatures.Count == 0)
                return await SignatureAsync(resolved.CallSignatures[0], false, true, active, cancellation).ConfigureAwait(false);
            if (emptyMembers && resolved.ConstructSignatures.Count == 1 && resolved.CallSignatures.Count == 0)
                return await SignatureAsync(resolved.ConstructSignatures[0], true, true, active, cancellation).ConfigureAwait(false);
            var fields = new List<string>();
            foreach (var signature in resolved.CallSignatures)
                fields.Add(await SignatureAsync(signature, false, false, active, cancellation).ConfigureAwait(false) + ";");
            foreach (var signature in resolved.ConstructSignatures)
                fields.Add(await SignatureAsync(signature, true, false, active, cancellation).ConfigureAwait(false) + ";");
            foreach (var indexInfo in resolved.IndexInfos)
            {
                string name = indexInfo.Declaration is IndexSignatureDeclarationNode { Parameters: { Count: > 0 } indexParameters }
                    && SemanticSyntax.Name(indexParameters[0]) is IdentifierNode identifier ? identifier.Text : "x";
                fields.Add(
                    (indexInfo.IsReadonly
                        ? "readonly "
                        : "") + "[" + name + ": " + await WriteAsync(
                            indexInfo.KeyType,
                            active,
                            cancellation).ConfigureAwait(false) + "]: " + (type is ReverseMappedType ? Elision : await WriteAsync(
                                indexInfo.ValueType,
                                active,
                                cancellation).ConfigureAwait(false)) + ";");
            }
            foreach (var property in resolved.Properties ?? [])
            {
                string propertyType;
                if (ElideReverseMappedProperty(property, active.ReverseMapped))
                    propertyType = Elision;
                else
                {
                    var value = await values.GetAsync(property, cancellation).ConfigureAwait(false);
                    bool readOnly = (property.CheckFlags & CheckFlags.Readonly) != 0
                        || property.Declarations.Any(d => SemanticSyntax.HasModifier(d, SyntaxKind.ReadonlyKeyword));
                    if ((property.Flags & (SymbolFlags.Function | SymbolFlags.Method)) != 0 && !readOnly)
                    {
                        var callable = algebra.Filter(value, t => (t.Flags & TypeFlags.Undefined) == 0);
                        var method = callable is StructuredType structured
                            ? await members.ResolveAsync(structured, cancellation).ConfigureAwait(false)
                            : null;
                        if (method is null || method.Properties is null or { Count: 0 })
                        {
                            foreach (var signature in method?.CallSignatures ?? [])
                                fields.Add(PropertyName(property) + ((property.Flags & SymbolFlags.Optional) != 0 ? "?" : "")
                                    + await SignatureAsync(signature, false, false, active, cancellation).ConfigureAwait(false) + ";");
                            if (method?.CallSignatures.Count > 0 || (property.Flags & SymbolFlags.Optional) == 0)
                                continue;
                        }
                    }
                    bool reverse = (property.CheckFlags & CheckFlags.ReverseMapped) != 0;
                    if (reverse)
                        active.ReverseMapped.Add(property);
                    try
                    {
                        propertyType = await WriteAsync(value, active, cancellation).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (reverse)
                            active.ReverseMapped.RemoveAt(active.ReverseMapped.Count - 1);
                    }
                }
                fields.Add(
                    ((property.CheckFlags & CheckFlags.Readonly) != 0
                        || property.Declarations.Any(d => SemanticSyntax.HasModifier(d, SyntaxKind.ReadonlyKeyword))
                        ? "readonly "
                        : "")
                    + PropertyName(property) + ((property.Flags & SymbolFlags.Optional) != 0 ? "?" : "") + ": "
                    + propertyType + ";");
            }
            return fields.Count == 0 ? "{}" : "{ " + string.Join(" ", fields) + " }";
        }
        finally
        {
            active.Active.Remove(type);
        }
    }

    private string Elision => noTruncation ? "any" : "...";

    internal string SymbolName(Symbol symbol)
    {
        var name = SemanticSyntax.Name(symbol.ValueDeclaration ?? symbol.Declarations.FirstOrDefault());
        if (name is StringLiteralNode && SemanticSyntax.Source(name) is { } file)
        {
            var (start, _) = CheckerDiagnostic.TokenRange(file, name.Pos);
            return file.Source.Text[file.Source.ToUtf16Position(start)..file.Source.ToUtf16Position(name.End)];
        }
        return PropertyName(symbol, quote: false);
    }

    private string PropertyName(Symbol symbol, bool quote = true)
    {
        var name = SemanticSyntax.Name(symbol.ValueDeclaration ?? symbol.Declarations.FirstOrDefault());
        if (name is ComputedPropertyNameNode computed)
        {
            static string? EntityName(SyntaxNode? expression)
            {
                var parts = new Stack<string>();
                while (expression is PropertyAccessExpressionNode access)
                {
                    if (access.Name is not IdentifierNode member)
                        return null;
                    parts.Push(member.Text);
                    expression = access.Expression;
                }
                if (expression is not IdentifierNode identifier)
                    return null;
                parts.Push(identifier.Text);
                return string.Join(".", parts);
            }
            if (EntityName(computed.Expression) is { } entity)
                return "[" + entity + "]";
        }
        if (links.Values.TryGet(symbol)?.NameType is UniqueSymbolType unique)
            return "[" + unique.Symbol!.Name + "]";
        if (name is PrivateIdentifierNode privateName)
            return privateName.Text;
        if (name is NumericLiteralNode)
            return symbol.Name;
        bool identifierName = symbol.Name.Length != 0;
        int index = 0;
        foreach (var rune in symbol.Name.EnumerateRunes())
            identifierName &= index++ == 0 ? TokenFacts.IsIdentifierStart(rune.Value) : TokenFacts.IsIdentifierPart(rune.Value);
        return quote && !identifierName ? Quote(symbol.Name) : symbol.Name;
    }

    private bool ElideReverseMappedProperty(Symbol property, IReadOnlyList<Symbol> stack)
    {
        if ((property.CheckFlags & CheckFlags.ReverseMapped) == 0)
            return false;
        if (stack.Contains(property))
            return true;
        if (stack.Count != 0 && links.ReverseMappedSymbols.TryGet(stack[^1])?.PropertyType is { } parent
            && (parent.ObjectFlags & ObjectFlags.Anonymous) == 0)
            return true;
        // The reference inspects four preceding symbols after at least three nested mappings.
        if (stack.Count < 3 || links.ReverseMappedSymbols.TryGet(property)?.MappedType?.Symbol is not { } mappedSymbol)
            return false;
        for (int i = 0; i < stack.Count && i <= 3; i++)
            if (links.ReverseMappedSymbols.TryGet(stack[stack.Count - 1 - i])?.MappedType?.Symbol == mappedSymbol)
                return true;
        return false;
    }

    private async ValueTask<string> NamedAsync(
        string name,
        IReadOnlyList<Type> arguments,
        DisplayState active,
        CancellationToken cancellation)
    {
        if (arguments.Count == 0)
            return name;
        var parts = new List<string>();
        foreach (var argument in arguments)
            parts.Add(await WriteAsync(argument, active, cancellation).ConfigureAwait(false));
        return name + "<" + string.Join(", ", parts) + ">";
    }

    private static string Quote(string value, bool template = false)
    {
        var result = new StringBuilder(template ? "`" : "\"");
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                result.Append(ch).Append(value[++i]);
                continue;
            }
            result.Append(ch switch
            {
                '\0' => i + 1 < value.Length && char.IsAsciiDigit(value[i + 1]) ? "\\x00" : "\\0",
                '"' => template ? "\"" : "\\\"",
                '`' when template => "\\`",
                '$' when template && i + 1 < value.Length && value[i + 1] == '{' => "\\$",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => template ? "\n" : "\\n",
                '\v' => "\\v",
                '\f' => "\\f",
                '\r' => "\\r",
                < ' ' or '\u0085' or '\u2028' or '\u2029' or >= '\ud800' and <= '\udfff' => "\\u" + ((int)ch).ToString(
                    "X4",
                    CultureInfo.InvariantCulture),
                _ => ch.ToString()
            });
        }
        return result.Append(template ? '`' : '"').ToString();
    }

    private async ValueTask<string> SignatureAsync(
        Signature signature,
        bool construct,
        bool arrow,
        DisplayState active,
        CancellationToken cancellation)
    {
        var typeParameters = new List<string>();
        foreach (var parameter in signature.TypeParameters)
        {
            string text = parameter.Symbol?.Name ?? "T";
            if (await constraints.ConstraintAsync(parameter, cancellation).ConfigureAwait(false) is { } constraint)
            {
                string? reused = null;
                if (parameter.Symbol?.Declarations.OfType<TypeParameterDeclarationNode>().FirstOrDefault(d => d.Constraint is not null)?.Constraint is { } annotation
                    && await nodes.FromNodeAsync(annotation, cancellation).ConfigureAwait(false) == constraint)
                    reused = Reuse(annotation);
                text += " extends " + (reused ?? await WriteAsync(constraint, active, cancellation).ConfigureAwait(false));
            }
            if (await constraints.DefaultAsync(parameter, cancellation).ConfigureAwait(false) is { } defaultType)
                text += " = " + await WriteAsync(defaultType, active, cancellation).ConfigureAwait(false);
            typeParameters.Add(text);
        }
        var arguments = new List<string>();
        foreach (var parameter in signature.ThisParameter is { } thisParameter
            ? new[] { thisParameter }.Concat(signature.Parameters)
            : signature.Parameters)
        {
            bool rest = (parameter.CheckFlags & CheckFlags.RestParameter) != 0
                || parameter.ValueDeclaration is ParameterDeclarationNode { DotDotDotToken: not null };
            bool optional = (parameter.Flags & SymbolFlags.Optional) != 0
                || parameter.ValueDeclaration is ParameterDeclarationNode { QuestionToken: not null }
                    or ParameterDeclarationNode { Initializer: not null };
            arguments.Add((rest ? "..." : "") + parameter.Name + (optional ? "?" : "") + ": "
                + await WriteAsync(
                    await parameters.ParameterAsync(parameter, cancellation).ConfigureAwait(false),
                    active,
                    cancellation).ConfigureAwait(false));
        }
        string returned = await signatures.PredicateAsync(signature, cancellation).ConfigureAwait(false) is { } predicate
            ? await PredicateAsync(predicate, active, cancellation).ConfigureAwait(false)
            : await WriteAsync(
                await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false),
                active,
                cancellation).ConfigureAwait(false);
        return (construct ? arrow && (signature.Flags & SignatureFlags.Abstract) != 0 ? "abstract new " : "new " : "")
            + (typeParameters.Count == 0 ? "" : "<" + string.Join(", ", typeParameters) + ">")
            + "(" + string.Join(", ", arguments) + ")" + (arrow ? " => " : ": ")
            + returned;
    }

    private static string? Reuse(SyntaxNode node)
    {
        if (node is LiteralTypeNode literal && SemanticSyntax.Source(literal.Literal!) is { } source)
        {
            var scanner = new Scanner(source.Source);
            scanner.ResetPosition(source.Source.ToUtf16Position(literal.Literal!.Pos));
            scanner.Scan();
            return source.Source.Text[scanner.TokenStart..source.Source.ToUtf16Position(literal.Literal.End)];
        }
        if (node is UnionTypeNode or IntersectionTypeNode)
        {
            var parts = (node is UnionTypeNode union ? union.Types! : ((IntersectionTypeNode)node).Types!).Select(Reuse).ToArray();
            return parts.Any(p => p is null) ? null : string.Join(node.Kind == SyntaxKind.UnionType ? " | " : " & ", parts);
        }
        if (node is ParenthesizedTypeNode parentheses && Reuse(parentheses.Type!) is { } inner)
            return "(" + inner + ")";
        return null;
    }
}
