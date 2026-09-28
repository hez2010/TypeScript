using TypeScript.Compiler.Text;
using System.Numerics;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

// Deterministic order used by normalized unions and relation keys. This is not
// assignability or structural equality. Declarations precede allocation IDs.
internal sealed class TypeOrder : IComparer<Type>, IComparer<Symbol>
{
    private readonly Dictionary<SourceFileNode, int> files = new(ReferenceEqualityComparer.Instance);
    private Stack<Part>? reusableParts;

    internal TypeOrder(IEnumerable<SourceFileNode> sourceFiles)
    {
        foreach (var source in sourceFiles)
            files.Add(source, files.Count);
    }

    private enum PartKind
    {
        Type,
        Structure,
        TypeId,
        Names,
        Types,
        Mappers,
        Result
    }

    private readonly record struct Part(PartKind Kind, object? Left, object? Right, int Result = 0);

    public int Compare(Type? left, Type? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is LiteralType a && right is LiteralType b && a.Flags == b.Flags
            && (a.Flags & TypeFlags.EnumLike) == 0 && a.Alias is null && b.Alias is null)
        {
            a.Context.RequireOwned(b);
            int result = CompareLiteralValues(a, b);
            return result != 0 ? result : a.Id.CompareTo(b.Id);
        }
        var pending = Interlocked.Exchange(ref reusableParts, null) ?? new Stack<Part>();
        try
        {
            return CompareCore(left, right, pending);
        }
        finally
        {
            pending.Clear();
            Interlocked.CompareExchange(ref reusableParts, pending, null);
        }
    }

    private int CompareCore(Type? left, Type? right, Stack<Part> pending)
    {
        pending.Push(new(PartKind.Type, left, right));
        while (pending.TryPop(out var part))
        {
            if (part.Kind == PartKind.Result)
            {
                if (part.Result != 0)
                    return part.Result;
                continue;
            }
            if (ReferenceEquals(part.Left, part.Right))
                continue;
            int result;
            switch (part.Kind)
            {
                case PartKind.Type:
                    if (part.Left is null)
                        return -1;
                    if (part.Right is null)
                        return 1;
                    var a = (Type)part.Left;
                    var b = (Type)part.Right;
                    a.Context.RequireOwned(b);
                    result = SortFlags(a).CompareTo(SortFlags(b));
                    if (result != 0)
                        return result;
                    pending.Push(new(PartKind.TypeId, a, b));
                    pending.Push(new(PartKind.Structure, a, b));
                    pending.Push(new(PartKind.Names, a, b));
                    break;
                case PartKind.TypeId:
                    return ((Type)part.Left!).Id.CompareTo(((Type)part.Right!).Id);
                case PartKind.Names:
                    a = (Type)part.Left!;
                    b = (Type)part.Right!;
                    Symbol? sa = NameSymbol(a), sb = NameSymbol(b);
                    if (sa == sb)
                        pending.Push(new(PartKind.Types, a.Alias?.TypeArguments, b.Alias?.TypeArguments));
                    else
                    {
                        if (sa is null)
                            return 1;
                        if (sb is null)
                            return -1;
                        result = CompareSymbolNames(sa.Name, sb.Name);
                        if (result != 0)
                            return result;
                        result = CompareSymbols(sa, sb);
                        if (result != 0)
                            return result;
                    }
                    break;
                case PartKind.Types:
                    var ta = (IReadOnlyList<Type>?)part.Left ?? [];
                    var tb = (IReadOnlyList<Type>?)part.Right ?? [];
                    if (ta.Count != tb.Count)
                        return ta.Count.CompareTo(tb.Count);
                    for (int i = ta.Count - 1; i >= 0; i--)
                        pending.Push(new(PartKind.Type, ta[i], tb[i]));
                    break;
                case PartKind.Mappers:
                    if (part.Left is null)
                        return 1;
                    if (part.Right is null)
                        return -1;
                    var ma = (TypeMapper)part.Left;
                    var mb = (TypeMapper)part.Right;
                    result = ma.Kind.CompareTo(mb.Kind);
                    if (result != 0)
                        return result;
                    switch (ma.Kind)
                    {
                        case TypeMapperKind.Simple:
                            pending.Push(new(PartKind.Type, ma.Single.Target, mb.Single.Target));
                            pending.Push(new(PartKind.Type, ma.Single.Source, mb.Single.Source));
                            break;
                        case TypeMapperKind.Array:
                            pending.Push(new(PartKind.Types, ma.Targets, mb.Targets));
                            pending.Push(new(PartKind.Types, ma.Sources, mb.Sources));
                            break;
                        case TypeMapperKind.Merged:
                            pending.Push(new(PartKind.Mappers, ma.Parts.Second, mb.Parts.Second));
                            pending.Push(new(PartKind.Mappers, ma.Parts.First, mb.Parts.First));
                            break;
                    }
                    break;
                case PartKind.Structure:
                    result = Structure((Type)part.Left!, (Type)part.Right!, pending);
                    if (result != 0)
                        return result;
                    break;
            }
        }
        return 0;
    }

    private int Structure(Type a, Type b, Stack<Part> pending)
    {
        // Boolean is also a union, but is ordered with the singleton types.
        if ((a.Flags & TypeFlags.Singleton) != 0)
            return 0;
        int result;
        switch (a)
        {
            case ObjectType oa:
                var ob = (ObjectType)b;
                if (oa is InstantiationExpressionType expressionA && ob is InstantiationExpressionType expressionB)
                {
                    result = CompareNodes(
                        expressionA.Symbol?.Declarations.FirstOrDefault(),
                        expressionB.Symbol?.Declarations.FirstOrDefault());
                    if (result != 0)
                        return result;
                    result = CompareNodes(expressionA.Node, expressionB.Node);
                    if (result != 0)
                        return result;
                }
                else
                {
                    result = CompareSymbols(oa.Symbol, ob.Symbol);
                    if (result != 0)
                        return result;
                }
                bool ar = (oa.ObjectFlags & ObjectFlags.Reference) != 0, br = (ob.ObjectFlags & ObjectFlags.Reference) != 0;
                if (ar && br)
                {
                    var ra = (TypeReference)oa;
                    var rb = (TypeReference)ob;
                    if (ra.Target is TupleType tupleA && rb.Target is TupleType tupleB)
                    {
                        result = CompareTuples(tupleA, tupleB);
                        if (result != 0)
                            return result;
                    }
                    if (ra.Node is null && rb.Node is null)
                        pending.Push(new(PartKind.Types, ra.ResolvedTypeArguments, rb.ResolvedTypeArguments));
                    else
                    {
                        result = CompareNodes(ra.Node, rb.Node);
                        if (result != 0)
                            return result;
                        pending.Push(new(PartKind.Mappers, ra.Mapper, rb.Mapper));
                    }
                }
                else if (ar)
                    return -1;
                else if (br)
                    return 1;
                else
                {
                    result = (oa.ObjectFlags & ObjectFlags.ObjectTypeKindMask).CompareTo(ob.ObjectFlags & ObjectFlags.ObjectTypeKindMask);
                    if (result != 0)
                        return result;
                    var ma = oa.Mapper;
                    var mb = ob.Mapper;
                    if (oa is MappedType)
                    {
                        ma = ma?.Parts.Second;
                        mb = mb?.Parts.Second;
                    }
                    pending.Push(new(PartKind.Mappers, ma, mb));
                    if (oa is ReverseMappedType reverseA)
                    {
                        var reverseB = (ReverseMappedType)ob;
                        pending.Push(new(PartKind.Type, reverseA.ConstraintType, reverseB.ConstraintType));
                        pending.Push(new(PartKind.Type, reverseA.MappedType, reverseB.MappedType));
                        pending.Push(new(PartKind.Type, reverseA.Source, reverseB.Source));
                    }
                }
                break;
            case UnionType ua:
                var ub = (UnionType)b;
                if (ua.Origin is null && ub.Origin is null)
                    pending.Push(new(PartKind.Types, ua.Types, ub.Types));
                else if (ua.Origin is null)
                    return 1;
                else if (ub.Origin is null)
                    return -1;
                else
                    pending.Push(new(PartKind.Type, ua.Origin, ub.Origin));
                break;
            case IntersectionType ia:
                pending.Push(new(PartKind.Types, ia.Types, ((IntersectionType)b).Types));
                break;
            case LiteralType when (a.Flags & TypeFlags.EnumLike) != 0:
            case UniqueSymbolType:
            case TypeParameter:
                return CompareSymbols(a.Symbol, b.Symbol);
            case LiteralType la:
                return CompareLiteralValues(la, (LiteralType)b);
            case IndexType xa:
                var xb = (IndexType)b;
                pending.Push(new(PartKind.Result, null, null, xa.IndexFlags.CompareTo(xb.IndexFlags)));
                pending.Push(new(PartKind.Type, xa.Target, xb.Target));
                break;
            case IndexedAccessType aa:
                var ab = (IndexedAccessType)b;
                pending.Push(new(PartKind.Type, aa.IndexType, ab.IndexType));
                pending.Push(new(PartKind.Type, aa.ObjectType, ab.ObjectType));
                break;
            case ConditionalType ca:
                var cb = (ConditionalType)b;
                result = CompareNodes(ca.Root.Node, cb.Root.Node);
                if (result != 0)
                    return result;
                pending.Push(new(PartKind.Mappers, ca.Mapper, cb.Mapper));
                break;
            case SubstitutionType sa:
                var sb = (SubstitutionType)b;
                pending.Push(new(PartKind.Type, sa.Constraint, sb.Constraint));
                pending.Push(new(PartKind.Type, sa.BaseType, sb.BaseType));
                break;
            case TemplateLiteralType ta:
                var tb = (TemplateLiteralType)b;
                for (int i = 0; i < Math.Min(ta.Texts.Count, tb.Texts.Count); i++)
                {
                    result = CompareText(ta.Texts[i], tb.Texts[i]);
                    if (result != 0)
                        return result;
                }
                if (ta.Texts.Count != tb.Texts.Count)
                    return ta.Texts.Count.CompareTo(tb.Texts.Count);
                pending.Push(new(PartKind.Types, ta.Types, tb.Types));
                break;
            case StringMappingType ma:
                pending.Push(new(PartKind.Type, ma.Target, ((StringMappingType)b).Target));
                break;
        }
        return 0;
    }

    private static uint SortFlags(Type type) => (type.Flags & TypeFlags.EnumLike) != 0 && (type.Flags & TypeFlags.Union) == 0
        ? (uint)TypeFlags.Enum : (uint)type.Flags;

    private static int CompareLiteralValues(LiteralType left, LiteralType right) => left.Value switch
    {
        TextSlice text => CompareText(text, (TextSlice)right.Value!),
        double number => number.CompareTo((double)right.Value!),
        BigInteger integer => integer.CompareTo((BigInteger)right.Value!),
        bool boolean => boolean.CompareTo((bool)right.Value!),
        _ => throw new InvalidOperationException("Invalid literal value")
    };

    private static Symbol? NameSymbol(Type type) => type.Alias?.Symbol
        ?? ((type.Flags & (TypeFlags.TypeParameter | TypeFlags.StringMapping)) != 0
            || (type.ObjectFlags & (ObjectFlags.ClassOrInterface | ObjectFlags.Reference)) != 0 ? type.Symbol : null);

    internal int CompareSymbols(Symbol? left, Symbol? right)
    {
        if (left == right)
            return 0;
        if (left is null)
            return 1;
        if (right is null)
            return -1;
        if (left.Declarations.Length != 0 && right.Declarations.Length != 0)
        {
            int result = CompareNodes(left.Declarations[0], right.Declarations[0]);
            if (result != 0)
                return result;
        }
        else if (left.Declarations.Length != 0)
            return -1;
        else if (right.Declarations.Length != 0)
            return 1;
        int name = CompareSymbolNames(left.Name, right.Name);
        return name != 0 ? name : left.Id.CompareTo(right.Id);
    }

    int IComparer<Symbol>.Compare(Symbol? left, Symbol? right) => CompareSymbols(left, right);

    internal int CompareNodes(SyntaxNode? left, SyntaxNode? right)
    {
        if (left == right)
            return 0;
        if (left is null)
            return 1;
        if (right is null)
            return -1;
        static SourceFileNode? Source(SyntaxNode node)
        {
            while (node.Parent is { } parent)
                node = parent;
            return node as SourceFileNode;
        }
        var a = Source(left);
        var b = Source(right);
        return a == b ? left.Pos.CompareTo(right.Pos)
            : (a is null ? 0 : files.GetValueOrDefault(a)).CompareTo(b is null ? 0 : files.GetValueOrDefault(b));
    }

    private static int CompareTuples(TupleType left, TupleType right)
    {
        if (left == right)
            return 0;
        if (left.IsReadonly != right.IsReadonly)
            return left.IsReadonly ? 1 : -1;
        if (left.ElementInfos.Count != right.ElementInfos.Count)
            return left.ElementInfos.Count.CompareTo(right.ElementInfos.Count);
        for (int i = 0; i < left.ElementInfos.Count; i++)
        {
            int result = left.ElementInfos[i].Flags.CompareTo(right.ElementInfos[i].Flags);
            if (result != 0)
                return result;
        }
        for (int i = 0; i < left.ElementInfos.Count; i++)
        {
            var a = left.ElementInfos[i].LabeledDeclaration;
            var b = right.ElementInfos[i].LabeledDeclaration;
            if (a == b)
                continue;
            if (a is null)
                return -1;
            if (b is null)
                return 1;
            int result = CompareText(((IdentifierNode)((INamedNode)a).Name!).Text, ((IdentifierNode)((INamedNode)b).Name!).Text);
            if (result != 0)
                return result;
        }
        return 0;
    }

    // UTF-8/WTF-8 byte order equals code point order, including lone surrogates.
    // UTF-16 ordinal comparison alone reverses some BMP/supplementary pairs.
    internal static int CompareSymbolNames(TextSlice left, TextSlice right)
    {
        bool leftPrefixed = left.Span.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal);
        bool rightPrefixed = right.Span.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal);
        bool leftInternal = leftPrefixed && !left.Span.StartsWith(Symbol.InternalPrefix + Symbol.InternalPrefix, StringComparison.Ordinal);
        bool rightInternal = rightPrefixed && !right.Span.StartsWith(Symbol.InternalPrefix + Symbol.InternalPrefix, StringComparison.Ordinal);
        // Go's internal sentinel is byte FE, after every valid UTF-8 name. A
        // doubled C# prefix instead denotes a user name and is unescaped once.
        if (leftInternal != rightInternal)
            return leftInternal ? 1 : -1;
        return CompareText(left.Span.Slice(leftPrefixed ? 1 : 0), right.Span.Slice(rightPrefixed ? 1 : 0));
    }

    internal static int CompareText(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        int prefix = left.CommonPrefixLength(right);
        // A shared high surrogate may be paired on only one side of the first difference.
        if (prefix > 0 && char.IsHighSurrogate(left[prefix - 1]))
            prefix--;
        int a = prefix, b = prefix;
        while (a < left.Length && b < right.Length)
        {
            int x = Next(left, ref a), y = Next(right, ref b);
            if (x != y)
                return x.CompareTo(y);
        }
        return (left.Length - a).CompareTo(right.Length - b);

        static int Next(ReadOnlySpan<char> text, ref int index)
        {
            char first = text[index++];
            return char.IsHighSurrogate(first) && index < text.Length && char.IsLowSurrogate(text[index])
                ? char.ConvertToUtf32(first, text[index++]) : first;
        }
    }
}
