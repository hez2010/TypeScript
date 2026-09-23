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
        for (const flags of [16, 16 | (1 << 25), 16 | (1 << 24)]) {
            for (const captured of [0, 1, 2]) {
                for (const aliased of [false, true]) {
                    for (const replacement of [1, 2, 3, 4, 5, 6, 7]) {
                        const steps = ["string", "number", "any", "unknown", "never", "undefined", "null"].map(text => ({ op: "builtin", text }));
                        const add = step => (steps.push(step), steps.length);
                        const t = add({ op: "parameter", symbol: "T" });
                        const u = add({ op: "parameter", symbol: "U" });
                        const a = add({ op: "array", args: [u] });
                        const source = add({ op: "capturedObject", flags, text: "Source", args: [t, u].slice(0, captured), symbol: aliased ? "Alias" : "", aliasArgs: aliased ? [t, u] : [] });
                        add({ op: "instantiate", args: [source, t, t, u, u] });
                        const first = add({ op: "instantiate", args: [source, t, a] });
                        add({ op: "instantiate", args: [source, t, a] });
                        const second = add({ op: "instantiate", args: [first, u, replacement] });
                        if (captured > 0) {
                            add({ op: "objectMap", args: [first, t] });
                            add({ op: "objectMap", args: [second, t] });
                            add({ op: "objectMap", args: [second, u] });
                        }
                        add({ op: "instantiate", args: [source, t, replacement, u, replacement], symbol: "Explicit", aliasArgs: [replacement] });
                        const clone = add({ op: "anonymousInstance", args: [source, t], aliasArgs: [u] });
                        add({ op: "objectMap", args: [clone, t] });
                        add({ op: "instantiate", args: [clone, u, replacement] });
                        cases.push({ name: `object:${strict}:${exact}:${flags}:${captured}:${aliased}:${replacement}`, strict, exact, steps });
                    }
                }
            }
        }
        for (const captured of [0, 1, 2]) {
            for (const aliased of [false, true]) {
                const steps = ["string", "number", "unknown"].map(text => ({ op: "builtin", text }));
                const add = step => (steps.push(step), steps.length);
                const t = add({ op: "parameter", symbol: "T" });
                const u = add({ op: "parameter", symbol: "U" });
                const target = add({ op: "object", flags: 6, symbol: "Target" });
                const source = add({ op: "deferredObject", args: [target, ...[t, u].slice(0, captured)], symbol: aliased ? "Alias" : "", aliasArgs: aliased ? [t, u] : [] });
                add({ op: "instantiate", args: [source, t, t, u, u] });
                const first = add({ op: "instantiate", args: [source, t, u] });
                add({ op: "instantiate", args: [source, t, u] });
                add({ op: "instantiate", args: [first, u, 1] });
                add({ op: "instantiate", args: [source, t, 1, u, 2], symbol: "Explicit", aliasArgs: [1, 2] });
                cases.push({ name: `deferred:${strict}:${exact}:${captured}:${aliased}`, strict, exact, steps });
            }
        }
    }
}
for (const type of ["T", "number", "T[]", "Array<T>", "Array<number>", "keyof T", "{ value: T }", "{ value: number }", "{ method(): T }", "{ method(arg: T): number }", "{ method<X extends T>(): number }", "typeof outside", "typeof unknownName", "typeof this", "typeof outside<T>", "T extends string ? T : number"]) {
    for (const body of [false, true]) {
        const source = body
            ? `declare const outside: number; function Host<T>() { type Result = ${type}; }`
            : `declare const outside: number; type Host<T> = { Result: ${type} };`;
        cases.push({ name: `references:${body}:${type}`, strict: true, steps: [{ op: "possiblyReferenced", text: source, symbol: "T" }] });
    }
}
for (const type of ["T", "this", "number", "typeof outside", "typeof this", "{ method(): this }", "{ method(): T }"]) {
    for (const thisType of [false, true]) {
        cases.push({ name: `references:class:${thisType}:${type}`, strict: true, steps: [{ op: "possiblyReferenced", text: `declare const outside: number; class C<T> { Result: ${type}; }`, symbol: thisType ? "C" : "T", this: thisType }] });
    }
}
for (const type of ["T", "number", "(T)", "Array<T>", "infer U"]) {
    cases.push({ name: `references:conditional:${type}`, strict: true, steps: [{ op: "possiblyReferenced", text: `type Host<T> = { Result: number extends ${type} ? string : number };`, symbol: "T", member: "true" }] });
}
const file = path.join(output, "checker-objects-inputs.json");
await json(file, cases);
console.log(await run(process.execPath, [fileURLToPath(new URL("./checker-types.mjs", import.meta.url)), "--inputs", file, "--scope", "Captured object and deferred reference instantiation with explicit outer parameters; full checker integration remains incomplete", ...process.argv.slice(2)]));
