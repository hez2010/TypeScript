using TypeScript.Compiler.Text;
using System.Globalization;
using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private sealed class TypeSyntaxLength(bool noTruncation)
    {
        internal long Value { get; set; }
        internal bool NoTruncation { get; } = noTruncation;
        internal bool WasTruncated { get; set; }

        internal void Add(long count) => Value += count;

        internal void Add(Utf8String text, int extra = 0) => Add(text.Length + extra);

        internal bool Truncated() => WasTruncated |= Value > (NoTruncation
            ? TypeDisplay.NoTruncationMaximumTruncationLength : TypeDisplay.DefaultMaximumTruncationLength);
    }

    private static int IntrinsicSyntaxLength(Type type)
    {
        if ((type.Flags & TypeFlags.Any) != 0)
            return type.Alias is null ? 3 : 0;
        if ((type.Flags & (TypeFlags.String | TypeFlags.Number | TypeFlags.BigInt | TypeFlags.ESSymbol | TypeFlags.NonPrimitive)) != 0)
            return 6;
        if ((type.Flags & TypeFlags.Boolean) != 0 && type.Alias is null)
            return 7;
        if (type is LiteralType literal && (type.Flags & TypeFlags.EnumLike) == 0)
            return literal.Value switch
            {
                Utf8String value => value.Length + 2,
                double value => TokenFacts.NumberText(value).Length,
                System.Numerics.BigInteger value => Utf8String.Format(value).Length + 1,
                bool value => value ? 4 : 5,
                _ => 0
            };
        if ((type.Flags & (TypeFlags.Void | TypeFlags.Null)) != 0 || type is TypeParameter { IsThisType: true })
            return 4;
        if ((type.Flags & TypeFlags.Undefined) != 0)
            return 9;
        return (type.Flags & TypeFlags.Never) != 0 ? 5 : 0;
    }

    private static SyntaxNode ElidedTypeSyntax(TypeSyntaxContext state, int? count = null, bool countLength = true)
    {
        if (countLength)
            state.Length.Add(3);
        return state.Length.NoTruncation ? state.Factory.NewKeywordTypeNode(K.AnyKeyword)
            : state.Factory.NewTypeReferenceNode(state.Factory.NewIdentifier(count is { } n
                ? Utf8Literals.EllipsisSpace + Utf8String.Format(n) + Utf8Literals.More : Utf8Literals.Ellipsis), null);
    }

    private static void AddReusedSyntaxLength(SyntaxNode node, TypeSyntaxContext state)
    {
        if (node.Pos >= 0 && node.End >= node.Pos)
            state.Length.Add((long)node.End - node.Pos);
    }

    private static void AddExpressionNameLength(TypeSyntaxLength length, Utf8String name, bool first, bool enumMember)
    {
        if (first || IdentifierName(name.Span.StartsWith((byte)'#') ? name[1..] : name))
            length.Add(name, 1);
        else
        {
            if (name.Span.StartsWith((byte)'['))
                name = name[1..^1];
            bool quoted = Quoted(name) && !enumMember;
            length.Add(quoted ? UnquoteSymbolText(name) : name, quoted ? 4 : 2);
        }
    }
}
