using TypeScript.Compiler.Text;
using System.Globalization;
using System.Numerics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class TypeInference
{
    private async ValueTask TemplateAsync(State state, Type source, TemplateLiteralType target, CancellationToken cancellation)
    {
        var matches = await templates.InferAsync(source, target, AssignableAsync, cancellation).ConfigureAwait(false);
        if (matches is not { Count: > 0 } && !target.Texts.All(t => t.Length == 0))
            return;
        for (int i = 0; i < target.Types.Count; i++)
        {
            var candidate = matches is { Count: > 0 } ? matches[i] : context.NeverType;
            var variable = target.Types[i];
            if (candidate is LiteralType { Value: Utf8String text } && (variable.Flags & TypeFlags.TypeVariable) != 0
                && Info(state, variable) is { } info
                && await constraints.BaseConstraintAsync(info.Parameter, cancellation).ConfigureAwait(false) is { } constraint
                && (constraint.Flags & TypeFlags.Any) == 0)
            {
                var flags = Parts(constraint).Aggregate(TypeFlags.None, (f, t) => f | t.Flags);
                if ((flags & TypeFlags.String) == 0)
                {
                    double number = JsNumber.FromString(text);
                    if ((flags & TypeFlags.NumberLike) != 0
                        && (text.Length == 0 || !double.IsFinite(number) || TokenFacts.NumberText(number) != text))
                        flags &= ~TypeFlags.NumberLike;
                    bool bigint = TemplateMatching.BigInt(text)
                        && BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
                        && Utf8String.Format(integer) == text;
                    if ((flags & TypeFlags.BigIntLike) != 0 && !bigint)
                        flags &= ~TypeFlags.BigIntLike;
                    Type selected = context.NeverType;
                    foreach (var part in Parts(constraint))
                        selected = await ChooseAsync(selected, part).ConfigureAwait(false);
                    if ((selected.Flags & TypeFlags.Never) == 0)
                    {
                        await FromAsync(state, selected, variable, cancellation).ConfigureAwait(false);
                        continue;
                    }

                    async ValueTask<Type> ChooseAsync(Type left, Type right)
                    {
                        if ((right.Flags & flags) == 0 || (left.Flags & TypeFlags.String) != 0)
                            return left;
                        if ((right.Flags & TypeFlags.String) != 0)
                            return candidate;
                        if ((left.Flags & TypeFlags.TemplateLiteral) != 0)
                            return left;
                        if (right is TemplateLiteralType template
                            && await templates.MatchesAsync(candidate, template, AssignableAsync, cancellation).ConfigureAwait(false))
                            return candidate;
                        if ((left.Flags & TypeFlags.StringMapping) != 0)
                            return left;
                        if (right is StringMappingType mapping && TypeAlgebra.ApplyStringMapping(mapping.Symbol!.Name, text) == text)
                            return candidate;
                        if ((left.Flags & TypeFlags.StringLiteral) != 0)
                            return left;
                        if (right is LiteralType { Value: Utf8String value } && value == text)
                            return right;
                        if ((left.Flags & TypeFlags.Number) != 0)
                            return left;
                        if ((right.Flags & TypeFlags.Number) != 0)
                            return context.GetNumberLiteralType(number);
                        if ((left.Flags & TypeFlags.Enum) != 0)
                            return left;
                        if ((right.Flags & TypeFlags.Enum) != 0)
                            return context.GetNumberLiteralType(number);
                        if ((left.Flags & TypeFlags.NumberLiteral) != 0)
                            return left;
                        if (right is LiteralType { Value: double numeric } && numeric == number)
                            return right;
                        if ((left.Flags & TypeFlags.BigInt) != 0)
                            return left;
                        if ((right.Flags & TypeFlags.BigInt) != 0)
                            return context.GetBigIntLiteralType(BigInteger.Parse(text, CultureInfo.InvariantCulture));
                        if ((left.Flags & TypeFlags.BigIntLiteral) != 0)
                            return left;
                        if (right is LiteralType { Value: BigInteger big } && Utf8String.Format(big) == text)
                            return right;
                        if ((left.Flags & TypeFlags.Boolean) != 0)
                            return left;
                        if ((right.Flags & TypeFlags.Boolean) != 0)
                            return text == Utf8Literals.True ? context.TrueType : text == Utf8Literals.False ? context.FalseType : context.BooleanType;
                        if ((left.Flags & TypeFlags.BooleanLiteral) != 0)
                            return left;
                        if (right is LiteralType { Value: bool boolean } && (boolean ? Utf8Literals.True : Utf8Literals.False) == text)
                            return right;
                        if ((left.Flags & TypeFlags.Undefined) != 0)
                            return left;
                        if ((right.Flags & TypeFlags.Undefined) != 0 && right is IntrinsicType undefined && undefined.IntrinsicName == text)
                            return right;
                        if ((left.Flags & TypeFlags.Null) != 0)
                            return left;
                        if ((right.Flags & TypeFlags.Null) != 0 && right is IntrinsicType nullable && nullable.IntrinsicName == text)
                            return right;
                        return left;
                    }
                }
            }
            await FromAsync(state, candidate, variable, cancellation).ConfigureAwait(false);
        }
    }
}
