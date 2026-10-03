import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, referenceRevision, sha256 } from "./common.mjs";
import { comparableTelemetry } from "./service-response-comparison.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase7-validation/lsp-project-services")));
await mkdir(directory, { recursive: true });
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const oracle = path.join(directory, "oracle.exe"), fixture = path.join(output, "content-mapper-fixture.exe");
const fixtureInfo = JSON.parse(await readFile(path.join(output, "content-mapper-fixture.json"), "utf8"));
assert.equal(fixtureInfo.referenceRevision, referenceRevision);
assert.equal(fixtureInfo.executableSha256, sha256(await readFile(fixture)));
if (!process.argv.includes("--no-prepare")) {
    const target = path.join(source, "cmd/csharp-lsp-session"); await mkdir(target, { recursive: true });
    await copyFile(path.join(root, "csharp/oracle/lsp-session/main.go"), path.join(target, "main.go"));
    await run("D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe", ["-C", source, "build", "-o", oracle, "./cmd/csharp-lsp-session"],
        { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
}
const dll = path.resolve(option("--managed-directory", path.join(output, "phase7-build/bin/TypeScript.Compatibility/release")), "TypeScript.Compatibility.dll");
const component = '<component name="ProfileCard">\n<template><h1>{{ title }}</h1></template>\n<script lang="ts">\nexport const title = "Profile";\n</script>';
const uri = "file:///home/project/ProfileCard.vue";
const document = { textDocument: { uri } }, position = { ...document, position: { line: 3, character: 15 } };
const config = { compilerOptions: { target: "es2020", module: "esnext", moduleResolution: "bundler", strict: true },
    contentMappers: [{ package: "mapper", extensions: [".vue"] }] };
const packageJson = JSON.stringify({ name: "mapper", version: "1.0.0", typescript: { contentMapper: { exec: ["component-mapper"] } } });
const request = (method, params, expected) => ({ method, params, ...(expected !== undefined ? { expected } : {}) });
const notify = (method, params) => ({ method, params, notification: true });
const contribute = (contributions, openDocuments = []) => request("custom/setContentMapperContributions", { contributions, openDocuments }, null);
const inline = (extensions = [".vue"], contributorId = "extension") => ({ contributorId, extensions,
    inferredProjectContribution: { manifest: { name: "component-mapper", exec: ["component-mapper"] } } });
const open = notify("textDocument/didOpen", { textDocument: { uri, languageId: "vue", version: 1, text: component } });
const close = notify("textDocument/didClose", document);
const dynamic = names => Object.fromEntries(names.map(name => [name, { dynamicRegistration: true }]));
const features = ["diagnostic", "hover", "signatureHelp", "definition", "typeDefinition", "implementation", "references", "documentHighlight", "completion", "rename",
    "semanticTokens", "documentSymbol", "foldingRange", "selectionRange", "inlayHint", "codeLens", "codeAction", "formatting", "rangeFormatting", "onTypeFormatting", "linkedEditingRange", "callHierarchy"];
const originalFeatures = ["documentSymbol", "foldingRange", "selectionRange", "inlayHint", "codeLens", "codeAction", "formatting", "rangeFormatting", "onTypeFormatting", "linkedEditingRange", "callHierarchy", "semanticTokens"];
const caps = names => ({ workspace: { fileOperations: { dynamicRegistration: true, willRename: true } },
    textDocument: { synchronization: { dynamicRegistration: true }, ...dynamic(names),
        ...(names.includes("semanticTokens") ? { semanticTokens: { dynamicRegistration: true, requests: {}, tokenTypes: [], tokenModifiers: [], formats: ["relative"] } } : {}) } });
const clearQueries = [request("textDocument/hover", position, null), request("textDocument/diagnostic", document, { kind: "full", items: [] }),
    request("textDocument/completion", position, null), request("textDocument/references", { ...position, context: { includeDeclaration: true } }, null),
    request("textDocument/rename", { ...position, newName: "renamed" }, null)];
let cases = [];
for (const configured of [true, false]) cases.push({ name: configured ? "TestProjectInfoConfiguredProject" : "TestProjectInfoInferredProject",
    cwd: "/home/projects", files: { "/home/projects/index.ts": "export const x = 1;", ...(configured ? { "/home/projects/tsconfig.json": "{}" } : {}) },
    requests: [notify("textDocument/didOpen", { textDocument: { uri: "file:///home/projects/index.ts", languageId: "typescript", version: 1, text: "export const x = 1;" } }),
        request("custom/projectInfo", { textDocument: { uri: "file:///home/projects/index.ts" } }, { configFilePath: configured ? "/home/projects/tsconfig.json" : "" })] });
cases.push({ name: "TestSetContentMapperContributionsBeforeDidOpen", cwd: "/home/project", capabilities: caps(originalFeatures), runExternalCode: true,
    files: { "/home/project/tsconfig.json": JSON.stringify(config), "/home/project/node_modules/mapper/package.json": packageJson, "/home/project/ProfileCard.vue": component },
    requests: [contribute([{ contributorId: "test", extensions: [".vue", ".svelte"] }], [{ uri }]), { waitRegistrations: 16 }, open,
        { ...request("textDocument/hover", position), notNull: true }, request("custom/projectInfo", document, { configFilePath: "/home/project/tsconfig.json" }),
        request("$test/writeFiles", { "/home/project/tsconfig.json": JSON.stringify({ compilerOptions: config.compilerOptions }) }, null),
        notify("workspace/didChangeWatchedFiles", { changes: [{ uri: "file:///home/project/tsconfig.json", type: 2 }] }),
        ...clearQueries, { waitUnregistrations: 16 }, close] });
for (const [name, capabilities, count] of [["all", caps(features), 26], ["sync-only", caps([]), 4],
    ["sync-disabled", { ...caps(features), textDocument: { ...caps(features).textDocument, synchronization: { dynamicRegistration: false } } }, 0],
    ...features.map(feature => [feature, caps([feature]), 5])]) {
    cases.push({ name: `mapper-registration-${name}`, capabilities, runExternalCode: true, files: {},
        requests: [contribute([inline([".vue", ".svelte"])]), { waitRegistrations: count }, contribute([]), { waitUnregistrations: count }] });
}
for (const runExternalCode of [false, true]) {
    const invalid = [null, { contributorId: "", extensions: [] }, { contributorId: "id", extensions: ["vue"] }, { contributorId: "id", extensions: ["."] },
        ...[".ts", ".TS", ".d.ts", ".a.b", ".vue/file", ".tſ"].map(extension => ({ contributorId: "id", extensions: [extension] })),
        { ...inline(), inferredProjectContribution: { manifest: { name: "mapper", exec: [] } } },
        { ...inline(), inferredProjectContribution: { manifest: { name: "", exec: ["mapper"] } } },
        { ...inline(), inferredProjectContribution: { manifest: { name: "mapper", exec: ["mapper"], compilerOptions: ["unknown"] } } },
        { ...inline(), inferredProjectContribution: { manifest: { name: "mapper", exec: ["mapper"], cwd: "relative" } } }];
    cases.push({ name: `mapper-contribution-validation-external-${runExternalCode}`, files: {}, runExternalCode, requests: [
        ...invalid.map(value => ({ method: "custom/setContentMapperContributions", params: { contributions: [value], openDocuments: [] }, error: true })),
        { method: "custom/setContentMapperContributions", params: { contributions: [inline(), inline([".VUE"], "second")], openDocuments: [] }, error: true },
        contribute([{ contributorId: "hint", extensions: [".vue"] }, inline(), { contributorId: "hint2", extensions: [".VUE"] }]),
        contribute([]),
    ] });
}
cases.push({ name: "mapper-inferred-first-open-and-removal", files: { "/home/project/ProfileCard.vue": component }, runExternalCode: true, capabilities: caps(features),
    requests: [contribute([inline()]), { waitRegistrations: 26 }, open, { ...request("textDocument/hover", position), notNull: true },
        request("custom/projectInfo", document, { configFilePath: "" }), contribute([]), ...clearQueries, { waitUnregistrations: 26 }, close] });
cases.push({ name: "mapper-disabled-inline-ignored", files: {}, runExternalCode: false, capabilities: caps(features),
    requests: [contribute([inline()]), { waitRegistrations: 0 }, contribute([]), { waitUnregistrations: 0 }] });
for (const kind of ["configured", "inferred", "nested", "no-ack", "japanese", "disabled", "omitted", "delayed"]) {
    const prefix = `/home/projects/${kind === "nested" ? "sub/" : ""}`;
    const file = prefix + "index.ts", text = "export const x = 1;", config = prefix + "tsconfig.json";
    const textDocument = { uri: `file://${file}` };
    const enabled = !["disabled", "omitted", "delayed"].includes(kind);
    cases.push({ name: kind === "configured" ? "original-TestProgressNotificationsEndToEnd" : `progress-${kind}`,
        cwd: "/home/projects", files: { [file]: text, ...(kind === "inferred" ? {} : { [config]: "{}" }) },
        capabilities: kind === "omitted" ? {} : { window: { workDoneProgress: kind !== "disabled" } },
        locale: kind === "japanese" ? "ja" : undefined, noProgressAck: kind === "no-ack",
        progressDelayMilliseconds: kind === "delayed" ? 3600000 : 0, compareProgress: true,
        requests: [notify("textDocument/didOpen", { textDocument: { ...textDocument, languageId: "typescript", version: 1, text } }),
            request("custom/projectInfo", { textDocument }, { configFilePath: kind === "inferred" ? "" : config }),
            ...(enabled ? [{ waitProgressEnds: 1 }] : [])] });
}
for (const level of [0, 1, 2, 3, 4, 5, -1, 99]) {
    const first = level < 0 || level > 5 || level > 0 && level <= 3 ? 1 : 0;
    cases.push({ name: `logging-verbosity-${level}`, files: {}, logVerbosity: level, compareGCLogs: true,
        requests: [request("custom/runGC", undefined, null), ...(first ? [{ waitGCLogs: first }] : []),
            notify("$/setTrace", { value: "verbose" }), request("custom/runGC", undefined, null), ...(first ? [{ waitGCLogs: 2 }] : []),
            notify("custom/setLogVerbosity", { verbosity: 3 }), request("custom/runGC", undefined, null), { waitGCLogs: first * 2 + 1 }] });
}
cases.push({ name: "developer-profile-endpoints", files: {}, logVerbosity: 0, requests: [
    { ...request("custom/runGC", null), error: true }, { ...request("custom/runGC", {}), error: true },
    { ...request("custom/stopCPUProfile", null), error: true }, { ...request("custom/stopCPUProfile", undefined), error: true }, request("custom/startCPUProfile", { dir: "$profiles" }, null),
    { ...request("custom/startCPUProfile", { dir: "$profiles" }), error: true },
    { ...request("custom/saveHeapProfile", { dir: "$profiles" }), profile: "heap" },
    { ...request("custom/saveAllocProfile", { dir: "$profiles" }), profile: "alloc" },
    { ...request("custom/stopCPUProfile", undefined), profile: "cpu" }, { ...request("custom/stopCPUProfile", undefined), error: true },
] });
cases.push({ name: "protocol-invalid-notifications-preserve-document", files: { "/home/project/index.ts": "export const x = 1;" }, logVerbosity: 0,
    requests: [notify("textDocument/didOpen", { textDocument: { uri: "file:///home/project/index.ts", languageId: "typescript", version: 1, text: "export const x = 1;" } }),
        ...[undefined, null, false, 1, [], {}, { id: null }, { id: true }, { id: 2147483648 }, { id: "unmatched" }].map(params => notify("$/cancelRequest", params)),
        ...[null, {}, { textDocument: { uri: "file:///home/project/index.ts", version: 2 }, contentChanges: null },
            { textDocument: { uri: "file:///home/project/index.ts", version: 2 }, contentChanges: [{ range: null, text: "invalid" }] },
            { textDocument: { uri: "file:///home/project/index.ts", version: "invalid" }, contentChanges: [{ text: "invalid" }] }].map(params => notify("textDocument/didChange", params)),
        notify("custom/setLogVerbosity", {}), notify("$/setTrace", { value: null }),
        request("textDocument/hover", { textDocument: { uri: "file:///home/project/index.ts" }, position: { line: 0, character: 13 } }),
        request("custom/projectInfo", { textDocument: { uri: "file:///home/project/index.ts" } }, { configFilePath: "" })] });
cases.push({ name: "protocol-nonobject-configuration-is-ignored", files: { "/home/project/index.ts": "export const x=1;" },
    requests: [notify("textDocument/didOpen", { textDocument: { uri: "file:///home/project/index.ts", languageId: "typescript", version: 1, text: "export const x=1;" } }),
        notify("workspace/didChangeConfiguration", { settings: { "js/ts": { format: { enabled: false } } } }),
        ...[null, false, 0, "", []].flatMap(settings => [notify("workspace/didChangeConfiguration", { settings }),
            request("textDocument/formatting", { textDocument: { uri: "file:///home/project/index.ts" }, options: { tabSize: 4, insertSpaces: true } }, null)])] });
cases.push({ name: "protocol-full-unsigned-position-range", files: { "/home/project/index.ts": "export const x=1;" },
    requests: [notify("textDocument/didOpen", { textDocument: { uri: "file:///home/project/index.ts", languageId: "typescript", version: 1, text: "export const x=1;" } }),
        request("textDocument/hover", { textDocument: { uri: "file:///home/project/index.ts" }, position: { line: 0, character: 4294967295 } }, null),
        ...[{ line: 4294967295, character: 0 }, { line: 2147483648, character: 2147483648 }]
            .map(position => ({ ...request("textDocument/hover", { textDocument: { uri: "file:///home/project/index.ts" }, position }, null),
                referenceFailure: { code: -32603, message: `InternalError: panic handling request textDocument/hover: runtime error: index out of range [${position.line | 0}]` },
                classification: `The Go decoder accepts uint32, then its signed line index overflows for line ${position.line}. C# clamps this out-of-document coordinate to EOF.` })),
        request("custom/projectInfo", { textDocument: { uri: "file:///home/project/index.ts" } }, { configFilePath: "" })] });
for (const relative of [false, true]) {
    const cwd = "/home/workspace", text = "export const value = 1;";
    const doc = { uri: "file:///home/workspace/a.ts" };
    const capabilities = { workspace: { didChangeWatchedFiles: { dynamicRegistration: true, relativePatternSupport: relative } } };
    const cfg = JSON.stringify({ compilerOptions: { noLib: true, types: [] }, files: ["a.ts"] });
    const watch = pattern => ({ globPattern: pattern, kind: 7 });
    cases.push({ name: `watch-configured-shared-${relative}`, cwd, capabilities, compareWatches: true,
        files: { [cwd + "/tsconfig.json"]: cfg, [cwd + "/a.ts"]: text, "/home/node_modules/pkg/index.d.ts": "export const external: number;" },
        requests: [notify("textDocument/didOpen", { textDocument: { ...doc, languageId: "typescript", version: 1, text } }),
            request("custom/projectInfo", { textDocument: doc }, { configFilePath: cwd + "/tsconfig.json" }),
            { watches: [watch(cwd + "/**/*"), watch("/home/node_modules/**/*")] },
            notify("textDocument/didChange", { textDocument: { ...doc, version: 2 }, contentChanges: [{ text: "export const value = 2;" }] }),
            request("custom/projectInfo", { textDocument: doc }, { configFilePath: cwd + "/tsconfig.json" }),
            { watches: [watch(cwd + "/**/*"), watch("/home/node_modules/**/*")] },
            notify("textDocument/didClose", { textDocument: doc }),
            { watches: [watch(cwd + "/**/*")] }] });
    cases.push({ name: `watch-external-resolution-${relative}`, cwd, capabilities, compareWatches: true,
        files: { [cwd + "/tsconfig.json"]: cfg, [cwd + "/a.ts"]: 'import { external } from "../../external/package/file"; export { external };',
            "/external/package/file.ts": "export const external = 1;" },
        requests: [notify("textDocument/didOpen", { textDocument: { ...doc, languageId: "typescript", version: 1,
            text: 'import { external } from "../../external/package/file"; export { external };' } }),
            request("custom/projectInfo", { textDocument: doc }, { configFilePath: cwd + "/tsconfig.json" }),
            { watches: [watch(cwd + "/**/*"), watch(relative ? { baseUri: "file:///external", pattern: "**/*" } : "/external/**/*")] }] });
    cases.push({ name: `watch-extended-config-${relative}`, cwd, capabilities, compareWatches: true,
        files: { [cwd + "/tsconfig.json"]: JSON.stringify({ extends: "../../external/config/base.json", files: ["a.ts"] }),
            "/external/config/base.json": JSON.stringify({ compilerOptions: { noLib: true, types: [] } }), [cwd + "/a.ts"]: text },
        requests: [notify("textDocument/didOpen", { textDocument: { ...doc, languageId: "typescript", version: 1, text } }),
            request("custom/projectInfo", { textDocument: doc }, { configFilePath: cwd + "/tsconfig.json" }),
            { watches: [watch(cwd + "/**/*"), watch("/external/config/base.json")] }] });
}
for (const kind of ["tsconfig", "jsconfig", "inferred", "disabled", "omitted"]) {
    const cwd = "/home/private-project", doc = { uri: `file://${cwd}/a.ts` }, text = "export const secret = '😀';";
    const configured = kind !== "inferred", enabled = !["disabled", "omitted"].includes(kind);
    const configName = kind === "jsconfig" ? "jsconfig.json" : "tsconfig.json";
    cases.push({ name: `telemetry-project-${kind}`, cwd, compareTelemetry: true, enableTelemetry: kind === "omitted" ? undefined : enabled,
        files: { [cwd + "/a.ts"]: text, ...(configured ? {
            [cwd + "/b.d.ts"]: "declare const symbol: number;", [cwd + "/c.js"]: "export const x = 1;",
            [cwd + "/d.jsx"]: "export const jsx = <div/>;", [cwd + "/e.tsx"]: "export const tsx = <div/>;",
            [cwd + "/" + configName]: JSON.stringify({ extends: "./base.json", files: ["a.ts", "b.d.ts", "c.js", "d.jsx", "e.tsx"], include: [], exclude: [],
                compilerOptions: { strict: false, noImplicitAny: true, target: "es2020", module: "esnext", moduleResolution: "bundler", jsx: "preserve", allowJs: true,
                    checkJs: false, declaration: true, composite: false, isolatedModules: true, skipLibCheck: true, incremental: false } }),
            [cwd + "/base.json"]: JSON.stringify({ compilerOptions: { noLib: true, baseUrl: "/private", paths: { secret: ["./private"] } } })
        } : {}) },
        requests: [notify("textDocument/didOpen", { textDocument: { ...doc, languageId: "typescript", version: 1, text } }),
            request("custom/projectInfo", { textDocument: doc }), { telemetryCount: enabled ? 1 : 0 },
            notify("textDocument/didChange", { textDocument: { ...doc, version: 2 }, contentChanges: [{ text: text + "\n// changed" }] }),
            request("custom/projectInfo", { textDocument: doc }), notify("textDocument/didClose", { textDocument: doc }),
            notify("textDocument/didOpen", { textDocument: { ...doc, languageId: "typescript", version: 3, text } }),
            request("custom/projectInfo", { textDocument: doc }), { telemetryCount: enabled ? 1 : 0 }] });
}
for (const trackFlakyDiagnostics of [0, 1, 2]) {
    const cwd = "/home/project", doc = { textDocument: { uri: "file:///home/project/a.ts" } }, text = 'export const x: number = "wrong";';
    cases.push({ name: `telemetry-diagnostic-flakes-${trackFlakyDiagnostics}`, cwd, trackFlakyDiagnostics, enableTelemetry: true, compareTelemetry: true,
        files: { [cwd + "/a.ts"]: text, [cwd + "/tsconfig.json"]: JSON.stringify({ compilerOptions: { noLib: true, declaration: true }, files: ["a.ts"] }) },
        requests: [notify("textDocument/didOpen", { textDocument: { ...doc.textDocument, languageId: "typescript", version: 1, text } }),
            request("textDocument/diagnostic", doc), request("textDocument/diagnostic", doc), { telemetryCount: 1 }] });
}
const filter = option("--filter", ""); if (filter) cases = cases.filter(input => new RegExp(filter).test(input.name));
await json(path.join(directory, "inputs.json"), cases);

async function execute(input, index, label) {
    const files = path.join(directory, `files-${index}.json`); await json(files, { caseSensitive: false, ...input });
    const child = spawn(label === "reference" ? oracle : "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe",
        label === "reference" ? [files] : [dll, "--lsp-project-test-server", files],
        { cwd: root, windowsHide: true, env: { ...process.env, CSHARP_CONTENT_MAPPER_FIXTURE: fixture } });
    const pending = new Map(), frames = [], callbacks = [], responses = [], watches = new Map(), watchSnapshots = [];
    let priorWatchIds = new Map();
    const profileDirectory = path.join(directory, `${label}-${index}-profiles`);
    let sequence = 0, buffer = Buffer.alloc(0), stderr = "";
    const exit = new Promise(resolve => child.on("close", code => {
        for (const item of pending.values()) item.reject(new Error(`Exit ${code}: ${stderr}`)); resolve(code);
    }));
    child.on("error", error => { for (const item of pending.values()) item.reject(error); });
    child.stderr.on("data", data => stderr += data); child.stdin.on("error", () => {});
    const send = value => {
        const message = { jsonrpc: "2.0", ...value }, bytes = Buffer.from(JSON.stringify(message));
        frames.push({ direction: "client", message }); child.stdin.write(Buffer.concat([Buffer.from(`Content-Length: ${bytes.length}\r\n\r\n`), bytes]));
    };
    const call = (method, params) => new Promise((resolve, reject) => {
        const id = ++sequence;
        const timer = setTimeout(() => { child.kill(); reject(new Error(`Timeout ${label}/${input.name}/${method}: ${stderr}`)); }, 30000);
        pending.set(id, { resolve: value => { clearTimeout(timer); resolve(value); }, reject: error => { clearTimeout(timer); reject(error); } });
        send({ id, method, params });
    });
    child.stdout.on("data", data => {
        buffer = Buffer.concat([buffer, data]);
        for (;;) {
            const header = buffer.indexOf("\r\n\r\n"); if (header < 0) break;
            const match = /Content-Length: (\d+)/i.exec(buffer.subarray(0, header).toString()); assert.ok(match);
            const end = header + 4 + Number(match[1]); if (buffer.length < end) break;
            const message = JSON.parse(buffer.subarray(header + 4, end).toString()); buffer = buffer.subarray(end);
            frames.push({ direction: "server", message });
            if (message.method) {
                callbacks.push(message);
                if (input.compareWatches && message.method === "client/registerCapability")
                    for (const registration of message.params.registrations) if (registration.method === "workspace/didChangeWatchedFiles") {
                        assert.equal(registration.registerOptions.watchers.length, 1, "One watcher per registration");
                        assert.ok(!watches.has(registration.id), "A live registration ID is not reused");
                        watches.set(registration.id, registration.registerOptions.watchers[0]);
                    }
                if (input.compareWatches && message.method === "client/unregisterCapability")
                    for (const registration of message.params.unregisterations) if (registration.method === "workspace/didChangeWatchedFiles")
                        assert.ok(watches.delete(registration.id), "Unregistration refers to a live registration");
                if (message.id !== undefined && !(input.noProgressAck && message.method === "window/workDoneProgress/create"))
                    send({ id: message.id, result: message.method === "workspace/configuration" ? [null, null, null, null] : null });
            } else {
                const item = pending.get(message.id); pending.delete(message.id);
                assert.ok(item, `Unexpected response ${JSON.stringify(message)}`); item.resolve(message);
            }
        }
    });
    function registrations(unregister) {
        return callbacks.filter(message => message.method === (unregister ? "client/unregisterCapability" : "client/registerCapability"))
            .flatMap(message => message.params[unregister ? "unregisterations" : "registrations"])
            .filter(item => item.id.startsWith("content-mapper-"));
    }
    try {
        const init = await call("initialize", { processId: null, rootUri: "file://" + (input.cwd ?? "/"), capabilities: input.capabilities ?? {},
            locale: input.locale,
            initializationOptions: { runExternalCode: input.runExternalCode ?? false, disablePushDiagnostics: true, logVerbosity: input.logVerbosity,
                enableTelemetry: input.enableTelemetry, trackFlakyDiagnostics: input.trackFlakyDiagnostics } });
        assert.ok(!init.error, JSON.stringify(init)); responses.push({ method: "initialize", capabilities: init.result.capabilities });
        send({ method: "initialized", params: {} });
        for (const step of input.requests) {
            if (step.telemetryCount !== undefined) {
                const count = () => callbacks.filter(message => message.method === "telemetry/event").length;
                for (let attempt = 0; attempt < 500 && count() < step.telemetryCount; attempt++) await new Promise(resolve => setTimeout(resolve, 10));
                assert.equal(count(), step.telemetryCount, `${label}/${input.name}: telemetry count`); continue;
            }
            if (step.watches !== undefined) {
                const ordered = values => [...values].sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b)));
                const expected = ordered(step.watches), current = () => ordered(watches.values());
                const end = Date.now() + 5000;
                while (JSON.stringify(current()) !== JSON.stringify(expected) && Date.now() < end) await new Promise(resolve => setTimeout(resolve, 10));
                assert.deepEqual(current(), expected, `${label}/${input.name}/watchers`);
                const ids = new Map([...watches].map(([id, pattern]) => [JSON.stringify(pattern), id]));
                assert.equal(ids.size, watches.size, "Shared globs have one live registration");
                for (const [pattern, id] of ids) if (priorWatchIds.has(pattern)) assert.equal(id, priorWatchIds.get(pattern), "Retained glob keeps registration identity");
                priorWatchIds = ids; watchSnapshots.push(current()); continue;
            }
            if (step.waitGCLogs !== undefined) {
                const logs = () => callbacks.filter(message => message.method === "window/logMessage" && message.params.message === "GC triggered").length;
                const deadline = Date.now() + 5000;
                while (logs() < step.waitGCLogs && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 10));
                assert.equal(logs(), step.waitGCLogs, `${label}/${input.name}/GC logs`); continue;
            }
            if (step.waitProgressEnds !== undefined) {
                const ends = () => callbacks.filter(message => message.method === "$/progress" && message.params.value.kind === "end").length;
                const deadline = Date.now() + 5000;
                while (ends() < step.waitProgressEnds && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 10));
                assert.equal(ends(), step.waitProgressEnds, `${label}/${input.name}/progress ends`); continue;
            }
            if (step.waitRegistrations !== undefined || step.waitUnregistrations !== undefined) {
                const unregister = step.waitUnregistrations !== undefined, count = step.waitUnregistrations ?? step.waitRegistrations;
                const end = Date.now() + 5000;
                while (registrations(unregister).length < count && Date.now() < end) await new Promise(resolve => setTimeout(resolve, 10));
                assert.equal(registrations(unregister).length, count, `${label}/${input.name}/registrations`); continue;
            }
            if (step.notification) { send({ method: step.method, params: step.params }); continue; }
            let { result, error } = await call(step.method, step.params?.dir === "$profiles" ? { ...step.params, dir: profileDirectory } : step.params);
            if (step.referenceFailure) {
                if (label === "reference") assert.deepEqual(error, step.referenceFailure);
                else { assert.ok(!error); assert.deepEqual(result, step.expected); }
                responses.push({ method: step.method, classifiedDifference: step.classification, reference: step.referenceFailure, candidate: step.expected });
                continue;
            }
            if (step.profile) {
                assert.ok(!error, JSON.stringify(error)); assert.deepEqual(Object.keys(result), ["file"]);
                assert.equal(path.dirname(result.file), profileDirectory);
                assert.match(path.basename(result.file), new RegExp(`^\\d+-\\d+-${step.profile}profile\\.pb\\.gz$`));
                const raw = await run("D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe", ["tool", "pprof", "-raw", result.file]);
                await writeFile(`${result.file}.raw.txt`, raw);
                const sample = { cpu: "cpu/nanoseconds", heap: "inuse_space/bytes", alloc: "alloc_space/bytes" }[step.profile];
                assert.ok(raw.includes(sample), `Missing ${sample} samples`);
                result = { file: `<${step.profile}-profile>`, consumer: "go tool pprof", sampleType: sample };
            }
            responses.push({ method: step.method, ...(error ? { error } : { result }) });
            if (step.error) assert.ok(error, `${label}/${input.name}: expected error`);
            else {
                assert.ok(!error, `${label}/${input.name}: ${JSON.stringify(error)}`);
                if ("expected" in step) assert.deepEqual(result, step.expected, `${label}/${input.name}/${step.method}`);
                if (step.notNull) assert.notEqual(result, null, `${label}/${input.name}/${step.method}`);
            }
        }
        const shutdown = await call("shutdown"); assert.ok(!shutdown.error, JSON.stringify(shutdown)); send({ method: "exit" }); child.stdin.end();
        assert.equal(await exit, 0, stderr);
        return { name: input.name, responses, registrations: registrations(false), unregistrations: registrations(true),
            ...(input.compareWatches ? { watchSnapshots } : {}),
            ...(input.compareTelemetry ? { telemetry: callbacks.filter(message => message.method === "telemetry/event").map(message => comparableTelemetry(message.params)) } : {}),
            ...(input.compareGCLogs ? { logs: callbacks.filter(message => message.method === "window/logMessage" && message.params.message === "GC triggered").map(message => message.params) } : {}),
            ...(input.compareProgress ? { progress: callbacks.filter(message => ["window/workDoneProgress/create", "$/progress"].includes(message.method))
                .map(({ method, params }) => ({ method, params })) } : {}) };
    } finally {
        if (child.exitCode === null) child.kill();
        await json(path.join(directory, `${label}-${index}.frames.json`), frames);
        await writeFile(path.join(directory, `${label}-${index}.stderr`), stderr);
    }
}
const differences = [], results = [];
for (let i = 0; i < cases.length; i++) {
    const runs = await Promise.allSettled([execute(cases[i], i, "reference"), execute(cases[i], i, "candidate")]);
    const pair = runs.map(result => result.status === "fulfilled" ? result.value : { error: result.reason.stack });
    results.push({ name: cases[i].name, reference: pair[0], candidate: pair[1] });
    try { assert.deepEqual(pair[1], pair[0]); assert.ok(!pair[0].error); }
    catch { differences.push(results.at(-1)); }
    await json(path.join(directory, "results.json"), results); await json(path.join(directory, "differences.json"), differences);
    console.log(`${i + 1}/${cases.length} ${cases[i].name}: ${differences.at(-1)?.name === cases[i].name ? "FAIL" : "PASS"}`);
}
const summary = { referenceRevision, cases: cases.length, requests: cases.reduce((n, input) => n + input.requests.filter(step => step.method && !step.notification).length, 0),
    classifiedDifferences: cases.reduce((n, input) => n + input.requests.filter(step => step.referenceFailure).length, 0),
    differences: differences.length, compilerSha256: sha256(await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))),
    inputsSha256: sha256(await readFile(path.join(directory, "inputs.json"))), fixture: fixtureInfo };
await json(path.join(directory, "summary.json"), summary); console.log(JSON.stringify(summary));
assert.equal(differences.length, 0);
