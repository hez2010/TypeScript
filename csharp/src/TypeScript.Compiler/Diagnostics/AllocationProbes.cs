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
    internal const int Count = 30;

    internal static readonly string[] Names =
    [
        "typeFromNode", "typeLiteral", "resolveMembers", "setMembers", "membersTable",
        "widen", "widenObject", "relationCompare", "relationRelated", "objectLiteral", "variableType",
        "objectProperty", "objectType", "objectRegular", "symbolTypesGet", "linkGet",
        "objectPropertyInit", "objectPropertyBook", "checkerRelated",
        "objectMutable", "objectCheckExpr", "relationProperties", "relationExcess",
        "mutableConst", "mutableContextual", "mutableWiden",
        "ctxElement", "ctxProperties", "propertyLookup", "ctxApparent",
    ];
}
