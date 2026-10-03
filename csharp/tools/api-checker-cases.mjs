export function checkerCases() {
    const ref = name => ({ $ref: name });
    const request = (method, params = {}, save) => ({ method, params, ...(save ? { save } : {}) });
    const context = { snapshot: ref("snapshot.snapshot"), project: ref("snapshot.operation.createdPrograms.0") };
    const properties = new Set(["getParentOfSymbol", "getExportSymbolOfSymbol", "getSymbolOfType", "getAliasSymbolOfType", "getAliasTypeArgumentsOfType",
        "getFreshTypeOfType", "getRegularTypeOfType", "getTypeParametersOfSignature", "getParametersOfSignature", "getThisParameterOfSignature",
        "getTargetOfSignature", "getReturnTypeOfSignature", "getTypeParametersOfType", "getOuterTypeParametersOfType", "getLocalTypeParametersOfType", "getThisTypeOfType",
        "getTargetOfType", "getTypesOfType", "getCheckTypeOfType", "getExtendsTypeOfType", "getObjectTypeOfType", "getIndexTypeOfType",
        "getMembersOfSymbol", "getExportsOfSymbol", "getNonNullableType", "getApparentPropertiesOfType", "getApparentType", "getReducedType",
        "getConstraintOfTypeParameter", "getDefaultFromTypeParameter", "getTrueTypeOfConditionalType", "getFalseTypeOfConditionalType"]);
    const query = (method, params = {}, save) => request(method, { ...context, ...(properties.has(method)
        ? { objectId: params.type ?? params.symbol ?? params.signature } : params) }, save);
    const symbol = { symbol: ref("exports.0.id") };
    const type = { type: ref("declared.id") };
    const signatureSyntax = [query("getSignaturesOfType", { type: ref("value.id"), kind: 0 }, "calls"),
        ...[undefined, ref("calls.0.declaration"), ref("module.declarations.0")].flatMap(location =>
            [0, 4, 1024, 0x2000000, 0x10000000].flatMap(flags => [
                query("typeToTypeNode", { type: ref("value.id"), location, flags }),
                query("signatureToSignatureDeclaration", { signature: ref("calls.0.id"), kind: 185, location, flags }),
            ])), query("getSourceFile", { file: "/p/main.ts" })];
    function scenario(name, text, extra = []) {
        return { name: `checker-${name}`, files: { "/p/main.ts": text }, requests: [
            request("createSnapshot", { createPrograms: [{ rootFiles: ["/p/main.ts"], compilerOptions: { noLib: true, strict: true } }] }, "snapshot"),
            query("getSymbolOfSourceFile", { file: "/p/main.ts" }, "module"),
            query("getExportsOfModule", { symbol: ref("module.id") }, "exports"),
            query("getSymbolAtLocation", { location: ref("exports.0.declarations.0") }),
            query("getSymbolsAtLocations", { locations: [ref("exports.0.declarations.0"), ref("exports.0.declarations.0")] }),
            query("getTypeAtLocation", { location: ref("exports.0.declarations.0") }, "at"),
            query("getTypeAtLocations", { locations: [ref("exports.0.declarations.0"), ref("exports.0.declarations.0")] }),
            query("getTypeOfSymbol", symbol, "value"), query("getDeclaredTypeOfSymbol", symbol, "declared"),
            query("getNonMissingTypeOfSymbol", symbol), query("getTypesOfSymbols", { symbols: [ref("exports.0.id"), ref("exports.0.id")] }),
            query("getParentOfSymbol", symbol), query("getExportSymbolOfSymbol", symbol),
            query("getSymbolOfType", type), query("getAliasSymbolOfType", type), query("getAliasTypeArgumentsOfType", type),
            query("getSymbolsOfSourceFiles", { files: ["/p/main.ts", { uri: "file:///p/main.ts" }] }),
            query("getSymbolsAtPositions", { file: "/p/main.ts", positions: Array.from({ length: Buffer.byteLength(text) + 1 }, (_, i) => i) }),
            query("getTypesAtPositions", { file: "/p/main.ts", positions: Array.from({ length: Buffer.byteLength(text) + 1 }, (_, i) => i) }),
            query("getSymbolAtPosition", { file: "/p/main.ts", position: 0 }),
            query("getTypeAtPosition", { file: "/p/main.ts", position: Buffer.byteLength(text) }),
            query("resolveName", { location: ref("exports.0.declarations.0"), name: ref("exports.0.name"), meaning: 0x7fffffff }),
            query("getSymbolsInScope", { location: ref("exports.0.declarations.0"), meaning: 0x7fffffff }),
            query("getMembersOfSymbol", symbol), query("getExportsOfSymbol", { symbol: ref("module.id") }),
            ...["Any", "String", "Number", "Boolean", "Void", "Undefined", "Null", "Never", "Unknown", "BigInt", "ESSymbol", "NonPrimitive"].map(name => query(`get${name}Type`, {}, name)),
            query("getWellKnownSymbols", {}, "knownSymbols"), query("getWellKnownSignatures", {}, "knownSignatures"),
            query("getTypeOfSymbol", { symbol: ref("knownSymbols.undefined") }),
            query("getTypeOfSymbol", { symbol: ref("knownSymbols.arguments") }),
            query("getTypeOfSymbol", { symbol: ref("knownSymbols.unknown") }),
            query("getReturnTypeOfSignature", { signature: ref("knownSignatures.unknown") }),
            query("getTypePredicateOfSignature", { signature: ref("knownSignatures.unknown") }),
            query("getWellKnownSymbols"), query("getWellKnownSignatures"),
            query("getFullyQualifiedName", symbol), query("getTargetSymbol", symbol), query("getExportSymbolOfSymbolForChecker", symbol),
            query("getMemberInModuleExports", { symbol: ref("module.id"), name: ref("exports.0.name") }),
            query("getMemberInModuleExports", { symbol: ref("module.id"), name: "missing" }),
            query("getReferencesToSymbolInFile", { ...symbol, file: "/p/main.ts" }),
            query("isReadonlySymbol", symbol), query("isArrayType", type), query("isArrayLikeType", type),
            query("isTypeAssignableTo", { source: ref("value.id"), target: ref("Any.id") }),
            query("isTypeAssignableTo", { source: ref("value.id"), target: ref("Never.id") }),
            query("getPropertiesOfType", type), query("getApparentPropertiesOfType", type),
            query("getApparentType", type), query("getReducedType", type), query("getNonNullableType", type),
            query("getBaseTypeOfLiteralType", { type: ref("value.id") }), query("getWidenedType", { type: ref("value.id") }),
            query("getAwaitedType", type), query("getBaseConstraintOfType", type), query("getIndexInfosOfType", type),
            query("getIndexInfoOfType", { ...type, kind: 0 }), query("getIndexInfoOfType", { ...type, kind: 1 }),
            query("getIndexTypeOfTypeByKind", { ...type, kind: 0 }), query("getIndexTypeOfTypeByKind", { ...type, kind: 1 }),
            query("getIndexInfoOfType", { ...type, kind: 2 }), query("getIndexInfoOfType", { ...type, kind: -1 }),
            query("getPropertyOfType", { ...type, name: "x" }), query("getTypeOfPropertyOfType", { ...type, name: "x" }),
            query("getPropertyOfType", { ...type, name: "missing" }), query("getTypeOfPropertyOfType", { ...type, name: "missing" }),
            query("typeToString", type), query("typeToString", { type: ref("value.id") }),
            query("typeToString", { ...type, location: ref("exports.0.declarations.0"), flags: 1 }),
            query("typeToTypeNode", type), query("typeToTypeNode", { type: ref("value.id") }),
            query("typeToTypeNode", { ...type, location: ref("exports.0.declarations.0"), flags: 1 }),
            query("getSymbolsInScope", { meaning: 0x7fffffff }),
            query("getSymbolAtPosition", { file: { uri: "file:///missing.ts" }, position: 1 }),
            ...extra,
            query("getDeclaredTypeOfSymbol", symbol),
            query("getTypeOfSymbol", { symbol: 0 }), query("getSignaturesOfType", { type: 0, kind: 0 }),
            query("getTargetOfSignature", { signature: 0 }),
            query("getSymbolOfSourceFile", { file: "/missing.ts" }),
            request("release", { snapshot: ref("snapshot.snapshot") }),
            query("getDeclaredTypeOfSymbol", symbol),
        ] };
    }
    return [
        scenario("literal", 'export const x = "😀日本語" as const;', [
            query("getFreshTypeOfType", { type: ref("value.id") }), query("getRegularTypeOfType", { type: ref("value.id") }),
        ]),
        scenario("bigint", "export const x = -123456789123456789n;", [
            query("getFreshTypeOfType", { type: ref("value.id") }), query("getRegularTypeOfType", { type: ref("value.id") }),
        ]),
        scenario("function", "export function f<T extends string>(this: {p:number}, x: T, y?: number): T { return x; }", [
            query("getSignaturesOfType", { type: ref("value.id"), kind: 0 }, "calls"),
            query("getTypeParametersOfSignature", { signature: ref("calls.0.id") }, "typeParameters"),
            query("getConstraintOfTypeParameter", { type: ref("typeParameters.0.id") }),
            query("getDefaultFromTypeParameter", { type: ref("typeParameters.0.id") }),
            query("getParametersOfSignature", { signature: ref("calls.0.id") }),
            query("getThisParameterOfSignature", { signature: ref("calls.0.id") }),
            query("getTargetOfSignature", { signature: ref("calls.0.id") }),
            query("getReturnTypeOfSignature", { signature: ref("calls.0.id") }),
            query("getRestTypeOfSignature", { signature: ref("calls.0.id") }),
            query("getTypePredicateOfSignature", { signature: ref("calls.0.id") }),
            ...[-1, 0, 1, 2, 9].flatMap(index => [query("getParameterType", { signature: ref("calls.0.id"), index }),
                query("getTypeParameterAtPosition", { signature: ref("calls.0.id"), index })]),
            query("getSignatureFromDeclaration", { location: ref("calls.0.declaration") }),
            query("isContextSensitive", { location: ref("calls.0.declaration") }),
            ...[174, 180, 181, 185, 186, 263, 219].flatMap(kind => [0, 1, 32].map(flags =>
                query("signatureToSignatureDeclaration", { signature: ref("calls.0.id"), kind, flags, location: ref("calls.0.declaration") }))),
        ]),
        scenario("interface", "export interface I<T> { x: T; (x: T): T; new(x: T): I<T>; }", [
            query("getTypeParametersOfType", type), query("getOuterTypeParametersOfType", type),
            query("getLocalTypeParametersOfType", type), query("getThisTypeOfType", type),
            query("getTargetOfType", type), query("getSignaturesOfType", { ...type, kind: 0 }),
            query("getSignaturesOfType", { ...type, kind: 1 }),
            query("getBaseTypes", type), query("getTypeArguments", type),
        ]),
        scenario("tuple", "export type Tuple = readonly [name: string, age?: number, ...rest: boolean[]];", [
            query("getTargetOfType", type, "target"), query("getTypeParametersOfType", { type: ref("target.id") }),
            query("getTypeArguments", type), query("getBaseTypes", { type: ref("target.id") }),
        ]),
        scenario("union", "export type U = 'a' | 12 | true | null | undefined;", [query("getTypesOfType", type)]),
        scenario("template", "export type Template<T extends string> = `foo${T}bar`;", [query("getTypesOfType", type)]),
        scenario("conditional", "export type C<T> = T extends string ? true : false;", [
            query("getCheckTypeOfType", type), query("getExtendsTypeOfType", type),
            query("getTrueTypeOfConditionalType", type), query("getFalseTypeOfConditionalType", type),
        ]),
        scenario("indexed", "export type C<T, K extends keyof T> = T[K];", [
            query("getObjectTypeOfType", type), query("getIndexTypeOfType", type),
        ]),
        scenario("indexes", "interface Base { z: string; } export interface I<T extends string = 'default'> extends Base { readonly [key: string]: string; [key: number]: T; readonly x: T; }", [
            query("getTypeParametersOfType", type, "typeParameters"),
            query("getConstraintOfTypeParameter", { type: ref("typeParameters.0.id") }),
            query("getDefaultFromTypeParameter", { type: ref("typeParameters.0.id") }), query("getBaseTypes", type),
            query("getPropertyOfType", { ...type, name: "x" }, "property"), query("isReadonlySymbol", { symbol: ref("property.id") }),
        ]),
        ...["x is string", "asserts x is string", "asserts x", "this is { x: string }"].map((predicate, i) =>
            scenario(`predicate-${i}`, `export function f(x: unknown): ${predicate} { throw 0; }`, [
                query("getSignaturesOfType", { type: ref("value.id"), kind: 0 }, "calls"),
                query("getTypePredicateOfSignature", { signature: ref("calls.0.id") }),
            ])),
        scenario("rest", "declare global { interface Array<T> { [n: number]: T; length: number; } } export function f(...args: string[]): void {}", [
            query("getSignaturesOfType", { type: ref("value.id"), kind: 0 }, "calls"),
            query("getRestTypeOfSignature", { signature: ref("calls.0.id") }),
            query("getParameterType", { signature: ref("calls.0.id"), index: 12 }),
        ]),
        scenario("constants", "export const enum E { A = 42, B = '😀', C = 1 / 0, D = 0 / 0 }", [
            query("getExportsOfSymbol", symbol, "members"),
            ...[0, 1, 2, 3].map(i => query("getConstantValue", { location: ref(`members.${i}.declarations.0`) })),
        ]),
        scenario("syntax-object", "export function f<T = string>(x: { a: T; b?: number }, y: [string, T]): { a: T; b?: number } { return x; }", signatureSyntax),
        scenario("syntax-reference", "type Box<T> = { value: T }; export function f<T>(x: Box<T>, y: T | null): Box<T> { return x; }", signatureSyntax),
        scenario("syntax-nested", "export function f<T>(x: (value: T) => T, a: keyof T): <T>(value: T) => T { return undefined as any; }", signatureSyntax),
        scenario("syntax-predicate", "export function f(x: unknown): x is { name: '日本語' } { return true; }", signatureSyntax),
    ];
}

// Handle numbers are process-local allocations. Preserve their bijection, project/snapshot
// scope and every edge in the response graph while comparing all other fields literally.
export function normalizeCheckerResponses(responses, requests) {
    const maps = new Map(), saved = new Map();
    function identity(domain, value) {
        if (value === 0) return 0;
        let map = maps.get(domain); if (!map) maps.set(domain, map = new Map());
        if (!map.has(value)) map.set(value, map.size + 1);
        return `${domain}:${map.get(value)}`;
    }
    function resolve(value) {
        if (Array.isArray(value)) return value.map(resolve);
        if (!value || typeof value !== "object") return value;
        if (value.$ref) { const [name, ...parts] = value.$ref.split("."); return parts.reduce((v, p) => v[p], saved.get(name)); }
        return Object.fromEntries(Object.entries(value).map(([key, child]) => [key, resolve(child)]));
    }
    return responses.map((response, i) => {
        const request = requests[i], params = resolve(request.params);
        const snapshot = identity("snapshot", params.snapshot ?? 0), scope = `${snapshot}/${params.project ?? ""}`;
        function visit(value) {
            if (Array.isArray(value)) return value.map(visit);
            if (!value || typeof value !== "object") return value;
            const kind = "id" in value && "flags" in value ? "project" in value ? "symbol" : "value" in value ? "type" : "signature" : undefined;
            function handle(domain, id) { return identity(`${domain}/${domain === "symbol" ? snapshot : scope}`, id); }
            return Object.fromEntries(Object.entries(value).sort(([a], [b]) => a.localeCompare(b)).map(([key, child]) => {
                if (key === "snapshot") return [key, identity("snapshot", child)];
                if (request.method === "getWellKnownSymbols" && ["unknown", "undefined", "arguments"].includes(key)) return [key, handle("symbol", child)];
                if (request.method === "getWellKnownSignatures" && key === "unknown") return [key, handle("signature", child)];
                if (kind && key === "id") return [key, handle(kind, child)];
                if (kind === "symbol" && key === "name" && /^__#\d+@#/.test(child))
                    return [key, child.replace(/^__#(\d+)@/, (_, id) => `__#${handle("symbol", Number(id))}@`)];
                if (kind === "symbol" && ["parent", "exportSymbol"].includes(key)) return [key, handle("symbol", child)];
                if (kind === "type") {
                    if (["symbol", "aliasSymbol"].includes(key)) return [key, handle("symbol", child)];
                    if (["target", "thisType", "freshType", "regularType", "objectType", "indexType", "checkType", "extendsType", "baseType", "substConstraint"].includes(key)) return [key, handle("type", child)];
                    if (["typeParameters", "outerTypeParameters", "localTypeParameters", "aliasTypeArguments"].includes(key)) return [key, child.map(id => handle("type", id))];
                }
                if (kind === "signature") {
                    if (key === "target") return [key, handle("signature", child)];
                    if (key === "thisParameter") return [key, handle("symbol", child)];
                    if (key === "parameters") return [key, child.map(id => handle("symbol", id))];
                    if (key === "typeParameters") return [key, child.map(id => handle("type", id))];
                }
                return [key, visit(child)];
            }));
        }
        // Go's scope query returns symbolsToArray(map), whose order changes
        // between identical process runs. All other response arrays retain order.
        const unordered = request.method === "getSymbolsInScope" && Array.isArray(response.result);
        let comparable = unordered ? { ...response, result: [...response.result].sort((a, b) =>
            Buffer.compare(Buffer.from(a.name), Buffer.from(b.name))) } : response;
        if (request.method === "getCompletionsAtPosition" && Array.isArray(response.result?.entries)) {
            const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;
            comparable = { ...response, result: { ...response.result, entries: response.result.entries.toSorted((a, b) =>
                compare(a.sortText, b.sortText) || compare(a.name, b.name)) } };
        }
        const normalized = visit(comparable);
        if (request.save && "result" in response) saved.set(request.save, response.result);
        return normalized;
    });
}
