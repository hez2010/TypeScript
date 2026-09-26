using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Checking;

// Diagnostic types, signatures and predicates share the checker node builder.
internal sealed class TypeDisplay(TypeContext context, CheckerLinks links, bool noTruncation,
    Func<Type, NodeBuilderFlags, CancellationToken, ValueTask<string>> typeSyntax,
    Func<Signature, CancellationToken, ValueTask<string>> signatureSyntax,
    Func<TypePredicate, CancellationToken, ValueTask<string>> predicateSyntax)
{
    private int serializationLevel;

    internal ValueTask<string> GetSignatureAsync(Signature signature, CancellationToken cancellation = default)
    {
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
        return signatureSyntax(signature, cancellation);
    }

    internal ValueTask<string> GetPredicateAsync(TypePredicate predicate, CancellationToken cancellation = default)
        => predicateSyntax(predicate, cancellation);

    internal ValueTask<string> GetAsync(Type type, CancellationToken cancellation = default)
        => GetAsync(type, NodeBuilderFlags.AllowUniqueESSymbolType | NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope, cancellation);

    internal async ValueTask<string> GetAsync(Type type, NodeBuilderFlags flags, CancellationToken cancellation = default)
    {
        // Lazy member resolution can report another diagnostic while building a diagnostic type.
        if (serializationLevel >= 2)
            return "?";
        serializationLevel++;
        try
        {
            if (noTruncation)
                flags |= NodeBuilderFlags.NoTruncation;
            int maximumLength = (flags & NodeBuilderFlags.NoTruncation) != 0 ? 2_000_000 : 320;
            string text = await typeSyntax(type, flags, cancellation).ConfigureAwait(false);
            return Encoding.UTF8.GetByteCount(text) >= maximumLength
                ? Wtf8.DecodeString(Wtf8.Encode(text).AsSpan(0, maximumLength - 3)) + "..." : text;
        }
        finally
        {
            serializationLevel--;
        }
    }

    internal string SymbolName(Symbol symbol)
    {
        var name = SemanticSyntax.Name(symbol.ValueDeclaration ?? symbol.Declarations.FirstOrDefault());
        if (name is StringLiteralNode && SemanticSyntax.Source(name) is { } file)
        {
            var (start, _) = CheckerDiagnostic.TokenRange(file, name.Pos);
            return file.Source.Text[file.Source.ToUtf16Position(start)..file.Source.ToUtf16Position(name.End)];
        }
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
        return name is PrivateIdentifierNode privateName ? privateName.Text : symbol.Name;
    }
}
