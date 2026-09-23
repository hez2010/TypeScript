import path from "node:path";
import { fileURLToPath } from "node:url";
import {
    json,
    output,
    run,
} from "./common.mjs";

const cases = [];
const names = ["any", "unknown", "error", "string", "number", "bigint", "boolean", "null", "undefined", "never", "object", "empty", "noConstraint", "circularConstraint", "resolvingDefault"];
const initial = () => names.map(text => ({ op: "builtin", text }));
for (const strict of [false, true]) {
    for (let type = 0; type <= 12; type++) {
        for (let fallback = 0; fallback <= 12; fallback++) {
            const steps = initial(),
                add = step => {
                    steps.push(step);
                    return steps.length;
                };
            const parameter = add({ op: "declaredParameter", symbol: "T", args: [type, fallback] });
            for (let repeat = 0; repeat < 2; repeat++) for (const op of ["baseConstraint", "resolvedConstraint", "constraint", "default", "resolvedDefault"]) add({ op, args: [parameter] });
            const clone = add({ op: "cloneParameter", args: [parameter, 4, 5] });
            for (const op of ["baseConstraint", "default", "resolvedDefault"]) add({ op, args: [clone] });
            cases.push({ name: `declarations:${strict}:${type}:${fallback}`, strict, steps });
        }
    }
}
for (const strict of [false, true]) {
    for (let type = 0; type <= 12; type++) {
        const steps = initial();
        steps.push({ op: "declaredParameter", symbol: "K", args: [type, 0], flags: 1 });
        const parameter = steps.length;
        for (const op of ["baseConstraint", "constraint", "resolvedConstraint"]) steps.push({ op, args: [parameter] });
        cases.push({ name: `mapped-parameters:${strict}:${type}`, strict, steps });
    }
}
for (const repeatedIdentity of [false, true]) {
    for (const length of [1, 2, 9, 10, 11, 12, 20, 49, 50, 51, 60, 70]) {
        const steps = initial();
        let previous = 4;
        for (let i = 0; i < length; i++) {
            steps.push({ op: "parameter", symbol: repeatedIdentity ? "T" : "T" + i, args: [previous] });
            previous = steps.length;
        }
        steps.push({ op: "baseConstraint", args: [previous] }, { op: "resolvedConstraint", args: [previous] }, { op: "constraint", args: [previous] });
        cases.push({ name: `depth:${repeatedIdentity}:${length}`, strict: true, steps });
    }
}
for (const declared of [false, true]) {
    for (const count of [1, 2, 3, 10]) {
        const steps = initial(), parameters = [];
        for (let i = 0; i < count; i++) {
            steps.push({ op: declared ? "declaredParameter" : "parameter", symbol: "T" + i, args: declared ? [4, 0] : [] });
            parameters.push(steps.length);
        }
        for (let i = 0; i < count; i++) steps.push({ op: "setConstraint", args: [parameters[i], parameters[(i + 1) % count]] });
        for (const parameter of parameters) for (const op of ["baseConstraint", "resolvedConstraint", "constraint"]) steps.push({ op, args: [parameter] });
        cases.push({ name: `cycles:${declared}:${count}`, strict: true, steps });
    }
}
for (let first = 1; first <= 12; first++) {
    for (let second = 1; second <= 12; second++) {
        const steps = initial(),
            add = step => {
                steps.push(step);
                return steps.length;
            };
        const a = add({ op: "parameter", symbol: "A", args: [first] });
        const b = add({ op: "parameter", symbol: "B", args: [second] });
        for (const op of ["unionReduced", "intersection"]) {
            const composite = add({ op, args: [a, b], flags: op === "unionReduced" ? 1 : 2 });
            add({ op: "baseConstraint", args: [composite] });
            add({ op: "resolvedConstraint", args: [composite] });
        }
        const conditional = add({ op: "conditional", args: [a, b, first, second] });
        add({ op: "baseConstraint", args: [conditional] });
        add({ op: "constraint", args: [conditional] });
        const noInfer = add({ op: "noInfer", args: [a] });
        add({ op: "baseConstraint", args: [noInfer] });
        const substitution = add({ op: "substitution", args: [a, b] });
        add({ op: "baseConstraint", args: [substitution] });
        cases.push({ name: `composites:${first}:${second}`, strict: true, steps });
    }
}
for (let constraint = 0; constraint <= 12; constraint++) {
    const steps = initial(),
        add = step => {
            steps.push(step);
            return steps.length;
        };
    const parameter = add({ op: "parameter", symbol: "T", args: constraint ? [constraint] : [] });
    for (const op of ["template", "stringMapping", "index"]) {
        const type = add({ op, text: "prefix", symbol: "Uppercase", args: [parameter] });
        add({ op: "baseConstraint", args: [type] });
        add({ op: "resolvedConstraint", args: [type] });
    }
    cases.push({ name: `strings:${constraint}`, strict: true, steps });
}
for (let value = 1; value <= 12; value++) {
    const steps = initial(),
        add = step => {
            steps.push(step);
            return steps.length;
        };
    const object = add({ op: "shape", properties: ["p"], args: [value] });
    const index = add({ op: "string", text: Buffer.from("p").toString("base64") });
    const objectParameter = add({ op: "parameter", symbol: "T", args: [object] });
    const indexParameter = add({ op: "parameter", symbol: "K", args: [index] });
    const access = add({ op: "indexed", args: [objectParameter, indexParameter] });
    for (const op of ["baseConstraint", "constraint", "resolvedConstraint"]) add({ op, args: [access] });
    cases.push({ name: `indexed:${value}`, strict: true, steps });
}
for (const marker of [13, 14, 15]) {
    const steps = initial();
    steps.push({ op: "parameter", symbol: "T" });
    const parameter = steps.length;
    steps.push({ op: "setDefault", args: [parameter, marker] }, { op: "default", args: [parameter] }, { op: "resolvedDefault", args: [parameter] });
    cases.push({ name: `default-markers:${marker}`, strict: true, steps });
}
for (const depth of [1, 2, 3, 5, 10]) {
    const steps = initial(),
        add = step => {
            steps.push(step);
            return steps.length;
        };
    const parameter = add({ op: "parameter", symbol: "T" });
    const sameParameter = add({ op: "parameter", symbol: "T" });
    const otherParameter = add({ op: "parameter", symbol: "U" });
    const first = add({ op: "object", flags: 16, symbol: "Origin" });
    const second = add({ op: "object", flags: 16, symbol: "Origin" });
    const literal = add({ op: "object", flags: 16 | 128, symbol: "Origin" });
    const fromNode = add({ op: "object", flags: 16 | (1 << 30), symbol: "Origin" });
    const reference = add({ op: "index", args: [parameter] });
    const access = add({ op: "indexed", args: [first, reference] });
    const mapped = add({ op: "mappedIdentity", args: [first] });
    const intersection = add({ op: "rawIntersection", args: [first, parameter] });
    const mappedIntersection = add({ op: "mappedIdentity", args: [intersection] });
    const roots = [parameter, sameParameter, otherParameter, first, second, literal, fromNode, reference, access, mapped, intersection, mappedIntersection];
    for (const a of roots) {
        for (const b of roots) {
            add({ op: "identityEquals", args: [a, b] });
            add({ op: "identityMatches", args: [a, b] });
            add({ op: "deeplyNested", args: [a, ...Array.from({ length: depth }, () => b)], flags: depth });
        }
    }
    add({ op: "deeplyNested", args: [first, first, second, first, second], flags: 4 });
    cases.push({ name: `recursion:${depth}`, steps, strict: true });
}
for (const js of [false, true]) {
    for (const count of [0, 1, 2, 3]) {
        for (const defaultType of [0, 1, 2, 4, 12]) {
            const steps = initial(),
                add = step => {
                    steps.push(step);
                    return steps.length;
                };
            const first = add({ op: "declaredParameter", symbol: "T", args: [0, defaultType] });
            const second = add({ op: "declaredParameter", symbol: "U", args: [0, first] });
            const third = add({ op: "declaredParameter", symbol: "V", args: [0, second] });
            const args = [5, 6, 7].slice(0, count);
            for (let i = 0; i < 3; i++) add({ op: "fillArgument", args, aliasArgs: [first, second, third], flags: i, this: js });
            cases.push({ name: `fill:${js}:${count}:${defaultType}`, strict: true, steps });
        }
    }
}
const file = path.join(output, "checker-constraints-inputs.json");
await json(file, cases);
console.log(await run(process.execPath, [fileURLToPath(new URL("./checker-types.mjs", import.meta.url)), "--inputs", file, "--scope", "Base/direct constraints, defaults and recursion caches with explicit fixture dependencies; full checker integration remains incomplete", ...process.argv.slice(2)]));
