import path from "node:path";
import { fileURLToPath } from "node:url";
import {
    json,
    output,
    run,
} from "./common.mjs";

const cases = [];
const names = ["any", "unknown", "error", "string", "number", "bigint", "boolean", "never", "undefined", "null", "missing", "void"];
const initial = () => names.map(text => ({ op: "builtin", text }));
for (const strict of [false, true]) {
    for (const exact of [false, true]) {
        for (let replacement = 1; replacement <= 12; replacement++) {
            const steps = initial(),
                add = step => {
                    steps.push(step);
                    return steps.length;
                };
            const t = add({ op: "parameter", symbol: "T" });
            const u = add({ op: "parameter", symbol: "U", args: [4] });
            const i = add({ op: "object", flags: 6, symbol: "I" });
            const reference = add({ op: "reference", args: [i, t, u] });
            const union = add({ op: "unionReduced", args: [t, u, 5], flags: 1, symbol: "Union", aliasArgs: [t, u] });
            const intersection = add({ op: "intersection", args: [t, u], flags: 2, symbol: "Intersection", aliasArgs: [t, u] });
            const template = add({ op: "templateNormalized", args: [t], texts: [Buffer.from("get").toString("base64"), ""] });
            const mapping = add({ op: "caseMap", symbol: "Uppercase", args: [t] });
            const index = add({ op: "index", args: [t] });
            const noInfer = add({ op: "noInfer", args: [u] });
            const roots = [t, u, reference, union, intersection, template, mapping, index, noInfer];
            for (const type of roots) {
                add({ op: "instantiate", args: [type, t, replacement, u, 5] });
                add({ op: "instantiate", args: [type, t, replacement, u, 5] });
                add({ op: "instantiate", args: [type, t, t, u, u] });
            }
            for (const type of [t, u, reference, union, intersection]) {
                const restricted = add({ op: "restrictive", args: [type] });
                add({ op: "restrictive", args: [restricted] });
                add({ op: "permissive", args: [type] });
            }
            cases.push({ name: `instantiate:${strict}:${exact}:${replacement}`, strict, exact, steps });
        }
    }
}
for (const strict of [false, true]) {
    for (const exact of [false, true]) {
        for (const readonly of [false, true]) {
            for (let code = 0; code < 4 ** 4; code++) {
                const steps = initial(),
                    add = step => {
                        steps.push(step);
                        return steps.length;
                    };
                const t = add({ op: "parameter", symbol: "T" });
                const array = add({ op: "array", args: [5], this: readonly });
                const nested = add({ op: "tuple", args: [4, 5], elements: [1, 2], this: readonly });
                let state = code;
                const elements = [], args = [];
                for (let i = 0; i < 4; i++) {
                    const flag = [1, 2, 4, 8][state % 4];
                    state = Math.floor(state / 4);
                    elements.push(flag);
                    args.push(flag === 8 ? [t, array, nested, 1][(code + i) % 4] : [4, 5, 6, t][i]);
                }
                const tuple = add({ op: "tuple", args, elements, this: readonly });
                add({ op: "tuple", args, elements, this: readonly });
                add({ op: "instantiate", args: [tuple, t, 4] });
                add({ op: "instantiate", args: [tuple, t, nested] });
                cases.push({ name: `tuple:${strict}:${exact}:${readonly}:${code}`, strict, exact, steps });
            }
        }
    }
}
for (let i = 0; i < 80; i++) {
    const steps = initial(),
        add = step => {
            steps.push(step);
            return steps.length;
        };
    const t = add({ op: "parameter", symbol: "T" });
    const first = add({ op: "tuple", args: [4, 5], elements: [1, i % 2 ? 2 : 1] });
    const second = add({ op: "tuple", args: [6], elements: [1] });
    const union = add({ op: "unionReduced", args: [first, second], flags: 1 });
    const outer = add({ op: "tuple", args: [t, union], elements: [1, 8], this: i % 2 === 0 });
    add({ op: "instantiate", args: [outer, t, 7] });
    const empty = add({ op: "tuple", args: [], elements: [] });
    add({ op: "tuple", args: [empty, first], elements: [8, 8] });
    cases.push({ name: `tuple-distribution:${i}`, strict: true, exact: i % 2 === 0, steps });
}
const file = path.join(output, "checker-instantiation-inputs.json");
await json(file, cases);
console.log(await run(process.execPath, [fileURLToPath(new URL("./checker-types.mjs", import.meta.url)), "--inputs", file, "--scope", "Generic type instantiation and tuple normalization with explicit resolved fixture dependencies; full checker integration remains incomplete", ...process.argv.slice(2)]));
