namespace TypeScript.Compiler.Diagnostics;

/// <summary>
/// Named method-level probes used by <c>--extendedDiagnostics</c>. Frames are inclusive: a probe
/// reports every byte allocated and every tick spent while it was active, including work done by
/// nested probes, so the numbers are read top-down along the known nesting (variable type -> type
/// node -> members). Collection is gated by a static flag, so a normal compile pays one predictable
/// branch per site.
/// </summary>
internal static class AllocationProbes
{
    internal const int TypeFromNode = 0;
    internal const int TypeLiteral = 1;
    internal const int ResolveMembers = 2;
    internal const int SetMembers = 3;
    internal const int MembersTable = 4;
    internal const int Widen = 5;
    internal const int WidenObject = 6;
    internal const int RelationCompare = 7;
    internal const int RelationRelated = 8;
    internal const int ObjectLiteral = 9;
    internal const int VariableType = 10;
    internal const int ObjectProperty = 11;
    internal const int ObjectType = 12;
    internal const int ObjectRegular = 13;
    internal const int SymbolTypesGet = 14;
    internal const int LinkGet = 15;
    internal const int ObjectPropertyInit = 16;
    internal const int ObjectPropertyBook = 17;
    internal const int CheckerRelated = 18;
    internal const int ObjectMutable = 19;
    internal const int ObjectCheckExpr = 20;
    internal const int RelationProperties = 21;
    internal const int RelationExcess = 22;
    internal const int MutableConst = 23;
    internal const int MutableContextual = 24;
    internal const int MutableWiden = 25;
    internal const int CtxElement = 26;
    internal const int CtxProperties = 27;
    internal const int PropertyLookup = 28;
    internal const int CtxApparent = 29;
    // Sub-frames of checkerRelated: the normalization target adjustment, the Simple/Comparable
    // short circuits, the weak-type check and the identical-shape fast path. They split the
    // frame's exclusive bytes without changing its inclusive number.
    internal const int RelNormalize = 30;
    internal const int RelSimple = 31;
    internal const int RelWeak = 32;
    internal const int RelIdentical = 33;
    // The entry point's tail: the union/intersection dispatch and the recursive-session dispatch.
    internal const int RelUnion = 34;
    internal const int RelRecurse = 35;
    // Relation-key construction, the remaining piece inside the recursive-session dispatch.
    internal const int RelKeys = 36;
    // Inside the recursive-session body: the deep-nesting check and the structured-comparison callback.
    internal const int RecDeep = 37;
    internal const int RecStamp = 38;
    internal const int Count = 39;

    internal static readonly string[] Names =
    [
        "typeFromNode", "typeLiteral", "resolveMembers", "setMembers", "membersTable",
        "widen", "widenObject", "relationCompare", "relationRelated", "objectLiteral", "variableType",
        "objectProperty", "objectType", "objectRegular", "symbolTypesGet", "linkGet",
        "objectPropertyInit", "objectPropertyBook", "checkerRelated",
        "objectMutable", "objectCheckExpr", "relationProperties", "relationExcess",
        "mutableConst", "mutableContextual", "mutableWiden",
        "ctxElement", "ctxProperties", "propertyLookup", "ctxApparent",
        "relNormalize", "relSimple", "relWeak", "relIdentical", "relUnion", "relRecurse", "relKeys",
        "recDeep", "recStamp",
    ];
}
