using System.Buffers;
using System.Numerics;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.Diagnostics;

internal sealed partial class CompilationTrace
{
    private static readonly (TypeFlags Flag, Utf8String Name)[] flagNames = Enum.GetValues<TypeFlags>().Distinct()
        .Where(flag => BitOperations.IsPow2((uint)flag)).Select(flag => (flag, Utf8String.FromString(flag.ToString()))).ToArray();

    private async ValueTask WriteTypesAsync(TypeRecorder recorder)
    {
        int displayCount = recorder.Types.Count;
        var identities = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        Utf8String path = TypesPath(recorder.Index);
        fileSystem.WriteFile(path, "["u8);
        var data = new ArrayBufferWriter<byte>();
        // Formatting can materialize types referenced by later descriptors. Include
        // those records too, without recursively formatting the newly created types.
        for (int index = 0; index < recorder.Types.Count; index++)
        {
            var type = recorder.Types[index];
            Utf8String display = default;
            if (index < displayCount && recorder.Checker is { } checker && ((type.ObjectFlags & ObjectFlags.Anonymous) != 0
                || (type.Flags & (TypeFlags.Literal | TypeFlags.TemplateLiteral | TypeFlags.UnionOrIntersection)) != 0))
            {
                try { display = await checker.GetTypeDisplayAsync(type).ConfigureAwait(false); }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException or KeyNotFoundException or NullReferenceException)
                { /* Incomplete types still have useful identities and flags; the reference also omits their display. */ }
            }
            if (index != 0) data.Write(",\n"u8);
            using (var writer = new Utf8JsonWriter(data)) WriteType(writer, type, display, recorder.Checker, identities);
            if (data.WrittenCount >= FlushThreshold)
            { fileSystem.AppendFile(path, data.WrittenSpan); data.Clear(); }
        }
        data.Write("]\n"u8); fileSystem.AppendFile(path, data.WrittenSpan);
    }

    private static void WriteType(Utf8JsonWriter writer, Type type, Utf8String display, Checker? checker, Dictionary<object, int> identities)
    {
        writer.WriteStartObject(); writer.WriteNumber("id"u8, type.Id);
        object identity = TypeRecursion.FromTarget(type).Value;
        if (!identities.TryGetValue(identity, out int recursion)) identities.Add(identity, recursion = identities.Count);
        writer.WriteNumber("recursionId"u8, recursion);
        if (type is IntrinsicType intrinsic && !intrinsic.IntrinsicName.IsEmpty) String(writer, "intrinsicName"u8, intrinsic.IntrinsicName);
        var symbol = type.Alias?.Symbol ?? type.Symbol;
        if (symbol is not null && !symbol.Name.IsEmpty) String(writer, "symbolName"u8, symbol.Name.Replace(Symbol.InternalPrefix.Span, "__"u8));
        if (type is TupleType) writer.WriteBoolean("isTuple"u8, true);
        if (type is UnionType union) Types("unionTypes"u8, union.Types);
        if (type is IntersectionType intersection) Types("intersectionTypes"u8, intersection.Types);
        if (type.Alias is { } alias) Types("aliasTypeArguments"u8, alias.TypeArguments);
        if (type is IndexType index) Id("keyofType"u8, index.Target);
        if (type is IndexedAccessType access)
        { Id("indexedAccessObjectType"u8, access.ObjectType); Id("indexedAccessIndexType"u8, access.IndexType); }
        if (type is ConditionalType conditional)
        {
            Id("conditionalCheckType"u8, conditional.CheckType); Id("conditionalExtendsType"u8, conditional.ExtendsType);
            writer.WriteNumber("conditionalTrueType"u8, conditional.ResolvedTrueType is { } yes ? (long)yes.Id : -1);
            writer.WriteNumber("conditionalFalseType"u8, conditional.ResolvedFalseType is { } no ? (long)no.Id : -1);
        }
        if (type is SubstitutionType substitution)
        { Id("substitutionBaseType"u8, substitution.BaseType); Id("constraintType"u8, substitution.Constraint); }
        if (type is TypeReference reference)
        {
            Id("instantiatedType"u8, reference.Target);
            if (reference.ResolvedTypeArguments is { } arguments) Types("typeArguments"u8, arguments);
            Location(writer, "referenceLocation"u8, reference.Node);
        }
        if (type is ReverseMappedType reverse)
        {
            Id("reverseMappedSourceType"u8, reverse.Source); Id("reverseMappedMappedType"u8, reverse.MappedType);
            Id("reverseMappedConstraintType"u8, reverse.ConstraintType);
        }
        if (type is EvolvingArrayType array)
        { Id("evolvingArrayElementType"u8, array.ElementType); Id("evolvingArrayFinalType"u8, array.FinalArrayType); }
        if (checker?.InferencePatterns.TryGetValue(type, out var pattern) == true) Location(writer, "destructuringPattern"u8, pattern);
        Location(writer, "firstDeclaration"u8, symbol?.Declarations.FirstOrDefault());
        writer.WriteStartArray("flags"u8);
        foreach (var entry in flagNames) if ((type.Flags & entry.Flag) != 0) Configuration.JsonStrings.WriteString(writer, entry.Name.Span);
        writer.WriteEndArray();
        if (!display.IsEmpty) String(writer, "display"u8, display);
        writer.WriteEndObject();

        void Id(ReadOnlySpan<byte> name, Type? value) { if (value is not null) writer.WriteNumber(name, value.Id); }
        void Types(ReadOnlySpan<byte> name, IReadOnlyList<Type> values)
        {
            if (values.Count == 0) return;
            writer.WriteStartArray(name); foreach (var value in values) writer.WriteNumberValue(value.Id); writer.WriteEndArray();
        }
    }

    private static void Location(Utf8JsonWriter writer, ReadOnlySpan<byte> property, SyntaxNode? node)
    {
        if (node is null || SemanticSyntax.Source(node) is not { } file) return;
        var scanner = new Scanner(file.Source);
        int start = scanner.SkipTriviaAt(Math.Clamp(node.Pos, 0, file.Source.Length));
        writer.WriteStartObject(property); String(writer, "path"u8, CompilerPath.Normalize(file.FileName).ToLowerInvariant());
        Position("start"u8, start); Position("end"u8, Math.Clamp(node.End, 0, file.Source.Length));
        writer.WriteEndObject();
        void Position(ReadOnlySpan<byte> name, int offset)
        {
            var (line, _) = file.Source.GetLineAndCharacter(offset);
            int units = 0, cursor = file.Source.LineStarts[line];
            while (cursor < offset)
            { int point = Wtf8.Decode(file.Source.Text.Span[cursor..offset], out int width); units += point >= 0x10000 ? 2 : 1; cursor += width; }
            writer.WriteStartObject(name); writer.WriteNumber("line"u8, line + 1); writer.WriteNumber("character"u8, units + 1); writer.WriteEndObject();
        }
    }
}
