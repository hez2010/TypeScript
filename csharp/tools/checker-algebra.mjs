import path from "node:path";
import { fileURLToPath } from "node:url";
import {
    json,
    output,
    run,
} from "./common.mjs";

const cases = [];
const encoded = value => Buffer.from(value).toString("base64");
const bits = value => {
    const buffer = Buffer.alloc(8);
    buffer.writeDoubleBE(value);
    return buffer.toString("hex");
};
const names = ["any", "unknown", "undefined", "undefinedWidening", "missing", "optional", "null", "nullWidening", "void", "string", "number", "bigint", "boolean", "regularFalse", "regularTrue", "symbol", "never", "silentNever", "object", "error", "wildcard", "auto", "nonInferrableAny", "empty", "unknownEmpty"];
function initial() {
    const steps = names.map(text => ({ op: "builtin", text }));
    steps.push({ op: "string", text: encoded("a") }, { op: "string", text: encoded("b") }, { op: "number", text: bits(0) }, { op: "number", text: bits(1) }, { op: "bigint", text: "0" }, { op: "fresh", args: [26] }, { op: "fresh", args: [28] });
    return steps;
}
for (const strict of [false, true]) {
    for (const exact of [false, true]) {
        for (let first = 1; first <= 32; first++) {
            for (let second = first; second <= 32; second++) {
                const steps = initial(),
                    add = step => {
                        steps.push(step);
                        return steps.length;
                    };
                for (const args of [[first, second], [second, first], [first, second, first]]) {
                    const union = add({ op: "unionReduced", args, flags: 1 });
                    add({ op: "unionReduced", args, flags: 0 });
                    // Relations are provided only for the fixture domain. Complex object
                    // relations will be exercised by the complete checker integration.
                    if (first < 24 && second < 24) add({ op: "unionReduced", args, flags: 2 });
                    for (let flags = 0; flags < 4; flags++) add({ op: "intersection", args, flags });
                    add({ op: "regularAll", args: [union] });
                    add({ op: "filter", args: [union], flags: 12 });
                }
                cases.push({ name: `pairs:${strict}:${exact}:${first}:${second}`, strict, exact, steps });
            }
        }
        for (let iteration = 0; iteration < 120; iteration++) {
            let state = iteration + 193;
            const random = n => (state = (Math.imul(state, 1664525) + 1013904223) >>> 0) % n;
            const steps = initial(),
                add = step => {
                    steps.push(step);
                    return steps.length;
                };
            const roots = [10, 11, 12, 13, 14, 15, 26, 27, 28, 29, 30, 31, 32];
            const unions = [];
            for (let i = 0; i < 8; i++) {
                const args = Array.from({ length: 1 + random(5) }, () => roots[random(roots.length)]);
                unions.push(add({ op: "unionReduced", args, flags: 1, symbol: i % 2 ? "A" + i : "", aliasArgs: i % 2 ? [10] : [] }));
            }
            for (let i = 0; i < 15; i++) {
                const args = Array.from({ length: 2 + random(4) }, () => unions[random(unions.length)]);
                const combined = add({ op: "unionReduced", args, flags: 1, symbol: i % 4 === 0 ? "Combined" : "" });
                add({ op: "filter", args: [combined], flags: 1024 });
                add({ op: "intersection", args, flags: random(4) });
            }
            const constraint = add({ op: "unionReduced", args: [10, 11], flags: 1 });
            const parameter = add({ op: "parameter", symbol: "T", args: [constraint] });
            const a = add({ op: "intersection", args: [parameter, 10] });
            const b = add({ op: "intersection", args: [11, parameter] });
            add({ op: "unionReduced", args: [a, b], flags: 1 });
            add({ op: "intersection", args: [parameter, 12] });
            cases.push({ name: `composites:${strict}:${exact}:${iteration}`, strict, exact, steps });
        }
    }
}

for (const strict of [false, true]) {
    for (let iteration = 0; iteration < 300; iteration++) {
        let state = iteration + 5516;
        const random = n => (state = (Math.imul(state, 1664525) + 1013904223) >>> 0) % n;
        const steps = initial(),
            add = step => {
                steps.push(step);
                return steps.length;
            };
        const t = add({ op: "parameter", symbol: "T" });
        const placeholders = [3, 5, 7, 10, 11, 12, 13, 14, 15, 17, 20, 21, 26, 27, 28, 29, 30, t];
        const text = ["", "get", "X", "-", "Σ", "ß", "İ", "😀", "\u0000", "𐐨"];
        for (let i = 0; i < 8; i++) {
            const args = Array.from({ length: random(4) }, () => placeholders[random(placeholders.length)]);
            const texts = Array.from({ length: args.length + 1 }, () => encoded(text[random(text.length)]));
            const template = add({ op: "templateNormalized", texts, args });
            add({ op: "templateNormalized", texts, args });
            for (const symbol of ["Uppercase", "Lowercase", "Capitalize", "Uncapitalize"]) {
                const mapped = add({ op: "caseMap", symbol, args: [template] });
                add({ op: "caseMap", symbol, args: [mapped] });
            }
        }
        const alternatives = add({ op: "unionReduced", args: [14, 15, 26, 27], flags: 1 });
        add({ op: "templateNormalized", texts: ["", "", ""], args: [alternatives, alternatives] });
        const stringPattern = add({ op: "templateNormalized", texts: [encoded("get"), ""], args: [10] });
        const getX = add({ op: "string", text: encoded("getX") });
        const setX = add({ op: "string", text: encoded("setX") });
        add({ op: "unionReduced", flags: 1, args: [stringPattern, getX, setX] });
        add({ op: "intersection", args: [getX, stringPattern] });
        add({ op: "intersection", args: [setX, stringPattern] });
        cases.push({ name: `templates:${strict}:${iteration}`, strict, steps });
    }
}

// Every scalar, in bounded strings, exercises the pinned casing maps. Context
// fixtures separately cover Final_Sigma and lone UTF-16 surrogate code units.
for (let start = 0; start <= 0x10ffff; start += 4096) {
    let value = "";
    for (let scalar = start; scalar < Math.min(start + 4096, 0x110000); scalar++) if (scalar < 0xd800 || scalar > 0xdfff) value += String.fromCodePoint(scalar);
    const steps = [{ op: "string", text: encoded(value) }];
    for (const symbol of ["Uppercase", "Lowercase", "Capitalize", "Uncapitalize"]) steps.push({ op: "caseMap", symbol, args: [1] });
    cases.push({ name: `casing:${start}`, strict: true, steps });
}
for (const value of ["ΟΣ", "ΟΣΑ", "ΟΣ'", "ΟΣ'A", "AΣ\u0300", "AΣ\u0300B", "ªΣ", "ºΣ", "ⅠΣ", "ΣΣ", "AΣ\ud800", "AΣ\u0345", "AΣ\u0345B", "ßİﬃ", "𐐨abc", "\ud800x\udfff"]) {
    // Encode isolated surrogates as WTF-8, since Node's UTF-8 encoder replaces them.
    const chunks = [];
    for (const rune of value) {
        const cp = rune.codePointAt(0);
        chunks.push(cp >= 0xd800 && cp <= 0xdfff ? Buffer.from([0xed, 0x80 | (cp >> 6 & 63), 0x80 | (cp & 63)]) : Buffer.from(rune));
    }
    const steps = [{ op: "string", text: Buffer.concat(chunks).toString("base64") }];
    for (const symbol of ["Uppercase", "Lowercase", "Capitalize", "Uncapitalize"]) steps.push({ op: "caseMap", symbol, args: [1] });
    cases.push({ name: `sigma:${cases.length}`, strict: true, steps });
}
for (const count of [399, 400]) {
    const steps = [{ op: "builtin", text: "wildcard" }], a = [], b = [];
    for (let i = 0; i < 250; i++) {
        steps.push({ op: "string", text: encoded("a" + i) });
        a.push(steps.length);
    }
    for (let i = 0; i < count; i++) {
        steps.push({ op: "string", text: encoded("b" + i) });
        b.push(steps.length);
    }
    steps.push({ op: "unionReduced", args: a, flags: 1 });
    const left = steps.length;
    steps.push({ op: "unionReduced", args: b, flags: 1 });
    const right = steps.length;
    steps.push({ op: "templateNormalized", args: [1, left, right], texts: ["", "", "", ""] });
    cases.push({ name: `complexity:${count}`, strict: true, steps });
}

for (let iteration = 0; iteration < 400; iteration++) {
    let state = iteration + 8111;
    const random = n => (state = (Math.imul(state, 1664525) + 1013904223) >>> 0) % n;
    const steps = initial(),
        add = step => {
            steps.push(step);
            return steps.length;
        };
    const objects = [];
    for (let i = 0; i < 8; i++) {
        const properties = Array.from({ length: 1 + random(3) }, (_, index) => index === 0 ? "tag" : "p" + index);
        objects.push(add({ op: "shape", properties, args: properties.map(() => [10, 11, 26, 27, 28, 29][random(6)]) }));
    }
    add({ op: "unionReduced", args: objects, flags: 2 });
    add({ op: "unionReduced", args: [...objects, 24], flags: 2 });
    const constraint = add({ op: "unionReduced", args: [10, 11], flags: 1 });
    const parameter = add({ op: "parameter", symbol: "T", args: [constraint] });
    add({ op: "unionReduced", args: [10, 11, parameter], flags: 2 });
    cases.push({ name: `subtypes:${iteration}`, strict: true, steps });
}
{
    const steps = initial(), objects = [];
    for (let i = 0; i < 1001; i++) {
        steps.push({ op: "shape", properties: ["p" + i], args: [29] });
        objects.push(steps.length);
    }
    steps.push({ op: "unionReduced", args: objects, flags: 2 });
    cases.push({ name: "subtype-complexity", strict: true, steps });
}

for (const strict of [false, true]) {
    const steps = initial(),
        add = step => {
            steps.push(step);
            return steps.length;
        };
    const parameter = add({ op: "parameter", symbol: "T" });
    for (const symbol of ["A", "B"]) for (const flags of [2, 3, 0, 1]) add({ op: "intersection", args: [parameter, 10], flags, symbol, aliasArgs: [] });
    const error = add({ op: "errorAlias", symbol: "Missing", aliasArgs: [] });
    for (const op of ["unionReduced", "intersection"]) for (const args of [[error, 1], [1, error], [error, 21], [error, 10, 11]]) add({ op, args, flags: op === "unionReduced" ? 1 : 0 });
    cases.push({ name: `cache-aliases:${strict}`, strict, steps });
}

const file = path.join(output, "checker-algebra-inputs.json");
await json(file, cases);
const tool = fileURLToPath(new URL("./checker-types.mjs", import.meta.url));
console.log(await run(process.execPath, [tool, "--inputs", file, "--scope", "Union/intersection/template/string-mapping normalization with explicit fixture relation services; full semantic checker remains incomplete", ...process.argv.slice(2)]));
