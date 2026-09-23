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
        for (const readonly of [false, true]) {
            for (const modifiers of [0, 1, 2, 4, 5, 6, 8, 9, 10]) {
                for (const templateKind of ["key", "indexed", "string", "void", "undefined", "optional", "error", "errorAlias"]) {
                    const steps = ["string", "number", "unknown", "any", "never", "undefined", "missing", "void", "null", "error", "wildcard"].map(text => ({ op: "builtin", text }));
                    const add = step => (steps.push(step), steps.length);
                    const t = add({ op: "parameter", symbol: "T" });
                    const p = add({ op: "parameter", symbol: "P" });
                    const index = add({ op: "index", args: [t] });
                    const indexed = add({ op: "indexed", args: [t, p] });
                    const optional = add({ op: "unionReduced", args: [1, 6], flags: 1 });
                    const errorAlias = add({ op: "errorAlias", symbol: "Unresolved" });
                    const template = ({ key: p, indexed, string: 1, void: 8, undefined: 6, optional, error: 10, errorAlias })[templateKind];
                    const mapped = add({ op: "mapped", text: "Mapped", args: [p, index, template, t], flags: modifiers });
                    if (modifiers === 0 && templateKind === "key") add({ op: "resolveEmpty", args: [mapped] });
                    const array = add({ op: "array", args: [1], this: readonly });
                    const tuple = add({ op: "tuple", args: [1, 2], elements: [1, 2], this: readonly });
                    const rest = add({ op: "tuple", args: [1, 2, 1], elements: [1, 4, 1], this: readonly });
                    const union = add({ op: "unionReduced", args: [array, tuple, 9], flags: 1 });
                    add({ op: "mappedConstraint", args: [mapped] });
                    add({ op: "mappedTemplate", args: [mapped] });
                    add({ op: "mappedName", args: [mapped] });
                    add({ op: "homomorphic", args: [mapped] });
                    for (const replacement of [1, 3, 4, 5, 10, 11, array, tuple, rest, union]) {
                        add({ op: "mappedInstantiate", args: [mapped, t, replacement] });
                        add({ op: "instantiate", args: [mapped, t, replacement] });
                        add({ op: "instantiate", args: [mapped, t, replacement] });
                    }
                    cases.push({ name: `mapped:${strict}:${exact}:${readonly}:${modifiers}:${templateKind}`, strict, exact, steps });
                }
            }
        }
    }
}
for (const strict of [false, true]) {
    for (const exact of [false, true]) {
        for (const modifiers of [0, 1, 2, 4, 5, 6, 8, 9, 10]) {
            for (const remapping of [false, true]) {
                const steps = ["string", "number", "any", "wildcard"].map(text => ({ op: "builtin", text }));
                const add = step => (steps.push(step), steps.length);
                const t = add({ op: "parameter", symbol: "T" });
                const u = add({ op: "parameter", symbol: "U" });
                const p = add({ op: "parameter", symbol: "P" });
                const index = add({ op: "index", args: [t] });
                const mapped = add({ op: "mapped", text: "Mapped", args: [p, index, p, t], flags: modifiers, origin: remapping ? p : 0 });
                const variadic = add({ op: "tuple", args: [1, u, 2], elements: [1, 8, 1] });
                const first = add({ op: "array", args: [1] });
                const second = add({ op: "array", args: [2], this: true });
                const intersection = add({ op: "rawIntersection", args: [first, second], flags: 1 << 25 });
                for (const target of [variadic, intersection]) {
                    const result = add({ op: "instantiate", args: [mapped, t, target], symbol: "Alias", aliasArgs: [target] });
                    add({ op: "instantiate", args: [mapped, t, target], symbol: "Alias", aliasArgs: [target] });
                    add({ op: "genericType", args: [result] });
                }
                add({ op: "genericMapped", args: [mapped] });
                cases.push({ name: `mapped-composite:${strict}:${exact}:${modifiers}:${remapping}`, strict, exact, steps });
            }
        }
        for (const genericName of [false, true]) {
            const steps = ["string", "number", "wildcard"].map(text => ({ op: "builtin", text }));
            const add = step => (steps.push(step), steps.length);
            const t = add({ op: "parameter", symbol: "T" });
            const p = add({ op: "parameter", symbol: "P" });
            const mapped = add({ op: "mapped", text: "Finite", args: [p, 1, p, t], origin: genericName ? t : p });
            add({ op: "genericMapped", args: [mapped] });
            const result = add({ op: "instantiate", args: [mapped, t, 2] });
            add({ op: "genericMapped", args: [result] });
            add({ op: "mappedName", args: [result] });
            const mappedWildcard = add({ op: "mapped", text: "Wildcard", args: [p, 3, 2, t] });
            add({ op: "mappedInstantiate", args: [mappedWildcard, t, 1] });
            const substitute = add({ op: "substitution", args: [t, 1] });
            add({ op: "actualVariable", args: [substitute] });
            cases.push({ name: `mapped-remap:${strict}:${exact}:${genericName}`, strict, exact, steps });
        }
    }
}
for (const strict of [false, true]) {
    for (const wrapper of ["Value", "(value: Value) => void", "(callback: (value: Value) => void) => void"]) {
        for (const unary of [false, true]) {
            for (const matching of [false, true]) {
                for (const shape of ["parameter", "index", "indexed", "substitution", "string"]) {
                    const steps = ["string", "number", "unknown"].map(text => ({ op: "builtin", text }));
                    const add = step => (steps.push(step), steps.length);
                    const t = add({ op: "parameter", symbol: "T" });
                    const p = add({ op: "parameter", symbol: "P" });
                    const type = shape === "parameter" ? t : shape === "string" ? 1
                        : add({ op: shape, args: shape === "index" ? [t] : [t, shape === "indexed" ? p : 1] });
                    const check = unary ? "[Check]" : "Check", extension = unary ? "[Extends]" : "Extends";
                    const source = `type Fixture = ${check} extends ${extension} ? ${wrapper} : never;`;
                    add({ op: "typeNodeFlow", text: source, args: [type, matching ? type : p, 2] });
                    cases.push({ name: `type-node-flow:${strict}:${wrapper}:${unary}:${matching}:${shape}`, strict, steps });
                }
            }
        }
    }
    for (const constraint of ["array", "tuple", "union", "mixed"]) {
        const steps = ["string", "number", "any"].map(text => ({ op: "builtin", text }));
        const add = step => (steps.push(step), steps.length);
        const array = add({ op: "array", args: [1] });
        const tuple = add({ op: "tuple", args: [1, 2], elements: [1, 2] });
        const bound = constraint === "array" ? array : constraint === "tuple" ? tuple
            : add({ op: "unionReduced", args: [array, constraint === "mixed" ? 1 : tuple], flags: 1 });
        const t = add({ op: "parameter", symbol: "T", args: [bound] });
        const p = add({ op: "parameter", symbol: "P" });
        const index = add({ op: "index", args: [t] });
        const mapped = add({ op: "mapped", text: "Mapped", args: [p, index, 2, t] });
        add({ op: "instantiate", args: [mapped, t, 3] });
        cases.push({ name: `mapped-any-constraint:${strict}:${constraint}`, strict, steps });
    }
}
const file = path.join(output, "checker-mapped-inputs.json");
await json(file, cases);
console.log(await run(process.execPath, [fileURLToPath(new URL("./checker-types.mjs", import.meta.url)), "--inputs", file, "--scope", "Mapped type instantiation, templates and modifiers with explicit semantic fixture dependencies; full checker integration remains incomplete", ...process.argv.slice(2)]));
