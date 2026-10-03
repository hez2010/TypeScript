import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { appendFile, mkdir, open, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";
import { comparableJSDocTemplateResult, comparableServiceResult, unorderedProjectResults, unorderedReferenceResults, unorderedFileRenameResults, unorderedCodeLensResults } from "./service-response-comparison.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const semantic = process.argv.includes("--semantic");
const formatting = process.argv.includes("--formatting");
const symbols = process.argv.includes("--symbols");
const workspace = process.argv.includes("--workspace");
const hover = process.argv.includes("--hover");
const signature = process.argv.includes("--signature");
const definitions = process.argv.includes("--definitions");
const codeActions = process.argv.includes("--code-actions");
const completion = process.argv.includes("--completion");
const diagnostics = process.argv.includes("--diagnostics");
const autoInsert = process.argv.includes("--auto-insert");
const inlayHints = process.argv.includes("--inlay-hints");
const codeLens = process.argv.includes("--code-lens");
const callHierarchy = process.argv.includes("--call-hierarchy");
const fileRename = process.argv.includes("--file-rename");
const rename = process.argv.includes("--rename");
const highlights = process.argv.includes("--highlights");
const vsReferences = process.argv.includes("--vs-references");
const references = vsReferences || process.argv.includes("--references");
const implementations = process.argv.includes("--implementations");
const semanticRequest = semantic || hover || signature || definitions || references || implementations || highlights || rename || fileRename || callHierarchy || codeLens || inlayHints || autoInsert || diagnostics || completion || codeActions;
const projectRequest = semanticRequest || workspace;
function documentUri(name) {
    if (name.startsWith("^/")) {
        const [, scheme, host, ...parts] = name.split("/");
        return host === "ts-nul-authority" ? `${scheme}:${parts.join("/")}` : `${scheme}://${host}/${parts.join("/")}`;
    }
    return "file://" + name;
}
async function jsonArray(file, values) {
    const destination = await open(file, "w");
    try {
        await destination.write("[\n");
        for (let i = 0; i < values.length; i++) await destination.write(JSON.stringify(values[i]) + (i + 1 < values.length ? ",\n" : "\n"));
        await destination.write("]\n");
    } finally { await destination.close(); }
}
const directory = option("--directory", path.join(output, "phase7-validation", codeActions ? "lsp-code-actions" : completion ? "lsp-completion" : diagnostics ? "lsp-diagnostics" : autoInsert ? "lsp-auto-insert" : inlayHints ? "lsp-inlay-hints" : codeLens ? "lsp-code-lens" : callHierarchy ? "lsp-call-hierarchy" : fileRename ? "lsp-file-rename" : rename ? "lsp-rename" : highlights ? "lsp-highlights" : vsReferences ? "lsp-vs-references" : references ? "lsp-references" : implementations ? "lsp-implementations" : definitions ? "lsp-definitions" : signature ? "lsp-signature-help" : hover ? "lsp-hover" : workspace ? "lsp-workspace-symbols" : symbols ? "lsp-document-symbols" : formatting ? "lsp-formatting" : semantic ? "lsp-semantic" : "lsp-syntax")); await mkdir(directory, { recursive: true });
const source = option("--input", path.join(output, "phase7-validation", codeActions ? "code-actions" : completion ? "completion" : diagnostics ? "diagnostics" : autoInsert ? "auto-insert" : inlayHints ? "inlay-hints" : codeLens ? "code-lens" : callHierarchy ? "call-hierarchy" : fileRename ? "file-rename" : rename ? "rename" : highlights ? "highlights" : vsReferences ? "vs-references" : references ? "references" : implementations ? "implementations" : definitions ? "definitions" : signature ? "signature-help" : hover ? "hover" : workspace ? "workspace-symbols" : symbols ? "document-symbols" : formatting ? "formatting" : semantic ? "semantic-services" : "syntax-services", "original-inputs.jsonl"));
const match = option("--match", "");
const openedMatch = option("--match-open-files", "");
const jsdocTemplates = process.argv.includes("--jsdoc-templates");
const capturedInputs = await readFile(source, "utf8");
const recorded = capturedInputs.trim().split(/\r?\n/).map(JSON.parse).filter(input => (!match || new RegExp(match).test(input.name))
    && (!openedMatch || (input.openFiles ?? input.rootFiles ?? Object.keys(input.files)).some(name => new RegExp(openedMatch).test(name))));
if (formatting) for (const input of recorded) input.files = { [input.file]: input.text };
const groups = new Map();
for (const input of recorded.filter(input => !input.projections && !input.mappedFiles)) {
    const key = JSON.stringify([input.encoding, input.foldingCapabilities ?? { foldingRange: { collapsedText: true } },
        ...(semanticRequest ? [input.semanticCapabilities, input.compilerOptions, input.rootFiles, input.files, input.cwd, input.caseSensitive, input.inferredOptions, input.symlinks] : []),
        ...(hover || signature || definitions || references || implementations || highlights || rename || fileRename || callHierarchy || codeLens || inlayHints || autoInsert || diagnostics || completion || codeActions ? [input.capabilities, input.preferences, input.openFiles, input.codeLensShowLocationsCommandName] : []),
        ...(workspace ? [input.programs, input.files, input.cwd, input.openFiles, input.preferences] : []),
        ...(symbols ? [input.symbolCapabilities] : [])]);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(input);
}
await json(path.join(directory, "files.json"), { files: {} });
if (!process.argv.includes("--no-build")) await run(dotnet, ["build", "csharp/tests/TypeScript.Compatibility", "-c", "Release", "--no-restore", "--artifacts-path", "built/csharp/phase7-build"]);
const dll = path.join(option("--managed-directory", path.join(output, "phase7-build/bin/TypeScript.Compatibility/release")), "TypeScript.Compatibility.dll");
const differences = [], results = [], clients = [];
let groupIndex = 0, requests = 0;
for (const [key, inputs] of groups) {
    const [encoding, foldingRange] = JSON.parse(key);
    const initial = inputs[0];
    const filesPath = path.join(directory, projectRequest ? `files-${groupIndex}.json` : "files.json");
    if (projectRequest) await json(filesPath, { files: initial.files, inferredOptions: initial.inferredOptions ?? initial.compilerOptions ?? initial.programs?.[0]?.compilerOptions, cwd: initial.cwd, caseSensitive: initial.caseSensitive, symlinks: initial.symlinks });
    const child = spawn(dotnet, [dll, "--lsp-test-server", filesPath], { cwd: root, windowsHide: true });
    const pending = new Map(); let sequence = 0, buffer = Buffer.alloc(0), stderr = "", current = {}, callbacks = [], previousOptions = "";
    const exit = new Promise(resolve => child.on("close", code => { for (const item of pending.values()) item.reject(new Error(`Exit ${code}: ${stderr}`)); resolve(code); }));
    child.on("error", error => { for (const item of pending.values()) item.reject(error); });
    child.stderr.on("data", data => stderr += data);
    child.stdin.on("error", () => {});
    const send = value => { const bytes = Buffer.from(JSON.stringify({ jsonrpc: "2.0", ...value })); child.stdin.write(Buffer.concat([Buffer.from(`Content-Length: ${bytes.length}\r\n\r\n`), bytes])); };
    const notify = (method, params) => send({ method, params });
    const request = (method, params) => new Promise((resolve, reject) => {
        const id = ++sequence;
        const timer = setTimeout(() => { child.kill(); reject(new Error(`Timeout ${method} in ${inputs[0].name}: ${stderr}`)); }, 30000);
        pending.set(id, { resolve: result => { clearTimeout(timer); resolve(result); }, reject: error => { clearTimeout(timer); reject(error); } });
        send({ id, method, params });
    });
    child.stdout.on("data", data => {
        buffer = Buffer.concat([buffer, data]);
        for (;;) {
            const header = buffer.indexOf("\r\n\r\n"); if (header < 0) break;
            const match = /Content-Length: (\d+)/i.exec(buffer.subarray(0, header).toString()); assert.ok(match);
            const end = header + 4 + Number(match[1]); if (buffer.length < end) break;
            const message = JSON.parse(buffer.subarray(header + 4, end).toString()); buffer = buffer.subarray(end);
            if (message.method) {
                callbacks.push(message);
                if (message.id !== undefined) send({ id: message.id, result: message.method === "workspace/configuration" ? [initial.preferences ?? {}, {}, {}, {}] : null });
            } else {
                const item = pending.get(message.id); pending.delete(message.id);
                assert.ok(item, `Unexpected response ${JSON.stringify(message)}`);
                item.resolve(message);
            }
        }
    });
    try {
        const initialized = await request("initialize", { rootUri: "file://" + (initial.cwd ?? "/"), capabilities: { ...initial.capabilities,
            general: { positionEncodings: [encoding] }, workspace: { ...initial.capabilities?.workspace, configuration: true },
            textDocument: { ...initial.capabilities?.textDocument, foldingRange, ...(semantic ? { semanticTokens: initial.semanticCapabilities } : {}), ...(symbols ? { documentSymbol: initial.symbolCapabilities } : {}) } }, initializationOptions: { disablePushDiagnostics: true, codeLensShowLocationsCommandName: initial.codeLensShowLocationsCommandName } });
        assert.equal(initialized.result.capabilities.positionEncoding, encoding);
        for (const name of ["selectionRangeProvider", "linkedEditingRangeProvider", "foldingRangeProvider"]) assert.equal(initialized.result.capabilities[name], true);
        if (diagnostics) assert.deepEqual(initialized.result.capabilities.diagnosticProvider, { identifier: "typescript", interFileDependencies: true, workspaceDiagnostics: false });
        if (autoInsert) assert.deepEqual(initialized.result.capabilities._vs_onAutoInsertProvider, { _vs_triggerCharacters: [">"] });
        if (inlayHints) assert.equal(initialized.result.capabilities.inlayHintProvider, true);
        if (codeLens) assert.deepEqual(initialized.result.capabilities.codeLensProvider, { resolveProvider: true });
        if (completion) assert.deepEqual(initialized.result.capabilities.completionProvider, { resolveProvider: true,
            triggerCharacters: [".", '"', "'", "`", "/", "@", "<", "#", " ", "*"], completionItem: { labelDetailsSupport: true } });
        if (codeActions) assert.deepEqual(initialized.result.capabilities.codeActionProvider, {
            codeActionKinds: ["quickfix", "source.organizeImports.ts", "source.removeUnusedImports.ts", "source.sortImports.ts", "source.fixAll.ts"] });
        if (callHierarchy) assert.equal(initialized.result.capabilities.callHierarchyProvider, true);
        if (rename) assert.deepEqual(initialized.result.capabilities.renameProvider, { prepareProvider: true });
        if (symbols) assert.equal(initialized.result.capabilities.documentSymbolProvider, true);
        if (workspace) assert.equal(initialized.result.capabilities.workspaceSymbolProvider, true);
        if (hover) assert.equal(initialized.result.capabilities.hoverProvider, true);
        if (highlights) {
            assert.equal(initialized.result.capabilities.documentHighlightProvider, true);
            assert.equal(initialized.result.capabilities.experimental.customMultiDocumentHighlightProvider, true);
        }
        if (vsReferences) assert.equal(initialized.result.capabilities._vs_referencesProvider, true);
        if (references) assert.equal(initialized.result.capabilities.referencesProvider, true);
        if (implementations) assert.equal(initialized.result.capabilities.implementationProvider, true);
        if (definitions) {
            assert.equal(initialized.result.capabilities.definitionProvider, true); assert.equal(initialized.result.capabilities.typeDefinitionProvider, true);
            assert.equal(initialized.result.capabilities.experimental.customSourceDefinitionProvider, true);
        }
        if (signature) assert.deepEqual(initialized.result.capabilities.signatureHelpProvider, { triggerCharacters: ["(", ",", "<"], retriggerCharacters: [")"] });
        if (formatting) {
            assert.equal(initialized.result.capabilities.documentFormattingProvider, true);
            assert.equal(initialized.result.capabilities.documentRangeFormattingProvider, true);
            assert.deepEqual(initialized.result.capabilities.documentOnTypeFormattingProvider, { firstTriggerCharacter: "{", moreTriggerCharacter: ["}", ";", "\n"] });
        }
        notify("initialized", {});
        for (const input of inputs) {
            if (formatting && previousOptions !== JSON.stringify(input.options)) {
                notify("workspace/didChangeConfiguration", { settings: { "js/ts": { unstable: Object.fromEntries(Object.entries(input.options).map(([key, value]) => [key[0].toLowerCase() + key.slice(1), value])) } } });
                previousOptions = JSON.stringify(input.options);
            }
            if (JSON.stringify(current) !== JSON.stringify(input.files)) {
                for (const name of Object.keys(current)) notify("textDocument/didClose", { textDocument: { uri: documentUri(name) } });
                const opened = projectRequest ? (input.openFiles ?? input.rootFiles).map(name => [name, input.files[name]]) : Object.entries(input.files);
                for (const [name, text] of opened) notify("textDocument/didOpen", { textDocument: {
                    uri: documentUri(name), languageId: name.endsWith(".json") ? "json" : name.endsWith(".tsx") ? "typescriptreact" : name.endsWith(".jsx") ? "javascriptreact" : /\.[cm]?js$/.test(name) ? "javascript" : "typescript", version: 1, text } });
                current = input.files;
            }
            if (input.priorCompletions?.length) {
                for (const previous of input.priorCompletions) {
                    notify("workspace/didChangeConfiguration", { settings: { "js/ts": previous.preferences } });
                    const warmup = await request("textDocument/completion", previous.params);
                    assert.equal(warmup.error, undefined, `Preceding completion failed in ${input.name}`);
                }
                notify("workspace/didChangeConfiguration", { settings: { "js/ts": input.preferences } });
            }
            const actual = await request(input.method, input.params); requests++;
            results.push({ name: input.name, method: input.method, encoding, response: actual });
            try { assert.deepEqual(actual.error, input.error); if (input.error) assert.equal(actual.result, undefined); else assert.deepEqual((jsdocTemplates ? comparableJSDocTemplateResult : comparableServiceResult)(input, actual.result), (jsdocTemplates ? comparableJSDocTemplateResult : comparableServiceResult)(input, input.result)); }
            catch { differences.push({ input, actual }); }
        }
        assert.equal((await request("shutdown", null)).result, null);
        await appendFile(path.join(directory, "progress.jsonl"), JSON.stringify({ group: groupIndex, phase: "shutdown", requests: inputs.length, differences: differences.length }) + "\n");
        notify("exit", null);
        const exitTimer = setTimeout(() => child.kill(), 5000);
        const code = await exit; clearTimeout(exitTimer); assert.equal(code, 0, stderr);
        clients.push({ key, requests: inputs.length, initialize: initialized, callbacks, exitCode: code });
    } finally {
        if (child.exitCode === null) child.kill();
        await writeFile(path.join(directory, `client-${groupIndex++}.stderr`), stderr);
        await json(path.join(directory, "differences.json"), differences);
    }
}
await jsonArray(path.join(directory, "clients.json"), clients); await jsonArray(path.join(directory, "responses.json"), results);
const summary = { referenceRevision, originalTests: new Set(recorded.filter(input => !input.projections && !input.mappedFiles && !input.name.startsWith("TestCSharp")).map(input => input.name)).size,
    ...(jsdocTemplates ? { scope: "Exact null and JSDoc-template completion responses plus template absence in other contexts; other provider contents remain deferred", exactResponseRequests: recorded.filter(input => !input.projections && !input.mappedFiles && (input.result === null || input.result?.items?.some(item => item.label === "/** */"))).length } : {}),
    requests, clients: clients.length, differences: differences.length, mappedQueriesValidatedSeparately: recorded.filter(input => input.projections || input.mappedFiles).length,
    unorderedProjectResults: unorderedProjectResults.filter(name => recorded.some(input => input.name === name)),
    unorderedFileRenameResults: unorderedFileRenameResults.filter(name => recorded.some(input => input.name === name)),
    unorderedCodeLensResults: unorderedCodeLensResults.filter(test => recorded.some(input => input.name === test.name)),
    unorderedReferenceResults: unorderedReferenceResults.filter(name => recorded.some(input => input.name === name)),
    compilerSha256: sha256(await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))), inputsSha256: sha256(capturedInputs) };
await json(path.join(directory, "summary.json"), summary); console.log(JSON.stringify(summary));
if (differences.length) process.exitCode = 1;
