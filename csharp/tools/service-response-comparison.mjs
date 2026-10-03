// The original fourslash baselines sort locations. These three cases also have
// recorded controls proving variable project/cache-directory iteration order.
// Keep every location field and duplicate count exact; other cases stay ordered.
export const unorderedProjectResults = ["TestImplementationsAcrossProjects"];
// Project telemetry embeds JSON in a string; unchanged Go runs vary its map-key order.
// Interpret only that documented field, preserving all properties, values and measurements.
export function comparableTelemetry(event) {
    if (event.eventName !== "languageServer.projectInfo") return event;
    if (typeof event.properties.compilerOptions !== "string") throw new TypeError("Expected JSON-stringified compiler options");
    return { ...event, properties: { ...event.properties, compilerOptions: JSON.parse(event.properties.compilerOptions) } };
}
// Fourslash sorts completion lists by sortText and label before checking them.
// Canonicalize distinct keys here, preserving response order within exact ties.
// Every item field, duplicate, and nested array remains unchanged.
export function orderedCompletionItems(result, input) {
    if (!Array.isArray(result?.items)) return result;
    const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;
    const items = result.items.toSorted((a, b) => compare(a.sortText, b.sortText) || compare(a.label, b.label));
    // Repeated original runs vary the two named 'defaults' entries' relative order.
    // Keep the default-import entry and every other tied group in response order.
    if (input?.name === "TestCompletionsImport_jsModuleExportsAssignment") {
        const positions = items.flatMap((item, i) => item.label === "defaults" && item.sortText === "16"
            && item.data?.autoImport?.moduleSpecifier === "./third_party/marked/src/defaults" && item.data.autoImport.importKind === 0 ? [i] : []);
        const ordered = positions.map(i => items[i]).toSorted((a, b) => compare(a.kind, b.kind));
        positions.forEach((position, i) => items[position] = ordered[i]);
    }
    return { ...result, items };
}
// A focused template audit still checks that other completion contexts never gain a template.
// Null and template responses remain exact; the contents of other providers are validated later.
export function comparableJSDocTemplateResult(input, result) {
    if (input.result === null || input.result?.items?.some(item => item.label === "/** */")) return result;
    if (result === null || Array.isArray(result?.items) && !result.items.some(item => item.label === "/** */")) return null;
    return result;
}
export const unorderedReferenceResults = ["TestTslibFindAllReferencesOnRuntimeImportWithPaths1", "TestJsxFindAllReferencesOnRuntimeImportWithPaths1"];
// Forty original runs in each encoding vary only the locations for this resolved lens.
export const unorderedCodeLensResults = [{ name: "TestCodeLensAcrossProjects", kind: "references", uri: "file:///projects/container/lib/index.ts", position: 18 }];
// Forty original runs per request establish variable map order for these file edits.
// Only contiguous edits to distinct documents commute; resource operations and edit order stay exact.
export const unorderedFileRenameResults = ["TestCSharpFileRename", "TestGetEditsForFileRename", "TestGetEditsForFileRenameWithSolutionConfigFile",
    "TestGetEditsForFileRename_directory", "TestGetEditsForFileRename_directory_down", "TestGetEditsForFileRename_directory_up",
    "TestGetEditsForFileRename_renameFromIndex", "TestGetEditsForFileRename_renameToIndex"];
export function orderedFileRenameEdits(result) {
    if (!result?.documentChanges) return result;
    const changes = result.documentChanges.slice();
    for (let i = 0; i < changes.length;) {
        if (!changes[i].textDocument) { i++; continue; }
        let end = i + 1;
        while (end < changes.length && changes[end].textDocument) end++;
        const block = changes.slice(i, end);
        if (new Set(block.map(change => change.textDocument.uri)).size === block.length)
            changes.splice(i, block.length, ...block.toSorted((a, b) => a.textDocument.uri < b.textDocument.uri ? -1 : a.textDocument.uri > b.textDocument.uri ? 1 : 0));
        i = end;
    }
    return { ...result, documentChanges: changes };
}
export function comparableServiceResult(input, result) {
    if (input.method === "textDocument/completion") return orderedCompletionItems(result, input);
    if (input.method === "codeLens/resolve" && unorderedCodeLensResults.some(test => test.name === input.name
        && test.kind === input.params?.data?.kind && test.uri === input.params?.data?.uri && test.position === input.params?.data?.position)
        && Array.isArray(result?.command?.arguments?.[2])) {
        const args = result.command.arguments.slice();
        args[2] = args[2].toSorted((left, right) => JSON.stringify(left) < JSON.stringify(right) ? -1 : JSON.stringify(left) > JSON.stringify(right) ? 1 : 0);
        return { ...result, command: { ...result.command, arguments: args } };
    }
    if (input.method === "workspace/willRenameFiles" && unorderedFileRenameResults.includes(input.name)) return orderedFileRenameEdits(result);
    if (!Array.isArray(result) || !(input.method === "textDocument/implementation" && unorderedProjectResults.includes(input.name)
        || input.method === "textDocument/references" && unorderedReferenceResults.includes(input.name))) return result;
    const key = location => JSON.stringify([location.targetUri ?? location.uri, location.targetSelectionRange ?? location.range,
        location.targetRange, location.originSelectionRange]);
    return result.toSorted((left, right) => key(left) < key(right) ? -1 : key(left) > key(right) ? 1 : 0);
}
