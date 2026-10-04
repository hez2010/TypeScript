import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { root } from "./common.mjs";
const __dirname = root;
const check = process.argv.includes("--check");
function writeGenerated(file, source) {
    const formatted = execFileSync(process.execPath, [path.join(root, "node_modules/dprint/bin.cjs"), "fmt", "--stdin", "ts"], { cwd: root, input: source, encoding: "utf8", windowsHide: true });
    if (check) assert.equal(fs.readFileSync(file, "utf8"), formatted, `Stale generated enum: ${file}`);
    else fs.writeFileSync(file, formatted);
}
// ── Enum generation from Go source ──────────────────────────────

/**
 * @typedef {{
 *   name: string;
 *   goPrefix: string;
 *   goFile: string;
 *   outDir: string;
 *   stringEnum?: boolean;
 *   excludeMembers?: readonly string[];
 *   valueReplacements?: Record<string, string>;
 * }} EnumDef
 */

/** @type {EnumDef[]} */
const enumDefs = [
    { name: "SymbolFlags", goPrefix: "SymbolFlags", goFile: "tsc/internal/ast/symbolflags.go", outDir: "packages/typescript/src/enums" },
    { name: "CheckFlags", goPrefix: "CheckFlags", goFile: "tsc/internal/ast/checkflags.go", outDir: "packages/typescript/src/enums" },
    { name: "TypeFlags", goPrefix: "TypeFlags", goFile: "tsc/internal/checker/types.go", outDir: "packages/typescript/src/enums" },
    { name: "ObjectFlags", goPrefix: "ObjectFlags", goFile: "tsc/internal/checker/types.go", outDir: "packages/typescript/src/enums" },
    { name: "SignatureFlags", goPrefix: "SignatureFlags", goFile: "tsc/internal/checker/types.go", outDir: "packages/typescript/src/enums" },
    { name: "SignatureKind", goPrefix: "SignatureKind", goFile: "tsc/internal/checker/types.go", outDir: "packages/typescript/src/enums" },
    { name: "IndexKind", goPrefix: "IndexKind", goFile: "tsc/internal/checker/types.go", outDir: "packages/typescript/src/enums" },
    { name: "ElementFlags", goPrefix: "ElementFlags", goFile: "tsc/internal/checker/types.go", outDir: "packages/typescript/src/enums" },
    { name: "TypePredicateKind", goPrefix: "TypePredicateKind", goFile: "tsc/internal/checker/types.go", outDir: "packages/typescript/src/enums" },
    { name: "TypeFormatFlags", goPrefix: "TypeFormatFlags", goFile: "tsc/internal/checker/types.go", outDir: "packages/typescript/src/enums" },
    { name: "DiagnosticCategory", goPrefix: "Category", goFile: "tsc/internal/diagnostics/diagnostics.go", outDir: "packages/typescript/src/enums" },
    { name: "SyntaxKind", goPrefix: "Kind", goFile: "tsc/internal/ast/kind_generated.go", outDir: "packages/typescript/src/enums" },
    { name: "NodeFlags", goPrefix: "NodeFlags", goFile: "tsc/internal/ast/nodeflags.go", outDir: "packages/typescript/src/enums" },
    { name: "OuterExpressionKinds", goPrefix: "OEK", goFile: "tsc/internal/ast/utilities.go", outDir: "packages/typescript/src/enums" },
    { name: "ModifierFlags", goPrefix: "ModifierFlags", goFile: "tsc/internal/ast/modifierflags.go", outDir: "packages/typescript/src/enums" },
    { name: "ModuleKind", goPrefix: "ModuleKind", goFile: "tsc/internal/core/compileroptions.go", outDir: "packages/typescript/src/enums" },
    { name: "ModuleResolutionKind", goPrefix: "ModuleResolutionKind", goFile: "tsc/internal/core/compileroptions.go", outDir: "packages/typescript/src/enums" },
    { name: "ModuleDetectionKind", goPrefix: "ModuleDetectionKind", goFile: "tsc/internal/core/compileroptions.go", outDir: "packages/typescript/src/enums" },
    { name: "NewLineKind", goPrefix: "NewLineKind", goFile: "tsc/internal/core/compileroptions.go", outDir: "packages/typescript/src/enums" },
    { name: "JsxEmit", goPrefix: "JsxEmit", goFile: "tsc/internal/core/compileroptions.go", outDir: "packages/typescript/src/enums" },
    { name: "ScriptKind", goPrefix: "ScriptKind", goFile: "tsc/internal/core/scriptkind.go", outDir: "packages/typescript/src/enums" },
    { name: "TokenFlags", goPrefix: "TokenFlags", goFile: "tsc/internal/ast/tokenflags.go", outDir: "packages/typescript/src/enums" },
    { name: "DiagnosticDirectivePolicy", goPrefix: "MappedDiagnosticDirectivePolicy", goFile: "tsc/internal/ast/ast.go", outDir: "packages/typescript/src/enums" },
    { name: "SpanMapKind", goPrefix: "Kind", goFile: "tsc/internal/spanmap/spanmap.go", outDir: "packages/typescript/src/enums" },
    { name: "SpanMapFidelity", goPrefix: "Fidelity", goFile: "tsc/internal/spanmap/spanmap.go", outDir: "packages/typescript/src/enums" },
    { name: "SpanMapFeature", goPrefix: "Feature", goFile: "tsc/internal/spanmap/spanmap.go", outDir: "packages/typescript/src/enums" },
    { name: "NodeBuilderFlags", goPrefix: "Flags", goFile: "tsc/internal/nodebuilder/types.go", outDir: "packages/typescript/src/enums" },
    { name: "CompletionItemKind", goPrefix: "CompletionItemKind", goFile: "tsc/internal/lsp/lsproto/lsp_generated.go", outDir: "packages/typescript/src/enums" },
    { name: "EmitOnly", goPrefix: "Emit", goFile: "tsc/internal/compiler/emitter.go", outDir: "packages/typescript/src/enums", excludeMembers: ["OnlyBuilderSignature"] },
    // String enum: Go stores internal names with a "\xFE" sentinel prefix, but the escaped
    // form sent over the wire uses "__" (see EscapeSymbolName), so map the sentinel accordingly.
    { name: "InternalSymbolName", goPrefix: "InternalSymbolName", goFile: "tsc/internal/ast/symbol.go", outDir: "packages/typescript/src/enums", stringEnum: true, valueReplacements: { InternalSymbolNamePrefix: "__" } },
];

/**
 * @param {string} block
 * @param {EnumDef} def
 * @returns {EnumMember[]}
 */
function parseGoConstBlock(block, def) {
    const prefix = def.goPrefix;
    const members = [];
    let iotaCounter = 0;
    let iotaExpression;

    const lines = block.split("\n");
    let i = 0;

    while (i < lines.length) {
        const rawLine = lines[i];
        const line = rawLine.replace(/\/\/.*$/, "").trim();

        if (!line) {
            i++;
            continue;
        }

        // Match: PrefixName Type = value  or  PrefixName = value
        const fullMatch = line.match(new RegExp(`^(${prefix}\\w+)\\s+(?:\\S+\\s*)?=\\s*(.+)$`));
        // Match bare iota continuation: just PrefixName
        const bareMatch = !fullMatch && iotaExpression !== undefined
            ? line.match(new RegExp(`^(${prefix}\\w+)$`))
            : null;

        if (!fullMatch && !bareMatch) {
            i++;
            continue;
        }

        const goName = fullMatch ? fullMatch[1] : /** @type {RegExpMatchArray} */ (bareMatch)[1];
        let goValue = fullMatch ? fullMatch[2].trim() : "";
        const memberName = goName.slice(prefix.length);

        // Accumulate continuation lines ending with |
        i++;
        while (i < lines.length && goValue.endsWith("|")) {
            const nextRaw = lines[i];
            const nextLine = nextRaw.replace(/\/\/.*$/, "").trim();
            if (!nextLine) {
                i++;
                continue;
            }
            goValue += " " + nextLine;
            i++;
        }

        let tsValue;
        if (def.stringEnum) {
            tsValue = parseGoStringValue(goValue, def.valueReplacements ?? {});
        }
        else {
            let numericValue = goValue;
            if (goValue.includes("iota")) {
                iotaExpression = goValue;
                numericValue = goValue.replace(/\biota\b/g, String(iotaCounter));
            }
            else if (iotaExpression !== undefined && goValue === "") {
                numericValue = iotaExpression.replace(/\biota\b/g, String(iotaCounter));
            }
            tsValue = translateGoNumericExpression(numericValue, prefix);
        }

        members.push({ name: memberName, value: tsValue });
        iotaCounter++;
    }

    return members;
}

const goBinaryPrecedence = new Map([
    ["||", 1],
    ["&&", 2],
    ["==", 3],
    ["!=", 3],
    ["<", 3],
    ["<=", 3],
    [">", 3],
    [">=", 3],
    ["+", 4],
    ["-", 4],
    ["|", 4],
    ["^", 4],
    ["*", 5],
    ["/", 5],
    ["%", 5],
    ["<<", 5],
    [">>", 5],
    ["&", 5],
    ["&^", 5],
]);

const tsBinaryPrecedence = new Map([
    ["||", 4],
    ["&&", 5],
    ["|", 6],
    ["^", 7],
    ["&", 8],
    ["==", 9],
    ["!=", 9],
    ["<", 10],
    ["<=", 10],
    [">", 10],
    [">=", 10],
    ["<<", 11],
    [">>", 11],
    ["+", 12],
    ["-", 12],
    ["*", 13],
    ["/", 13],
    ["%", 13],
]);

/**
 * @typedef {{ kind: "token"; text: string }
 *   | { kind: "unary"; operator: string; operand: GoExpression }
 *   | { kind: "binary"; operator: string; left: GoExpression; right: GoExpression }
 *   | { kind: "parenthesized"; expression: GoExpression }} GoExpression
 */

/**
 * Parse with Go's precedence and print with TypeScript's precedence, adding parentheses where
 * copying the expression verbatim would change its meaning.
 * @param {string} expression
 * @param {string} prefix
 */
function translateGoNumericExpression(expression, prefix) {
    const tokens = expression.match(/<<|>>|&\^|\|\||&&|==|!=|<=|>=|[()+\-*/%&|^<>]|(?:0[xX][\dA-Fa-f_]+|0[bB][01_]+|0[oO][0-7_]+|\d[\d_]*)|[A-Za-z_]\w*/g) ?? [];
    const withoutWhitespace = expression.replace(/\s/g, "");
    if (tokens.join("") !== withoutWhitespace) {
        throw new Error(`Cannot parse numeric enum value: ${expression}`);
    }

    let tokenIndex = 0;

    /** @returns {GoExpression} */
    function parseUnary() {
        const token = tokens[tokenIndex];
        if (token === "+" || token === "-" || token === "^") {
            tokenIndex++;
            return { kind: "unary", operator: token === "^" ? "~" : token, operand: parseUnary() };
        }
        if (token === "(") {
            tokenIndex++;
            const inner = parseBinary(1);
            if (tokens[tokenIndex] !== ")") {
                throw new Error(`Unmatched parenthesis in numeric enum value: ${expression}`);
            }
            tokenIndex++;
            return { kind: "parenthesized", expression: inner };
        }
        if (token === undefined || goBinaryPrecedence.has(token) || token === ")") {
            throw new Error(`Expected operand in numeric enum value: ${expression}`);
        }
        tokenIndex++;
        return { kind: "token", text: token.replace(new RegExp(`^${prefix}`), "") };
    }

    /**
     * @param {number} minimumPrecedence
     * @returns {GoExpression}
     */
    function parseBinary(minimumPrecedence) {
        let left = parseUnary();
        while (true) {
            const operator = tokens[tokenIndex];
            const precedence = goBinaryPrecedence.get(operator);
            if (precedence === undefined || precedence < minimumPrecedence) break;
            tokenIndex++;
            const right = parseBinary(precedence + 1);
            left = { kind: "binary", operator, left, right };
        }
        return left;
    }

    const parsed = parseBinary(1);
    if (tokenIndex !== tokens.length) {
        throw new Error(`Unexpected token '${tokens[tokenIndex]}' in numeric enum value: ${expression}`);
    }

    /**
     * @param {GoExpression} node
     * @param {number} minimumPrecedence
     * @returns {string}
     */
    function print(node, minimumPrecedence) {
        if (node.kind === "token") return node.text;
        if (node.kind === "parenthesized") return `(${print(node.expression, 0)})`;
        if (node.kind === "unary") {
            const text = `${node.operator}${print(node.operand, 14)}`;
            return 14 < minimumPrecedence ? `(${text})` : text;
        }

        const operator = node.operator === "&^" ? "&" : node.operator;
        const precedence = tsBinaryPrecedence.get(operator);
        assert(precedence !== undefined);
        const right = node.operator === "&^"
            ? `~${print(node.right, 14)}`
            : print(node.right, precedence + 1);
        const text = `${print(node.left, precedence)} ${operator} ${right}`;
        return precedence < minimumPrecedence ? `(${text})` : text;
    }

    return print(parsed, 0);
}

/**
 * Resolve a Go string-constant expression (e.g. `Prefix + "call"` or `"export="`)
 * into a quoted, JS-escaped TypeScript string literal. `replacements` maps bare
 * Go identifiers (such as a sentinel-prefix constant) to their literal value.
 * @param {string} goValue
 * @param {Record<string, string>} replacements
 * @returns {string}
 */
function parseGoStringValue(goValue, replacements) {
    let result = "";
    for (const part of goValue.split("+").map(p => p.trim())) {
        if (Object.prototype.hasOwnProperty.call(replacements, part)) {
            result += replacements[part];
            continue;
        }
        const stringMatch = part.match(/^"((?:[^"\\]|\\.)*)"$/);
        if (stringMatch === null) {
            throw new Error(`Cannot parse string enum value: ${goValue}`);
        }
        // Interpret Go escape sequences via JSON, then re-stringify below.
        result += JSON.parse(`"${stringMatch[1]}"`);
    }
    return JSON.stringify(result);
}

/**
 * @typedef {{
 *   name: string;
 *   value: string;
 * }} EnumMember
 */

/**
 * @param {EnumDef} def
 * @returns {EnumMember[]}
 */
function parseGoEnum(def) {
    const source = fs.readFileSync(def.goFile, "utf-8");
    const constBlockRegex = /const\s*\(([\s\S]*?)\n\)/g;

    for (const match of source.matchAll(constBlockRegex)) {
        const members = parseGoConstBlock(match[1], def).filter(member => !def.excludeMembers?.includes(member.name));
        if (members.length > 0) return topoSortMembers(members);
    }

    throw new Error(`No members found for enum ${def.name} in ${def.goFile}`);
}

/**
 * Topologically sort enum members so composite members appear after
 * all members they reference (Go allows forward references, TS does not).
 * @param {EnumMember[]} members
 * @returns {EnumMember[]}
 */
function topoSortMembers(members) {
    const nameSet = new Set(members.map(m => m.name));
    /** @type {Map<string, Set<string>>} */
    const deps = new Map();
    for (const m of members) {
        /** @type {Set<string>} */
        const refs = new Set();
        // Find all identifier references in the value that are other member names
        for (const [ref] of m.value.matchAll(/\b([A-Za-z_]\w*)\b/g)) {
            if (ref !== m.name && nameSet.has(ref)) refs.add(ref);
        }
        deps.set(m.name, refs);
    }

    const sorted = /** @type {EnumMember[]} */ ([]);
    const visited = new Set();
    const visiting = new Set();

    /** @param {string} name */
    function visit(name) {
        if (visited.has(name)) return;
        if (visiting.has(name)) return; // cycle — keep original order
        visiting.add(name);
        for (const dep of deps.get(name) ?? []) {
            visit(dep);
        }
        visiting.delete(name);
        visited.add(name);
        sorted.push(/** @type {EnumMember} */ (members.find(m => m.name === name)));
    }

    for (const m of members) {
        visit(m.name);
    }
    return sorted;
}

/**
 * @param {EnumDef} def
 * @param {EnumMember[]} members
 * @returns {string}
 */
function renderEnumTS(def, members) {
    const header = `// Code generated by Herebyfile.mjs generate:enums from ${def.goFile}. DO NOT EDIT.\n\n`;

    const lines = members.map(m => `    ${m.name} = ${m.value},`);
    return `${header}export enum ${def.name} {\n${lines.join("\n")}\n}\n`;
}

/**
 * Evaluate the generated IIFE in a sandbox and return each member's actual runtime value, so
 * validation checks what TS really computes rather than re-deriving it from the source text.
 * @param {string} enumSource
 * @param {string} enumName
 * @returns {Promise<Record<string, number | string>>}
 */
async function evaluateEnumMembers(enumSource, enumName) {
    const enumModule = await import(`data:text/javascript;charset=utf-8,${encodeURIComponent(enumSource)}`);
    /** @type {Record<string, number | string>} */
    const enumObj = enumModule[enumName];
    return enumObj;
}

async function runGenerateEnums() {
    const ts = /** @type {typeof import("typescript")} */ (await import("typescript"));

    /**
     * @param {string} enumSource
     * @param {string} enumName
     * @returns {string}
     */
    function transpile(enumSource, enumName) {
        return ts.transpileModule(enumSource, {
            compilerOptions: {
                module: ts.ModuleKind.ESNext,
                target: ts.ScriptTarget.ESNext,
            },
        }).outputText;
    }
    /**
     * @param {string} enumSource
     * @param {string} enumName
     * @returns {string}
     */
    function convertEnumToTs(enumSource, enumName) {
        return enumSource.replace(
            `export var ${enumName};`,
            `export var ${enumName}: any;`,
        );
    }

    console.log("Generating enums from Go source...");
    /** @type {Array<GeneratedEnum>} */
    const generatedEnums = [];
    for (const def of enumDefs) {
        const members = parseGoEnum(def);
        const camelName = def.name.charAt(0).toLowerCase() + def.name.slice(1);

        if (!check) fs.mkdirSync(def.outDir, { recursive: true });

        // Generate .enum.ts (TypeScript enum — used for types)
        const enumTS = renderEnumTS(def, members);
        const enumPath = path.join(def.outDir, `${camelName}.enum.ts`);
        writeGenerated(enumPath, enumTS);

        // Generate .ts (IIFE — used at runtime)
        const enumJsCode = transpile(enumTS, def.name);
        const iifeSource = convertEnumToTs(enumJsCode, def.name);
        const iifePath = path.join(def.outDir, `${camelName}.ts`);
        writeGenerated(iifePath, iifeSource);
        generatedEnums.push({
            code: enumJsCode,
            def,
            members,
            fileNames: [enumPath, iifePath],
        });

        console.log(`  ${def.name}: ${members.length} members → ${camelName}.enum.ts, ${camelName}.ts`);
    }

    console.log("Checking pinned wire values");
    const goValuesByEnum = JSON.parse(fs.readFileSync(path.join(root, "csharp/data/enum-values.json"), "utf8"));
    /** @type {string[]} */
    const mismatches = [];
    for (const { def, members, code } of generatedEnums) {
        if (def.stringEnum) continue;
        const goValues = goValuesByEnum[def.name];
        assert(goValues, `Enum ${def.name} was not outputted from GO`);
        const tsValues = await evaluateEnumMembers(code, def.name);
        for (const m of members) {
            const goValue = goValues[m.name];
            const tsValue = tsValues[m.name];
            if (tsValue !== goValue) {
                mismatches.push(
                    `${def.name}.${m.name}: Go says ${goValue}, but generated TS (\`${m.value}\`) evaluates to ${tsValue}`,
                );
            }
        }
    }

    if (mismatches.length > 0) {
        throw new Error(
            `Generated enum values disagree with Go (likely an operator precedence or transcription bug in generate:enums):\n` +
                mismatches.map(m => `  - ${m}`).join("\n"),
        );
    }
    console.log("All generated values match Go.");

    console.log("Done.");
}

await runGenerateEnums();
