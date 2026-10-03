using System.Buffers;
using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal static class ImportSorter
{
    internal static (Comparison<Utf8String> Modules, Comparison<SyntaxNode> Specifiers) OrganizeComparers(
        IReadOnlyList<ImportDeclarationNode[]> groups, IReadOnlyList<ImportDeclarationNode> imports, UserPreferences preferences)
    {
        var comparers = Comparers(preferences);
        bool automatic = ResolvedSort(preferences) == OrganizeImportsSort.Auto;
        var modules = automatic ? DetectStrings(groups.Select(group => group.Select(ModuleName).ToArray()).ToArray(), comparers).Compare : comparers[0];
        var names = comparers[0];
        var order = preferences.OrganizeImportsTypeOrder;
        if (automatic || order == OrganizeImportsTypeOrder.Auto)
        {
            var orders = order == OrganizeImportsTypeOrder.Auto ? new[] { OrganizeImportsTypeOrder.Last, OrganizeImportsTypeOrder.Inline, OrganizeImportsTypeOrder.First } : [order];
            if (DetectNamed(imports, comparers, orders) is { } detected)
            {
                if (automatic) names = detected.Compare;
                if (order == OrganizeImportsTypeOrder.Auto) order = detected.Order;
            }
        }
        return (modules, Specifiers(names, order));
    }

    internal static (Comparison<Utf8String> Compare, bool Sorted) DetectModules(IReadOnlyList<SyntaxNode> imports, UserPreferences preferences)
        => DetectStrings([imports.Select(ModuleName).ToArray()], Comparers(preferences));

    internal static (Comparison<SyntaxNode> Compare, bool? Sorted) DetectSpecifiers(ImportDeclarationNode? declaration,
        SourceFileNode? file, UserPreferences preferences)
    {
        var comparers = Comparers(preferences);
        var orders = preferences.OrganizeImportsTypeOrder != OrganizeImportsTypeOrder.Auto ? [preferences.OrganizeImportsTypeOrder]
            : new[] { OrganizeImportsTypeOrder.Last, OrganizeImportsTypeOrder.Inline, OrganizeImportsTypeOrder.First };
        var compare = Specifiers(comparers[0], preferences.OrganizeImportsTypeOrder);
        if (declaration is null || ResolvedSort(preferences) != OrganizeImportsSort.Auto && preferences.OrganizeImportsTypeOrder != OrganizeImportsTypeOrder.Auto)
            return (compare, null);
        var detected = DetectNamed([declaration], comparers, orders)
            ?? (file is null ? null : DetectNamed((file.Statements ?? new([])).OfType<ImportDeclarationNode>().ToArray(), comparers, orders));
        return detected is { } result ? (Specifiers(result.Compare, result.Order), result.Sorted) : (compare, null);
    }

    private static (Comparison<Utf8String> Compare, OrganizeImportsTypeOrder Order, bool Sorted)? DetectNamed(
        IReadOnlyList<ImportDeclarationNode> imports, IReadOnlyList<Comparison<Utf8String>> comparers, IReadOnlyList<OrganizeImportsTypeOrder> orders)
    {
        var groups = imports.Select(import => import.ImportClause?.NamedBindings).OfType<NamedImportsNode>()
            .Where(names => names.Elements is { Count: > 0 }).Select(names => names.Elements!).ToArray();
        if (groups.Length == 0) return null;
        bool mixed = groups.Any(group => group.Any(TypeOnly) && group.Any(node => !TypeOnly(node)));
        if (!mixed)
        {
            var strings = DetectStrings(groups.Select(group => group.Select(Name).ToArray()).ToArray(), comparers);
            return (strings.Compare, orders.Count == 1 ? orders[0] : OrganizeImportsTypeOrder.Last, strings.Sorted);
        }
        int best = int.MaxValue;
        var bestCompare = comparers[0];
        var bestOrder = orders[0];
        foreach (var order in orders)
            foreach (var compare in comparers)
            {
                var specifierCompare = Specifiers(compare, order);
                int difference = groups.Sum(group => Disorder(group, specifierCompare));
                if (difference >= best) continue;
                best = difference; bestCompare = compare; bestOrder = order;
            }
        return (bestCompare, bestOrder, best == 0);
    }

    private static (Comparison<Utf8String> Compare, bool Sorted) DetectStrings(IReadOnlyList<Utf8String[]> groups, IReadOnlyList<Comparison<Utf8String>> comparers)
    {
        int best = int.MaxValue;
        var selected = comparers[0];
        foreach (var compare in comparers)
        {
            int difference = groups.Sum(group => Disorder(group, compare));
            if (difference >= best) continue;
            best = difference; selected = compare;
        }
        return (selected, best == 0);
    }

    internal static int Disorder<T>(IReadOnlyList<T> list, Comparison<T> compare)
    {
        int result = 0;
        for (int i = 1; i < list.Count; i++) if (compare(list[i - 1], list[i]) > 0) result++;
        return result;
    }

    internal static int InsertionIndex<T>(IReadOnlyList<T> list, T value, Comparison<T> compare)
    {
        int start = 0, end = list.Count;
        while (start < end)
        {
            int mid = start + (end - start) / 2;
            int result = compare(list[mid], value);
            if (result < 0) start = mid + 1; else if (result > 0) end = mid; else return mid;
        }
        return start;
    }

    internal static int CompareDeclarations(SyntaxNode first, SyntaxNode second, Comparison<Utf8String> compare)
    {
        var left = ModuleName(first); var right = ModuleName(second);
        int result = left.IsEmpty.CompareTo(right.IsEmpty);
        if (result != 0) return result;
        result = Relative(left).CompareTo(Relative(right));
        if (result != 0) return result;
        result = compare(left, right);
        return result != 0 ? result : ImportOrder(first).CompareTo(ImportOrder(second));
    }

    internal static Utf8String ModuleName(SyntaxNode node)
    {
        var expression = node switch
        {
            ImportDeclarationNode import => import.ModuleSpecifier,
            ExportDeclarationNode export => export.ModuleSpecifier,
            ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode module } => module.Expression,
            VariableStatementNode { DeclarationList: VariableDeclarationListNode { Declarations: { Count: > 0 } declarations } }
                when declarations[0] is VariableDeclarationNode { Initializer: CallExpressionNode { Arguments: { Count: > 0 } arguments } } => arguments[0],
            _ => null,
        };
        return expression switch { StringLiteralNode value => value.Text, NoSubstitutionTemplateLiteralNode value => value.Text, _ => default };
    }

    private static int ImportOrder(SyntaxNode node) => node switch
    {
        ImportDeclarationNode { ImportClause: null } => 0,
        ImportDeclarationNode { ImportClause.PhaseModifier: K.TypeKeyword } => 1,
        ImportDeclarationNode { ImportClause.NamedBindings: NamespaceImportNode } => 2,
        ImportDeclarationNode { ImportClause.Name: not null } => 3,
        ImportDeclarationNode => 4,
        ImportEqualsDeclarationNode => 5,
        VariableStatementNode => 6,
        _ => 7,
    };

    private static Utf8String Name(SyntaxNode node) => node.DeclarationName switch
        { IdentifierNode name => name.Text, StringLiteralNode name => name.Text, _ => default };
    private static bool TypeOnly(SyntaxNode node) => node is ImportSpecifierNode { IsTypeOnly: true } or ExportSpecifierNode { IsTypeOnly: true };
    internal static bool Relative(Utf8String name) => name == "."u8 || name == ".."u8 || name.StartsWith("./"u8) || name.StartsWith("../"u8) || name.StartsWith("/"u8);

    private static Comparison<SyntaxNode> Specifiers(Comparison<Utf8String> compare, OrganizeImportsTypeOrder order) => (left, right) =>
    {
        int typeOrder = order == OrganizeImportsTypeOrder.Inline ? 0 : order == OrganizeImportsTypeOrder.First
            ? TypeOnly(right).CompareTo(TypeOnly(left)) : TypeOnly(left).CompareTo(TypeOnly(right));
        return typeOrder != 0 ? typeOrder : compare(Name(left), Name(right));
    };

    private static OrganizeImportsSort ResolvedSort(UserPreferences preferences) => preferences.OrganizeImportsSort != OrganizeImportsSort.Auto
        ? preferences.OrganizeImportsSort : preferences.OrganizeImportsIgnoreCase is not { } ignoreCase ? OrganizeImportsSort.Auto
        : preferences.OrganizeImportsUnicodeCollation ? ignoreCase ? OrganizeImportsSort.NaturalIgnoreCase : OrganizeImportsSort.Natural
        : ignoreCase ? OrganizeImportsSort.OrdinalIgnoreCase : OrganizeImportsSort.Ordinal;

    private static Comparison<Utf8String>[] Comparers(UserPreferences preferences)
        => preferences.OrganizeImportsSort != OrganizeImportsSort.Auto || preferences.OrganizeImportsIgnoreCase is not null
            ? [Comparer(preferences, preferences.OrganizeImportsIgnoreCase == true)] : [Comparer(preferences, true), Comparer(preferences, false)];

    private static Comparison<Utf8String> Comparer(UserPreferences preferences, bool ignoreCase) => preferences.OrganizeImportsSort switch
    {
        OrganizeImportsSort.Ordinal => static (a, b) => a.CompareTo(b),
        OrganizeImportsSort.OrdinalIgnoreCase => static (a, b) => GoUnicode.LowerText(a).CompareTo(GoUnicode.LowerText(b)),
        OrganizeImportsSort.Natural => static (a, b) => Natural(a, b, true),
        OrganizeImportsSort.NaturalIgnoreCase => static (a, b) => Natural(a, b, false),
        _ => preferences.OrganizeImportsUnicodeCollation ? (a, b) => Unicode(a, b, ignoreCase, preferences)
            : ignoreCase ? static (a, b) => GoUnicode.LowerText(a).CompareTo(GoUnicode.LowerText(b)) : static (a, b) => a.CompareTo(b),
    };

    private static int Natural(Utf8String left, Utf8String right, bool caseSensitive)
    {
        int result = Numeric(GoUnicode.NaturalSortKey(left), GoUnicode.NaturalSortKey(right));
        if (result != 0) return result;
        if (caseSensitive && (result = Case(left, right, OrganizeImportsCaseFirst.Upper)) != 0) return result;
        return left.CompareTo(right);
    }

    private static int Unicode(Utf8String left, Utf8String right, bool ignoreCase, UserPreferences preferences)
    {
        int Compare(Utf8String a, Utf8String b) => preferences.OrganizeImportsNumericCollation == true ? Numeric(a, b) : a.CompareTo(b);
        int result = Compare(GoUnicode.NaturalSortKey(left), GoUnicode.NaturalSortKey(right));
        if (result != 0) return result;
        if (preferences.OrganizeImportsAccentCollation != false && (result = Compare(GoUnicode.LowerText(left), GoUnicode.LowerText(right))) != 0) return result;
        if (!ignoreCase && (result = Case(left, right, preferences.OrganizeImportsCaseFirst)) != 0) return result;
        return left.CompareTo(right);
    }

    private static int Numeric(Utf8String left, Utf8String right)
    {
        int a = 0, b = 0;
        while (a < left.Length && b < right.Length)
        {
            if (Digit(left[a]) && Digit(right[b]))
            {
                int aEnd = a, bEnd = b;
                while (aEnd < left.Length && Digit(left[aEnd])) aEnd++;
                while (bEnd < right.Length && Digit(right[bEnd])) bEnd++;
                int aStart = a, bStart = b;
                while (aStart < aEnd - 1 && left[aStart] == '0') aStart++;
                while (bStart < bEnd - 1 && right[bStart] == '0') bStart++;
                int result = (aEnd - aStart).CompareTo(bEnd - bStart);
                if (result == 0) result = left[aStart..aEnd].CompareTo(right[bStart..bEnd]);
                if (result == 0) result = left[a..aEnd].CompareTo(right[b..bEnd]);
                if (result != 0) return result;
                a = aEnd; b = bEnd; continue;
            }
            if (Rune.DecodeFromUtf8(left.Span[a..], out var aPoint, out int aWidth) != OperationStatus.Done) { aPoint = Rune.ReplacementChar; aWidth = 1; }
            if (Rune.DecodeFromUtf8(right.Span[b..], out var bPoint, out int bWidth) != OperationStatus.Done) { bPoint = Rune.ReplacementChar; bWidth = 1; }
            if (aPoint != bPoint) return aPoint.Value.CompareTo(bPoint.Value);
            a += aWidth; b += bWidth;
        }
        return (left.Length - a).CompareTo(right.Length - b);
    }

    private static int Case(Utf8String left, Utf8String right, OrganizeImportsCaseFirst order)
    {
        var a = GoUnicode.Runes(left); var b = GoUnicode.Runes(right);
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool aUpper = GoUnicode.IsUpper(a[i]), bUpper = GoUnicode.IsUpper(b[i]);
            if (aUpper != bUpper) return order == OrganizeImportsCaseFirst.Upper ? bUpper.CompareTo(aUpper) : aUpper.CompareTo(bUpper);
        }
        return a.Length.CompareTo(b.Length);
    }

    private static bool Digit(byte value) => value is >= (byte)'0' and <= (byte)'9';
}
