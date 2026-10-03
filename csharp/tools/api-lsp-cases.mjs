export function lspCases() {
    const ref = name => ({ $ref: name });
    const call = (method, params = {}, save, session = "a") => ({ method, params, save, session });
    const snapshot = name => ({ snapshot: ref(`${name}.snapshot`) });
    const current = (changes = {}, save, session, base) => call("getCurrentLanguageServerSnapshot", { changes, ...(base ? { baseSnapshot: ref(`${base}.snapshot`) } : {}) }, save, session);
    const text = (name, file = "/p/a.ts", project = "/p/tsconfig.json", session) => call("getSourceFile", { ...snapshot(name), file, project }, undefined, session);
    const program = (rootFiles = ["/p/a.ts"]) => ({ rootFiles, compilerOptions: { noLib: true } });
    const editor = (kind, fileName, value, version = 1) => call("$editor", { kind, fileName, text: value, version });
    const config = files => JSON.stringify({ compilerOptions: { noLib: true }, files });
    const files = { "/p/tsconfig.json": config(["a.ts", "b.ts"]), "/p/a.ts": "export const a = 1;", "/p/b.ts": "export const b = 2;",
        "/q/tsconfig.json": config(["a.ts"]), "/q/a.ts": "export const q = 3;" };
    const cases = [
        { name: "lsp-shared-opens-and-close", files, requests: [
            current({ openProjects: ["/p/tsconfig.json"], openFiles: ["/p/a.ts"] }, "first"),
            current({ openProjects: ["/p/tsconfig.json"], openFiles: ["/p/a.ts", "/p/a.ts"] }, "repeat", "a", "first"),
            current({ openProjects: ["/p/tsconfig.json"], openFiles: ["/p/a.ts"] }, "other", "b"),
            current({ closeProjects: ["/q/tsconfig.json"], closeFiles: ["/q/a.ts"] }, "ignored", "b", "other"),
            call("$close"), current({}, "remaining", "b", "other"),
            current({ closeProjects: ["/p/tsconfig.json"], closeFiles: ["/p/a.ts"] }, "closed", "b", "remaining"),
        ] },
        { name: "lsp-editor-changes-and-pinned-branches", files, requests: [
            editor("open", "/p/a.ts", "export const 名 = '😀';"),
            current({ openFiles: ["/p/a.ts"] }, "old"), text("old"),
            editor("change", "/p/a.ts", "export const 名 = '🦋';", 2),
            current({ ensurePrograms: true }, "new", "a", "old"), text("new"), text("old"),
            call("updateSnapshot", { ...snapshot("old"), changes: { fileSystem: { kind: "layer", files: { "/p/a.ts": "export const branch = 4;" } }, ensurePrograms: true } }, "branch"),
            text("branch"), current({ ensurePrograms: true }, "canonical", "b"), text("canonical", undefined, undefined, "b"),
            call("$close"), current({ ensurePrograms: true }, "editor", "b"), text("editor", undefined, undefined, "b"),
            editor("close", "/p/a.ts"), current({}, "closed", "b", "editor"),
        ] },
        { name: "lsp-synthetic-ownership", files, requests: [
            current({ createPrograms: [program()] }, "first"),
            current({ createPrograms: [program(["/p/b.ts"])] }, "other", "b"),
            current({ removePrograms: [ref("first.operation.createdPrograms.0")] }, "ignored", "b", "other"),
            current({ reconfigurePrograms: [{ id: ref("first.operation.createdPrograms.0"), ...program(["/p/b.ts"]) }] }, undefined, "b"),
            current({ reconfigurePrograms: [{ id: ref("first.operation.createdPrograms.0"), ...program(["/p/b.ts"]) }] }, "changed"),
            text("first", "/p/a.ts", ref("first.operation.createdPrograms.0")),
            call("$close"), current({}, "after", "b", "other"),
            call("getSourceFileNames", { ...snapshot("after"), project: ref("other.operation.createdPrograms.0") }, undefined, "b"),
            current({ removePrograms: [ref("other.operation.createdPrograms.0")] }, "removed", "b", "after"),
        ] },
        { name: "lsp-synthetic-id-aliases", files, requests: [
            current({ createPrograms: [program()] }, "first"),
            current({ reconfigurePrograms: [{ id: "/dev/null/synthetic/01", ...program(["/p/b.ts"]) }] }, "changed"),
            current({ createPrograms: [program()] }, "other", "b"),
            current({ reconfigurePrograms: [{ id: "/dev/null/synthetic/+1", ...program() }] }, undefined, "b"),
            current({ removePrograms: ["/dev/null/synthetic/0001"] }, "ignored", "b", "other"),
            call("getSourceFileNames", { ...snapshot("ignored"), project: "/dev/null/synthetic/+1" }, undefined, "b"),
            current({ removePrograms: ["/dev/null/synthetic/0001"] }, "removed"),
            call("getSourceFileNames", { ...snapshot("removed"), project: "/dev/null/synthetic/02" }),
        ] },
        { name: "lsp-rejected-transaction-preserves-editor", files: { ...files, "/notes.txt": "text" }, requests: [
            current({ openProjects: ["/p/tsconfig.json"] }, "base"),
            editor("open", "/q/a.ts", "export const pending = '😀';"),
            current({ openFiles: ["/notes.txt"], createPrograms: [program()] }),
            current({ ensurePrograms: true }, "after", "a", "base"), text("after", "/q/a.ts", "/q/tsconfig.json"),
            call("release", snapshot("base")), text("after", "/q/a.ts", "/q/tsconfig.json"),
        ] },
        { name: "lsp-open-file-order-and-reopen", files, requests: [
            current({ openFiles: ["/q/a.ts", "/p/b.ts", "/q/a.ts"] }, "ordered"),
            current({ closeFiles: ["/q/a.ts", "/p/b.ts"], openFiles: ["/q/a.ts"] }, "reopen", "a", "ordered"),
            text("ordered", "/q/a.ts", "/q/tsconfig.json"),
            current({ closeFiles: ["/q/a.ts"] }, "closed", "a", "reopen"),
        ] },
        { name: "lsp-independent-root-and-lazy-ensure", files, requests: [
            current({ openProjects: ["/p/tsconfig.json"] }, "canonical"),
            call("createSnapshot", { createPrograms: [program(["/q/a.ts"])] }, "independent"),
            current({}, "current", "a", "canonical"),
            text("independent", "/q/a.ts", ref("independent.operation.createdPrograms.0")),
            editor("open", "/p/a.ts", "export const dirty = 7;"),
            current({}, "dirty", "a", "current"), current({ openProjects: ["/p/tsconfig.json"] }, "ensured", "b"),
            text("ensured", undefined, undefined, "b"), text("canonical"),
            current({ closeProjects: ["/p/tsconfig.json"], openProjects: ["/p/tsconfig.json"] }, "reopen", "a", "dirty"),
        ] },
    ];
    return cases.flatMap(input => [false, true].map(caseSensitive => ({ ...input, lsp: true, caseSensitive, name: `${input.name}-${caseSensitive}` })));
}
