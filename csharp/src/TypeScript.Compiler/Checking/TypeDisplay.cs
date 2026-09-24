using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

// Structural diagnostic displays. Declaration emit and accessibility-aware node
// building require the separate node-builder service.
internal sealed class TypeDisplay(TypeContext context, StructuredMembers members, TypeConstraints constraints,
    TypeReferences references, SymbolTypes values, Signatures signatures, SignatureParameters parameters, TypeNodes nodes)
{
    internal ValueTask<string> GetAsync(Type type, CancellationToken cancellation = default) => WriteAsync(type, [], cancellation);

    private async ValueTask<string> WriteAsync(Type type, HashSet<Type> active, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type.Alias is { } alias)
            return await NamedAsync(alias.Symbol.Name, alias.TypeArguments, active, cancellation).ConfigureAwait(false);
        if (type is IntrinsicType intrinsic)
            return intrinsic.IntrinsicName is "error" or "auto" or "wildcard" ? "any" : intrinsic.IntrinsicName;
        if ((type.Flags & TypeFlags.Boolean) != 0)
            return "boolean";
        if (type is LiteralType literal)
        {
            if ((literal.Flags & TypeFlags.EnumLiteral) != 0 && literal.Symbol is { } enumMember)
                return enumMember.Parent!.Name + "." + enumMember.Name;
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
            return parameter.IsThisType ? "this" : parameter.Symbol?.Name ?? "T";
        if (type is UniqueSymbolType unique)
            return "typeof " + unique.Symbol!.Name;
        if (!active.Add(type))
            return type.Symbol is { Name: var name } && !name.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal) ? name : "...";
        try
        {
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
                    for (int i = 0; i < arguments.Count; i++)
                    {
                        var info = tuple.ElementInfos[i];
                        string item = await WriteAsync(arguments[i], active, cancellation).ConfigureAwait(false);
                        bool rest = (info.Flags & (ElementFlags.Rest | ElementFlags.Variadic)) != 0;
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
                if (reference.Target?.Symbol?.Name is "Array" or "ReadonlyArray" && arguments.Count == 1)
                {
                    string element = await WriteAsync(arguments[0], active, cancellation).ConfigureAwait(false);
                    if (arguments[0] is UnionOrIntersectionType || element.Contains("=>", StringComparison.Ordinal))
                        element = "(" + element + ")";
                    return (reference.Target.Symbol.Name == "ReadonlyArray" ? "readonly " : "") + element + "[]";
                }
                return await NamedAsync(
                    reference.Target?.Symbol?.Name ?? throw new InvalidOperationException("Unnamed diagnostic reference"),
                    arguments,
                    active,
                    cancellation).ConfigureAwait(false);
            }
            if (type is IndexType index)
                return "keyof " + await WriteAsync(index.Target, active, cancellation).ConfigureAwait(false);
            if (type is IndexedAccessType indexed)
                return await WriteAsync(
                    indexed.ObjectType,
                    active,
                    cancellation).ConfigureAwait(false) + "[" + await WriteAsync(
                        indexed.IndexType,
                        active,
                        cancellation).ConfigureAwait(false) + "]";
            if (type is not ObjectType objectType)
                throw new InvalidOperationException($"Diagnostic display requires node building for {type.GetType().Name}");
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
                fields.Add(
                    (indexInfo.IsReadonly
                        ? "readonly "
                        : "") + "[x: " + await WriteAsync(
                            indexInfo.KeyType,
                            active,
                            cancellation).ConfigureAwait(false) + "]: " + await WriteAsync(
                                indexInfo.ValueType,
                                active,
                                cancellation).ConfigureAwait(false) + ";");
            foreach (var property in resolved.Properties ?? [])
                fields.Add(
                    ((property.CheckFlags & CheckFlags.Readonly) != 0
                        || property.Declarations.Any(d => SemanticSyntax.HasModifier(d, SyntaxKind.ReadonlyKeyword))
                        ? "readonly "
                        : "")
                    + property.Name + ((property.Flags & SymbolFlags.Optional) != 0 ? "?" : "") + ": "
                    + await WriteAsync(
                        await values.GetAsync(property, cancellation).ConfigureAwait(false),
                        active,
                        cancellation).ConfigureAwait(false) + ";");
            return fields.Count == 0 ? "{}" : "{ " + string.Join(" ", fields) + " }";
        }
        finally
        {
            active.Remove(type);
        }
    }

    private async ValueTask<string> NamedAsync(
        string name,
        IReadOnlyList<Type> arguments,
        HashSet<Type> active,
        CancellationToken cancellation)
    {
        if (arguments.Count == 0)
            return name;
        var parts = new List<string>();
        foreach (var argument in arguments)
            parts.Add(await WriteAsync(argument, active, cancellation).ConfigureAwait(false));
        return name + "<" + string.Join(", ", parts) + ">";
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
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
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\v' => "\\v",
                '\f' => "\\f",
                '\r' => "\\r",
                < ' ' or '\u0085' or '\u2028' or '\u2029' or >= '\ud800' and <= '\udfff' => "\\u" + ((int)ch).ToString(
                    "X4",
                    CultureInfo.InvariantCulture),
                _ => ch.ToString()
            });
        }
        return result.Append('"').ToString();
    }

    private async ValueTask<string> SignatureAsync(
        Signature signature,
        bool construct,
        bool arrow,
        HashSet<Type> active,
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
        return (construct ? "new " : "") + (typeParameters.Count == 0 ? "" : "<" + string.Join(", ", typeParameters) + ">")
            + "(" + string.Join(", ", arguments) + ")" + (arrow ? " => " : ": ")
            + await WriteAsync(
                await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false),
                active,
                cancellation).ConfigureAwait(false);
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
