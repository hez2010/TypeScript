import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, open, readFile, writeFile, rm } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";
import { comparableJSDocTemplateResult, comparableServiceResult, unorderedProjectResults, unorderedReferenceResults, unorderedFileRenameResults, unorderedCodeLensResults } from "./service-response-comparison.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const semantic = process.argv.includes("--semantic");
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
const directory = path.resolve(option("--directory", path.join(output, "phase7-validation", codeActions ? "code-actions" : completion ? "completion" : diagnostics ? "diagnostics" : autoInsert ? "auto-insert" : inlayHints ? "inlay-hints" : codeLens ? "code-lens" : callHierarchy ? "call-hierarchy" : fileRename ? "file-rename" : rename ? "rename" : highlights ? "highlights" : vsReferences ? "vs-references" : references ? "references" : implementations ? "implementations" : definitions ? "definitions" : signature ? "signature-help" : hover ? "hover" : workspace ? "workspace-symbols" : symbols ? "document-symbols" : semantic ? "semantic-services" : "syntax-services")));
let symbolFilter;
if (symbols || workspace || hover || signature || definitions || references || implementations || highlights || rename || fileRename || callHierarchy || codeLens || inlayHints || autoInsert || diagnostics || completion || codeActions) {
    const names = codeActions ? ["TestCSharpCodeActions"] : completion ? ["TestCSharpJSDocCompletion", "TestCSharpJSDocTags", "TestCSharpCompletionSnippets"] : diagnostics ? ["TestCSharpDiagnostics"] : autoInsert ? ["TestCSharpAutoInsert"] : inlayHints ? ["TestCSharpInlayHints"] : codeLens ? ["TestCSharpCodeLens"] : callHierarchy ? ["TestCSharpCallHierarchy"] : fileRename ? ["TestCSharpFileRename"] : rename ? ["TestCSharpRename"] : highlights ? ["TestCSharpHighlights"] : vsReferences ? ["TestCSharpVisualStudioReferences"] : references || implementations || highlights || rename || fileRename || callHierarchy || codeLens || inlayHints || autoInsert || diagnostics || completion ? ["TestCSharpReferences"] : definitions ? ["TestCSharpDefinitions"] : signature ? ["TestCSharpSignatureHelp"] : hover ? ["TestCSharpHover"] : [workspace ? "TestCSharpWorkspaceSymbols" : "TestCSharpDocumentSymbols"];
    if (workspace) names.push("TestCSharpWorkspaceSymbolMapper");
    const pattern = codeActions ? "(VerifyCodeFix|VerifyImportFix|VerifyRangeAfterCodeFix|VerifyOrganizeImports|VerifySourceFixAll|TextDocumentCodeActionInfo)" : completion ? "(Verify(No)?JSDocCompletion|VerifyCompletions|VerifyCompletionEntryDetails|GetCompletions|ResolveCompletionItem|BaselineAutoImportsCompletions|VerifyApplyCodeActionFromCompletion)" : diagnostics ? "Verify\\w*(Diagnostic|Error)" : autoInsert ? "Verify(JsxClosingTag|BaselineClosingTags)" : inlayHints ? "VerifyBaselineInlayHints" : codeLens ? "VerifyBaselineCodeLens" : callHierarchy ? "VerifyBaselineCallHierarchy" : fileRename ? "VerifyWillRenameFilesEdits" : rename ? "Verify(Baseline)?Rename" : highlights ? "VerifyBaselineDocumentHighlights" : vsReferences ? "VerifyBaselineVSFindAllReferences" : references ? "VerifyBaselineFindAllReferences" : implementations ? "VerifyBaselineGoToImplementation" : definitions ? "VerifyBaselineGoTo(Type|Source)?Definition" : signature ? "Verify\\w*SignatureHelp" : hover ? "Verify\\w*(QuickInfo|Hover)" : workspace ? "Verify(Baseline)?WorkspaceSymbol" : "VerifyBaselineDocumentSymbol";
    for (const file of (await run("rg", ["-l", pattern, path.join(source, "internal/fourslash/tests")])).split(/\r?\n/)) {
        const text = await readFile(file, "utf8"), functions = [...text.matchAll(/^func (Test\w+)\(/gm)];
        for (let i = 0; i < functions.length; i++)
            if (new RegExp(pattern).test(text.slice(functions[i].index, functions[i + 1]?.index))) names.push(functions[i][1]);
    }
    symbolFilter = `^(${names.join("|")})$`;
}
await mkdir(directory, { recursive: true });
const inputDirectory = path.resolve(option("--input-directory", directory));
assert.ok(inputDirectory === directory || process.argv.includes("--no-record"), "A separate input directory is read-only; use --no-record");
const inputsPath = path.join(inputDirectory, "original-inputs.jsonl");
const resultsPath = path.join(inputDirectory, "original-tests.jsonl");
if (!process.argv.includes("--no-record")) {
    const file = path.join(source, "internal/fourslash/fourslash.go");
    const helper = path.join(source, "internal/fourslash/csharp_syntax_record.go");
    const authored = path.join(source, "internal/fourslash/csharp_syntax_test.go");
    const mapperAuthored = path.join(source, "internal/fourslash/tests/csharp_semantic_mapper_test.go");
    const original = await readFile(file, "utf8");
    const coordinateSources = [];
    if (process.argv.includes("--correct-signature-recovery")) {
        const target = path.join(source, "internal/ls/utilities.go"), text = await readFile(target, "utf8");
        const needle = "if strings.LastIndex(sourceFile.Text(), closeTokenText) < bestGuessIndex {";
        assert.equal(text.split(needle).length, 2);
        coordinateSources.push({ target, text, changed: text.replace(needle, "if bestGuessIndex < token.Pos() && strings.LastIndex(sourceFile.Text(), closeTokenText) < bestGuessIndex {"), correction: "Reject a guessed opening token after the closing token to ensure backward progress." });
    }
    if (hover || signature || definitions || references || implementations || highlights || rename || fileRename || callHierarchy || codeLens || inlayHints || autoInsert || diagnostics || completion || codeActions) for (const name of ["test_parser.go", "baselineutil.go", "semantictokens.go"]) {
        const target = path.join(source, "internal/fourslash", name), text = await readFile(target, "utf8");
        coordinateSources.push({ target, text, changed: text.replaceAll("lsproto.PositionEncodingKindUTF8", "csharpSyntaxEncoding()") });
    }
    const needle = "resMsg, result, resultOk := f.client.SendRequest(t, info, params)";
    assert.equal(original.split(needle).length, 2);
    const changed = original.replace(needle, needle + "\n f.csharpRecordSyntax(t, info, params, result, resMsg.AsResponse().Result, resMsg.AsResponse().Error)")
        .replace("resMsg, result, _ := f.client.SendRequest(t, lsproto.TextDocumentPrepareRenameInfo, params)", "resMsg, result, _ := f.client.SendRequest(t, lsproto.TextDocumentPrepareRenameInfo, params)\n f.csharpRecordSyntax(t, lsproto.TextDocumentPrepareRenameInfo, params, result, resMsg.AsResponse().Result, resMsg.AsResponse().Error)")
        .replace("if renameMsg != nil && renameMsg.AsResponse().Error != nil {", 'f.csharpRecordSyntax(t, lsproto.TextDocumentRenameInfo, &lsproto.RenameParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(f.activeFilename)},Position:f.currentCaretPosition,NewName:"RENAME_FAILED_TEST"}, renameResult, renameMsg.AsResponse().Result, renameMsg.AsResponse().Error)\n if renameMsg != nil && renameMsg.AsResponse().Error != nil {')
        .replace("type FourslashTest struct {", "type FourslashTest struct {\n csharpInferredOptions *core.CompilerOptions\n csharpCompletionState *csharpCompletionState")
        .replace("client.SetCompilerOptionsForInferredProjects(compilerOptions)", "client.SetCompilerOptionsForInferredProjects(compilerOptions)\n f.csharpInferredOptions = compilerOptions")
        .replaceAll("lsproto.PositionEncodingKindUTF8", "csharpSyntaxEncoding()");
    await writeFile(inputsPath, "");
    await copyFile(path.join(root, "csharp/oracle/syntax-services/record.go"), helper);
    await copyFile(path.join(root, "csharp/oracle/syntax-services/authored_test.go"), authored);
    if (semantic || workspace) await copyFile(path.join(root, "csharp/oracle/syntax-services/semantic_mapper_test.go"), mapperAuthored);
    await writeFile(file, changed);
    for (const item of coordinateSources) await writeFile(item.target, item.changed);
    await json(path.join(directory, "instrumentation.json"), { file: "internal/fourslash/fourslash.go", originalSha256: sha256(original), recordedSha256: sha256(changed),
        coordinateSources: coordinateSources.map(item => ({ file: path.relative(source, item.target).replaceAll("\\", "/"), originalSha256: sha256(item.text), recordedSha256: sha256(item.changed), correction: item.correction })) });
    try {
        let results = "";
        const selected = option("--record-match", symbolFilter ?? (semantic ? "^Test(Semantic(Classific|Modern)|SyntacticClassific|CSharpSemantic)" : "^Test(SmartSelection_|LinkedEditingJsxTag|.*Outlin|FoldingRange|IncrementalParsingWithJsDoc|ContentMapper(DropsUnmappedFoldingRanges|SupplementalFoldingRanges|DisabledSupplementalFoldingRanges|DeduplicatesProjectedFoldingRanges|RangeFeaturesIncludeScriptInsideMarkup)$|CSharpSyntaxUnicode|CSharpFoldingCapabilities)"));
        const filters = [];
        if (selected === symbolFilter && selected.length > 12000) {
            let batch = [];
            for (const name of selected.slice(2, -2).split("|")) {
                if (batch.join("|").length + name.length > 12000) { filters.push(`^(${batch.join("|")})$`); batch = []; }
                batch.push(name);
            }
            if (batch.length) filters.push(`^(${batch.join("|")})$`);
        } else filters.push(selected);
        for (const encoding of ["utf-8", "utf-16"]) for (const [batch, filter] of filters.entries()) {
            if (filters.length > 1) console.log(`Recording ${encoding} batch ${batch + 1}/${filters.length}`);
            const child = spawn(go, ["-C", source, "test", "-json", `-count=${option("--reference-count", "1")}`, `-timeout=${option("--reference-timeout", "10m")}`, "-run", filter,
                "./internal/fourslash", "./internal/fourslash/tests"],
                { windowsHide: true, env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local", CSHARP_SYNTAX_RECORD: inputsPath, CSHARP_SYNTAX_ENCODING: encoding, CSHARP_SYNTAX_MODE: codeActions ? "code-actions" : completion ? "completion" : diagnostics ? "diagnostics" : autoInsert ? "auto-insert" : inlayHints ? "inlay-hints" : codeLens ? "code-lens" : callHierarchy ? "call-hierarchy" : fileRename ? "file-rename" : rename ? "rename" : highlights ? "highlights" : vsReferences ? "vs-references" : references ? "references" : implementations ? "implementations" : definitions ? "definitions" : signature ? "signature" : hover ? "hover" : workspace ? "workspace" : "" } });
            let stdout = "", stderr = "";
            child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
            const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
            await writeFile(path.join(directory, `original-${encoding}${filters.length > 1 ? `-${batch}` : ""}.stderr`), stderr);
            results += stdout.trim().split(/\r?\n/).filter(Boolean).map(line => JSON.stringify({ ...JSON.parse(line), encoding })).join("\n") + "\n";
            await writeFile(resultsPath, results);
            assert.equal(code, 0, `Original ${encoding} tests failed: ${stderr}`);
        }
    } finally {
        await writeFile(file, original);
        for (const item of coordinateSources) await writeFile(item.target, item.text);
        await rm(helper);
        await rm(authored);
        if (semantic || workspace) await rm(mapperAuthored);
    }
}
if (process.argv.includes("--record-only")) process.exit(0);
if (!process.argv.includes("--no-build")) await run(dotnet, ["build", "csharp/tests/TypeScript.Compatibility", "-c", "Release", "--no-restore", "--artifacts-path", "built/csharp/phase7-build"]);
const match = option("--match", "");
const capturedInputs = await readFile(inputsPath, "utf8");
const jsdocTemplates = process.argv.includes("--jsdoc-templates");
const isTemplateRequest = input => input.result === null || input.result?.items?.some(item => item.label === "/** */");
const cases = capturedInputs.trim().split(/\r?\n/).filter(Boolean).map(JSON.parse)
    .filter(input => !match || new RegExp(match).test(input.name));
const tests = (await readFile(resultsPath, "utf8")).trim().split(/\r?\n/).filter(Boolean).map(JSON.parse);
const passed = [...new Set(tests.filter(test => test.Action === "pass" && test.Test && (!match || new RegExp(match).test(test.Test))).map(test => test.Test))];
assert.equal(tests.filter(test => test.Action === "fail").length, 0);
const assembly = path.join(option("--managed-directory", path.join(output, "phase7-build/bin/TypeScript.Compatibility/release")), "TypeScript.Compatibility.dll");
const child = spawn(dotnet, [assembly, "--syntax-services-lines"], { windowsHide: true });
const stdout = [], stderr = [];
let completed = 0, nextProgress = 400;
child.stdout.on("data", data => {
    stdout.push(data); completed += [...data].filter(value => value === 10).length;
    if (completed >= nextProgress) { console.log(`Replayed ${completed}/${cases.length} requests`); nextProgress += 400; }
});
child.stderr.on("data", data => stderr.push(data));
child.stdin.on("error", () => {}); child.stdin.end(cases.map(item => JSON.stringify(item)).join("\n") + "\n");
const exitCode = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
await writeFile(path.join(directory, "candidate.stderr"), Buffer.concat(stderr));
await writeFile(path.join(directory, "candidate.raw.jsonl"), Buffer.concat(stdout));
const actual = Buffer.concat(stdout).toString("utf8").trim().split(/\r?\n/).filter(Boolean).map(JSON.parse);
await jsonArray(path.join(directory, "candidate.json"), actual);
assert.equal(exitCode, 0, `Candidate failed at ${cases[actual.length]?.name}: ${Buffer.concat(stderr)}`);
assert.equal(actual.length, cases.length);
const differences = cases.flatMap((input, i) => {
    try { assert.deepEqual((jsdocTemplates ? comparableJSDocTemplateResult : comparableServiceResult)(input, actual[i]), (jsdocTemplates ? comparableJSDocTemplateResult : comparableServiceResult)(input, input.error ? { error: input.error } : input.result)); return []; }
    catch { return [{ input, actual: actual[i] }]; }
});
await jsonArray(path.join(directory, "differences.json"), differences);
const originalSources = new Map();
for (const file of (await run("rg", ["--files", path.join(source, "internal/fourslash/tests")])).split(/\r?\n/)
    .filter(file => file.endsWith("_test.go"))) {
    const text = await readFile(file, "utf8");
    for (const match of text.matchAll(/^func (Test\w+)\(/gm)) originalSources.set(match[1], {
        source: "tsc/" + path.relative(source, file).replaceAll("\\", "/"), sourceSha256: sha256(text) });
}
const manifest = passed.map(name => {
    const requests = cases.filter(item => item.name === name || item.name.startsWith(name + "/"));
    return { name, ...originalSources.get(name.split("/")[0]), requests: requests.length,
        methods: [...new Set(requests.map(item => item.method))] };
});
assert.ok(manifest.some(test => test.requests !== 0), "Selected tests must produce replayed requests");
assert.ok(manifest.filter(test => !test.name.startsWith("TestCSharp")).every(test => test.source), "Every original test must retain its source mapping");
await json(path.join(directory, "manifest.json"), manifest);
const summary = { referenceRevision, ...(inputDirectory !== directory ? { inputDirectory } : {}), originalTests: new Set(cases.filter(input => !input.name.startsWith("TestCSharp")).map(input => input.name)).size,
    ...(jsdocTemplates ? { scope: "Exact null and JSDoc-template completion responses plus template absence in other contexts; other provider contents remain deferred",
        exactResponseRequests: cases.filter(isTemplateRequest).length, templateAbsenceChecks: cases.filter(input => !isTemplateRequest(input)).length,
        deferredCompletionRequests: capturedInputs.trim().split(/\r?\n/).filter(Boolean).map(JSON.parse).filter(input => (!match || new RegExp(match).test(input.name)) && !isTemplateRequest(input)).length } : {}),
    originalTestFunctions: new Set(cases.filter(input => !input.name.startsWith("TestCSharp")).map(input => input.name.split("/")[0])).size,
    selectedTestsWithoutRequests: manifest.filter(test => test.requests === 0).map(test => test.name),
    unorderedProjectResults: unorderedProjectResults.filter(name => cases.some(input => input.name === name)),
    unorderedFileRenameResults: unorderedFileRenameResults.filter(name => cases.some(input => input.name === name)),
    unorderedCodeLensResults: unorderedCodeLensResults.filter(test => cases.some(input => input.name === test.name)),
    unorderedReferenceResults: unorderedReferenceResults.filter(name => cases.some(input => input.name === name)),
    authoredTests: passed.filter(name => name.startsWith("TestCSharp")).length, encodings: [...new Set(cases.map(input => input.encoding))],
    requests: cases.length, differences: differences.length,
    compilerSha256: sha256(await readFile(path.join(path.dirname(assembly), "TypeScript.Compiler.dll"))), inputsSha256: sha256(capturedInputs) };
await json(path.join(directory, "summary.json"), summary);
console.log(JSON.stringify(summary));
if (differences.length) process.exitCode = 1;

async function jsonArray(file, items) {
    const handle = await open(file, "w");
    try {
        await handle.write("[\n");
        for (let i = 0; i < items.length; i++) await handle.write(JSON.stringify(items[i]) + (i + 1 < items.length ? ",\n" : "\n"));
        await handle.write("]\n");
    } finally { await handle.close(); }
}
