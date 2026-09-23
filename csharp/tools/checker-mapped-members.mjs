import path from "node:path";
import { fileURLToPath } from "node:url";
import {
    json,
    output,
    run,
} from "./common.mjs";

const cases = [];
for (const strict of [false, true]) {
    for (const exact of [false, true]) {
        for (const modifiers of [0, 1, 2, 4, 5, 6, 8, 9, 10]) {
            for (const remap of ["none", "filter", "fixed", "number", "string", "never"]) {
                for (const templateKind of ["key", "string", "optional"]) {
                    const steps = ["string", "number", "undefined", "never"].map(text => ({ op: "builtin", text }));
                    const add = step => (steps.push(step), steps.length);
                    const p = add({ op: "parameter", symbol: "P" });
                    const a = add({ op: "string", text: Buffer.from("a").toString("base64") });
                    const b = add({ op: "string", text: Buffer.from("b").toString("base64") });
                    const key = add({ op: "unionReduced", args: [a, b], flags: 1 });
                    const optional = add({ op: "unionReduced", args: [1, 3], flags: 1 });
                    const name = ({ none: 0, filter: p, fixed: a, number: 2, string: 1, never: 4 })[remap];
                    const template = ({ key: p, string: 1, optional })[templateKind];
                    const mapped = add({ op: "mapped", text: "Mapped", args: [p, key, template], flags: modifiers, origin: name });
                    add({ op: "mappedModifiers", args: [mapped] });
                    add({ op: "resolveMembers", args: [mapped] });
                    add({ op: "resolveMembers", args: [mapped] });
                    for (const text of ["a", "b", "missing"]) add({ op: "mappedProperty", args: [mapped], text });
                    add({ op: "mappedOptionality", args: [mapped] });
                    cases.push({ name: `mapped-members:${strict}:${exact}:${modifiers}:${remap}:${templateKind}`, strict, exact, steps });
                }
            }
        }
    }
}
for (const strict of [false, true]) {
    for (const exact of [false, true]) {
        for (const modifiers of [0, 1, 2, 4, 5, 6, 8, 9, 10]) {
            for (const remap of ["none", "filter", "fixed"]) {
                for (const templateKind of ["key", "value"]) {
                    for (let metadata = 0; metadata < 4; metadata++) {
                        const steps = ["string", "number", "undefinedOrMissing"].map(text => ({ op: "builtin", text }));
                        const add = step => (steps.push(step), steps.length);
                        const a = add({ op: "string", text: Buffer.from("a").toString("base64") });
                        const b = add({ op: "string", text: Buffer.from("b").toString("base64") });
                        const optional = add({ op: "unionReduced", args: [1, 3], flags: 1 });
                        const shape = add({ op: "memberShape", properties: ["a", "b"], args: [metadata & 1 ? optional : 1, metadata & 2 ? optional : 2] });
                        add({ op: "propertyMetadata", args: [shape, b], text: "b", flags: metadata & 2 ? 1 << 24 : 0, origin: (metadata & 1 ? 8 : 0) | (1 << 15) });
                        add({ op: "propertyMetadata", args: [shape, a], text: "a", flags: metadata & 1 ? 1 << 24 : 0, origin: metadata & 2 ? 8 : 0 });
                        const t = add({ op: "parameter", symbol: "T" });
                        const p = add({ op: "parameter", symbol: "P" });
                        const key = add({ op: "index", args: [t] });
                        const template = templateKind === "key" ? p : add({ op: "indexed", args: [t, p] });
                        const source = add({ op: "mapped", text: "Mapped", args: [p, key, template, t], flags: modifiers, origin: remap === "filter" ? p : remap === "fixed" ? a : 0, keyof: t });
                        const mapped = add({ op: "instantiate", args: [source, t, shape] });
                        add({ op: "mappedModifiers", args: [mapped] });
                        add({ op: "resolveMembers", args: [mapped] });
                        for (const text of ["a", "b"]) add({ op: "mappedProperty", args: [mapped], text });
                        add({ op: "apparentKeys", args: [mapped, p] });
                        cases.push({ name: `mapped-inherited:${strict}:${exact}:${modifiers}:${remap}:${templateKind}:${metadata}`, strict, exact, steps });
                    }
                }
            }
        }
    }
}
for (const strict of [false, true]) {
    for (const exact of [false, true]) {
        for (const modifiers of [0, 1, 2, 4, 5, 6, 8, 9, 10]) {
            for (const readonly of [false, true]) {
                const steps = ["string", "number", "symbol", "bigint"].map(text => ({ op: "builtin", text }));
                const add = step => (steps.push(step), steps.length);
                const shape = add({ op: "memberShape", properties: ["own"], args: [1] });
                add({ op: "addIndex", args: [shape, 1, 2], this: readonly });
                add({ op: "addIndex", args: [shape, 3, 4], this: !readonly });
                const t = add({ op: "parameter", symbol: "T" });
                const p = add({ op: "parameter", symbol: "P" });
                const key = add({ op: "index", args: [t] });
                const source = add({ op: "mapped", text: "Mapped", args: [p, key, p, t], flags: modifiers, keyof: t });
                const mapped = add({ op: "instantiate", args: [source, t, shape] });
                add({ op: "resolveMembers", args: [mapped] });
                add({ op: "mappedProperty", args: [mapped], text: "own" });
                cases.push({ name: `mapped-indexes:${strict}:${exact}:${modifiers}:${readonly}`, strict, exact, steps });
            }
        }
    }
}
for (const keys of [["__call", "__x", "a"], ["\ufdd0source", "\ufdd0\ufdd0source", "😀", "\ue000"], ["0", "-0", "NaN", "Infinity"]]) {
    const steps = [], add = step => (steps.push(step), steps.length);
    const values = keys.map(text => add({ op: "string", text: Buffer.from(text).toString("base64") }));
    const p = add({ op: "parameter", symbol: "P" });
    const key = add({ op: "unionReduced", args: values, flags: 1 });
    const mapped = add({ op: "mapped", text: "Mapped", args: [p, key, p] });
    add({ op: "resolveMembers", args: [mapped] });
    for (const text of keys) add({ op: "mappedProperty", args: [mapped], text });
    cases.push({ name: `mapped-names:${keys.join(":")}`, strict: true, steps });
}
for (const strict of [false, true]) {
    const steps = ["string", "number", "bigint", "emptyTypeLiteral"].map(text => ({ op: "builtin", text }));
    const add = step => (steps.push(step), steps.length);
    const p = add({ op: "parameter", symbol: "P" });
    const pattern = add({ op: "templateNormalized", args: [1], texts: [Buffer.from("data-").toString("base64"), ""] });
    const mapped = add({ op: "mapped", text: "Pattern", args: [p, pattern, p] });
    add({ op: "resolveMembers", args: [mapped] });
    for (const type of [1, 2, 3]) {
        const intersection = add({ op: "rawIntersection", args: [type, 4] });
        add({ op: "keyLowerBound", args: [intersection] });
    }
    cases.push({ name: `mapped-pattern:${strict}`, strict, steps });
}
{
    const steps = [], add = step => (steps.push(step), steps.length);
    const bytes = ["eda080", "edb080", "eda08061edb080", "00", "f09f9880"];
    const values = bytes.map(hex => add({ op: "string", text: Buffer.from(hex, "hex").toString("base64") }));
    const p = add({ op: "parameter", symbol: "P" });
    const key = add({ op: "unionReduced", args: values, flags: 1 });
    const mapped = add({ op: "mapped", text: "Wtf8", args: [p, key, p] });
    add({ op: "resolveMembers", args: [mapped] });
    for (const hex of bytes) add({ op: "mappedProperty64", args: [mapped], text: Buffer.from(hex, "hex").toString("base64") });
    cases.push({ name: "mapped-wtf8-names", strict: true, steps });
}
for (const numeric of [false, true]) {
    const steps = [], add = step => (steps.push(step), steps.length);
    const first = add({ op: numeric ? "enumNumber" : "enumString", text: numeric ? "0000000000000000" : "same", symbol: "E", member: "E.A" });
    const second = add({ op: numeric ? "enumNumber" : "enumString", text: numeric ? "0000000000000000" : "same", symbol: "F", member: "F.B" });
    const key = add({ op: "unionReduced", args: [first, second], flags: 1 });
    const p = add({ op: "parameter", symbol: "P" });
    const mapped = add({ op: "mapped", text: "Collision", args: [p, key, p] });
    add({ op: "resolveMembers", args: [mapped] });
    add({ op: "mappedProperty", args: [mapped], text: numeric ? "0" : "same" });
    cases.push({ name: `mapped-enum-collision:${numeric}`, strict: true, steps });
}
const file = path.join(output, "checker-mapped-members-inputs.json");
await json(file, cases);
console.log(await run(process.execPath, [fileURLToPath(new URL("./checker-types.mjs", import.meta.url)), "--inputs", file, "--scope", "Mapped member construction and lazy property resolution with explicit semantic fixture dependencies; full checker integration remains incomplete", ...process.argv.slice(2)]));
