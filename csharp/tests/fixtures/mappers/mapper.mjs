import { createHash } from "node:crypto";
import fs from "node:fs";
let incoming = Buffer.alloc(0), initializeCount = 0, openCount = 0, closeCount = 0, transformCount = 0, locale = "";
const projects = new Map();
function send(value) {
    const bytes = Buffer.from(JSON.stringify(value));
    process.stdout.write(`Content-Length: ${bytes.length}\r\n\r\n`);
    process.stdout.write(bytes);
}
async function request(message) {
    const p = message.params;
    process.stderr.write(JSON.stringify({ method: message.method, handle: p.projectHandle, file: p.fileName, pid: process.pid }) + "\n");
    switch (message.method) {
        case "initialize":
            initializeCount++;
            locale = p.locale ?? "";
            return { positionEncoding: process.argv[2] ?? "utf-16", diagnosticSource: process.argv[3] ?? "fixture" };
        case "openProject": {
            openCount++;
            projects.set(p.projectHandle, p);
            const options = p.options ?? {};
            if (options.openDelay) await new Promise(resolve => setTimeout(resolve, options.openDelay));
            const dynamic = options.dynamicFile ? { configIdentity: createHash("sha256").update(fs.readFileSync(options.dynamicFile)).digest("hex"), watchedFiles: [options.dynamicFile] } : {};
            return { ...dynamic, ...options.projectResult };
        }
        case "closeProject":
            closeCount++;
            projects.delete(p.projectHandle);
            return null;
        case "transform": {
            transformCount++;
            const project = projects.get(p.projectHandle);
            if (!project) throw Error("Unknown project");
            const options = project.options ?? {};
            if (p.content === "crash") process.exit(23);
            if (p.content === "wait") await new Promise(resolve => setTimeout(resolve, 100));
            if (options.result) return options.result;
            const state = { pid: process.pid, initializeCount, openCount, closeCount, transformCount, handle: p.projectHandle, locale, compilerOptions: project.compilerOptions };
            return { text: options.echo ? p.content : `export const state = ${JSON.stringify(state)};\n${options.imports ?? ""}`, extension: ".ts", mappings: options.echo ? [[0, p.content.length, 0, p.content.length, 0]] : [], supplemental: options.supplemental ? [{ text: "export const supplemental = 1;", extension: ".ts", mappings: [] }] : [] };
        }
        default:
            throw Error("Unknown method " + message.method);
    }
}
process.stdin.on("data", bytes => {
    incoming = Buffer.concat([incoming, bytes]);
    while (true) {
        const headerEnd = incoming.indexOf("\r\n\r\n");
        if (headerEnd < 0) return;
        const match = /^Content-Length: (\d+)/m.exec(incoming.subarray(0, headerEnd).toString());
        if (!match) process.exit(24);
        const length = Number(match[1]);
        if (incoming.length < headerEnd + 4 + length) return;
        const message = JSON.parse(incoming.subarray(headerEnd + 4, headerEnd + 4 + length));
        incoming = incoming.subarray(headerEnd + 4 + length);
        request(message).then(result => send({ jsonrpc: "2.0", id: message.id, result }), error => send({ jsonrpc: "2.0", id: message.id, error: { code: -32603, message: error.message } }));
    }
});
