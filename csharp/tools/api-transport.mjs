import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import net from "node:net";
import path from "node:path";
import { root, output, run, json, referenceRevision, sha256 } from "./common.mjs";

const dotnet = "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe";
const go = "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe";
const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase7-validation/api-transport"))); await mkdir(directory, { recursive: true });
if (!process.argv.includes("--no-prepare")) await run(process.execPath, ["csharp/tools/api-session.mjs"]);
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8")); assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const cmd = path.join(source, "cmd/csharp-api-server"); await mkdir(cmd, { recursive: true });
await copyFile(path.join(root, "csharp/oracle/api-server/main.go"), path.join(cmd, "main.go"));
const executable = path.join(directory, "oracle.exe");
await run(go, ["-C", source, "build", "-o", executable, "./cmd/csharp-api-server"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const cli = path.join(option("--managed-directory", path.join(output, "phase7-build/bin/TypeScript.CommandLine/release")), "tsgo-cs.dll");
const cwd = directory.replaceAll("\\", "/");
const disk = `${cwd}/disk.ts`; await writeFile(disk, "export const disk = '日本語😀';");
const virtual = `${cwd}/virtual.ts`, missing = `${cwd}/missing.ts`, throws = `${cwd}/throws.ts`;
const bom = `${cwd}/bom.ts`;
const bomConfig = `${cwd}/bomconfig.json`;

function bin(bytes) {
    const width = bytes.length < 256 ? 1 : bytes.length < 65536 ? 2 : 4;
    const header = Buffer.alloc(width + 1); header[0] = width === 1 ? 0xC4 : width === 2 ? 0xC5 : 0xC6;
    header.writeUIntBE(bytes.length, 1, width); return Buffer.concat([header, bytes]);
}
function tuple(type, method, payload) { return Buffer.concat([Buffer.from([0x93, type]), bin(Buffer.from(method)), bin(Buffer.from(payload))]); }
function readBin(buffer, offset) {
    if (offset >= buffer.length) return;
    const width = buffer[offset] === 0xC4 ? 1 : buffer[offset] === 0xC5 ? 2 : buffer[offset] === 0xC6 ? 4 : 0;
    assert(width, "Expected binary field"); if (offset + width + 1 > buffer.length) return;
    const length = buffer.readUIntBE(offset + 1, width), end = offset + 1 + width + length;
    if (end > buffer.length) return; return { bytes: buffer.subarray(offset + 1 + width, end), end };
}
class Peer {
    constructor(stream, write, async, callback) {
        this.stream = stream; this.write = write; this.async = async; this.callback = callback;
        this.pending = new Map(); this.sequence = 0; this.buffer = Buffer.alloc(0); this.frames = []; this.bytes = [];
        stream.on("data", bytes => { this.bytes.push(bytes); this.buffer = Buffer.concat([this.buffer, bytes]); try { this.parse(); } catch (e) { this.fail(e); } });
        stream.on("error", error => this.fail(error));
        stream.on("end", () => this.fail(new Error("Peer closed the connection")));
    }
    fail(error) { this.failure ??= error; for (const entry of this.pending.values()) { clearTimeout(entry.timer); entry.reject(error); } this.pending.clear(); }
    parse() {
        while (this.buffer.length) {
            if (this.async) {
                const header = this.buffer.indexOf("\r\n\r\n"); if (header < 0) return;
                const match = /^Content-Length: (\d+)$/m.exec(this.buffer.subarray(0, header).toString()); assert(match);
                const length = Number(match[1]), end = header + 4 + length; if (end > this.buffer.length) return;
                const message = JSON.parse(this.buffer.subarray(header + 4, end)); this.buffer = this.buffer.subarray(end);
                this.frames.push(message); this.dispatch(message.id, message.method, message.method === undefined ? message.result : message.params, message.error?.message);
            } else {
                if (this.buffer.length < 2) return; assert.equal(this.buffer[0], 0x93); const type = this.buffer[1];
                const method = readBin(this.buffer, 2); if (!method) return; const data = readBin(this.buffer, method.end); if (!data) return;
                this.buffer = this.buffer.subarray(data.end); const name = method.bytes.toString();
                this.frames.push({ type, method: name, data: data.bytes.toString("base64") });
                if (type === 6) this.dispatch(name, name, JSON.parse(data.bytes));
                else { assert(type === 4 || type === 5); this.dispatch(name, undefined, data.bytes, type === 5 ? data.bytes.toString() : undefined); }
            }
        }
    }
    dispatch(id, method, value, error) {
        if (method !== undefined) {
            Promise.resolve().then(() => this.callback(method, value, this)).then(
                result => this.sendResponse(id, method, result), reason => this.sendResponse(id, method, undefined, String(reason.message ?? reason)))
                .catch(reason => this.fail(reason));
            return;
        }
        const entry = this.pending.get(id); assert(entry, `Unmatched response ${id}`); this.pending.delete(id); clearTimeout(entry.timer);
        if (error !== undefined) entry.resolve({ error });
        else entry.resolve({ result: entry.raw && !this.async ? { data: value.toString("base64") } : this.async ? value : JSON.parse(value) });
    }
    sendResponse(id, method, result, error) {
        if (this.async) this.sendJson({ jsonrpc: "2.0", id, ...(error === undefined ? { result: result ?? null } : { error: { code: -32603, message: error } }) });
        else this.write(tuple(error === undefined ? 2 : 3, method, error === undefined ? JSON.stringify(result ?? null) : error));
    }
    sendJson(message, fragment = false) {
        const data = Buffer.from(JSON.stringify(message)); const frame = Buffer.concat([Buffer.from(`Content-Length: ${data.length}\r\n\r\n`), data]);
        if (fragment) for (let i = 0; i < frame.length; i += 3) this.write(frame.subarray(i, i + 3)); else this.write(frame);
    }
    call(method, params = {}, { raw = false, fragment = false, id, payload } = {}) {
        if (this.failure) return Promise.reject(this.failure);
        id ??= this.async ? ++this.sequence : method; assert(!this.pending.has(id));
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => this.fail(new Error(`Timed out waiting for ${method}`)), 20000);
            this.pending.set(id, { resolve, reject, timer, raw });
            if (this.async) this.sendJson({ jsonrpc: "2.0", id, method, params }, fragment);
            else {
                const frame = tuple(1, method, payload ?? JSON.stringify(params));
                if (fragment) for (let i = 0; i < frame.length; i += 3) this.write(frame.subarray(i, i + 3)); else this.write(frame);
            }
        });
    }
}
async function connect(pipe) {
    for (let attempt = 0; attempt < 100; attempt++) {
        try { return await new Promise((resolve, reject) => { const socket = net.connect(pipe); socket.once("connect", () => resolve(socket)); socket.once("error", reject); }); }
        catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    throw new Error(`Pipe was not available: ${pipe}`);
}
function normalize(results) {
    const identities = new Map(); let next = 0;
    return results.map(record => {
        if (record.method === "callback-error") {
            assert(record.value.error?.includes("ipc: remote error [-32603]: callback test failure"));
            return { ...record, value: { error: "ipc: remote error [-32603]: callback test failure" } };
        }
        if (record.method.includes("Timing")) {
            const result = structuredClone(record.value);
            if (result.result?.totals) {
                assert(result.result.totals.totalProcessingTimeMs >= 0);
                result.result.totals.totalProcessingTimeMs = "nonnegative duration";
                for (const sample of result.result.recentRequests) {
                    assert(sample.processingTimeMs >= 0 && sample.timestamp > 0);
                    sample.processingTimeMs = "nonnegative duration"; sample.timestamp = "positive timestamp";
                }
            }
            return { ...record, value: result };
        }
        const normalized = structuredClone(record), result = normalized.value?.result;
        if ((record.method === "createSnapshot" || record.method === "updateSnapshot") && typeof result?.snapshot === "number") {
            if (!identities.has(result.snapshot)) identities.set(result.snapshot, ++next);
            result.snapshot = identities.get(result.snapshot);
        }
        return normalized;
    });
}
async function scenario(label, async, pipeMode) {
    const name = `${label}-${async ? "jsonrpc" : "msgpack"}-${pipeMode ? "pipe" : "stdio"}`;
    const pipe = process.platform === "win32" ? `\\\\.\\pipe\\csharp-api-${process.pid}-${name}` : `/tmp/csharp-api-${process.pid}-${name}`;
    const args = ["--cwd", cwd, "--callbacks", "readFile,fileExists,directoryExists,getAccessibleEntries,realpath", "--timing", ...(async ? ["--async"] : []), ...(pipeMode ? ["--pipe", pipe] : [])];
    const child = spawn(label === "reference" ? executable : dotnet, label === "reference" ? args : [cli, "--api", ...args], { cwd: root, windowsHide: true });
    let stderr = ""; child.stderr.on("data", bytes => stderr += bytes); child.stdin.on("error", () => {});
    const closed = new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    let socket, peer; const trace = [], responses = [];
    try {
        socket = pipeMode ? await connect(pipe) : undefined;
        let nestedTested = false;
        const callback = async (method, value, channel) => {
            trace.push({ method, value });
            if (method === "readFile" && value === throws) throw new Error("callback test failure");
            if (method === "readFile" && value === missing) return { content: null };
            if (method === "readFile" && value === bom) return { content: "\ufeffexport const bom = 1;" };
            if (method === "readFile" && value === bomConfig) return { content: '\ufeff{"compilerOptions":{"noLib":true,"bad":true},"files":["bom.ts"]}' };
            if (method === "readFile" && value === virtual) {
                if (async && !nestedTested) { nestedTested = true; assert.deepEqual(await channel.call("ping", {}, { id: "inside-callback" }), { result: "pong" }); }
                return { content: "export const 日本語 = '😀';" };
            }
            if (method === "fileExists" && (value === virtual || value === bom || value === bomConfig)) return true;
            if (method === "getAccessibleEntries" && value === cwd) return { files: ["virtual.ts", "disk.ts"], directories: [] };
            return null;
        };
        peer = new Peer(socket ?? child.stdout, bytes => (socket ?? child.stdin).write(bytes), async, callback);
        const call = async (method, params = {}, options = {}, label = method) => {
            const value = await peer.call(method, params, options); responses.push({ method: label, value }); return value;
        };
        await call("initialize", {}, { fragment: true }); await call("ping");
        await call("echo", { text: (async ? "日本語😀" : "日本語😀\ud800").repeat(12000), numbers: [1, -1, 2147483647] });
        if (!async) {
            await call("echo", null, { raw: true, payload: Buffer.from([0, 0xc4, 0xff, 0xe0, 0x80, 0x80]) }, "binary-echo");
            await call("echo", null, { raw: true, payload: Buffer.alloc(0) }, "empty-binary-echo");
        }
        await call("unknown"); await call("ping");
        await call("createSourceFile", { fileName: virtual, sourceText: "const 日本語 = '😀';" }, { raw: true, fragment: true });
        await call("createSourceFileFromFile", { fileName: virtual }, { raw: true });
        await call("createSourceFileFromFile", { fileName: disk }, { raw: true });
        await call("createSourceFileFromFile", { fileName: bom }, { raw: true });
        await call("createSourceFileFromFile", { fileName: missing });
        await call("createSourceFileFromFile", { fileName: throws }, {}, "callback-error");
        await call("ping");
        const snapshot = (await call("createSnapshot", { createPrograms: [{ rootFiles: [virtual, bom], compilerOptions: { noLib: true } }] })).result;
        assert(snapshot); const project = snapshot.operation.createdPrograms[0];
        await call("getSourceFileNames", { snapshot: snapshot.snapshot, project });
        await call("getSourceFile", { snapshot: snapshot.snapshot, project, file: virtual }, { raw: true });
        await call("getSourceFile", { snapshot: snapshot.snapshot, project, file: bom }, { raw: true });
        await call("release", { snapshot: snapshot.snapshot });
        const configured = (await call("createSnapshot", { openProjects: [bomConfig] })).result;
        await call("release", { snapshot: configured.snapshot });
        await call("getServerTiming"); await call("resetServerTiming"); await call("getServerTiming");
        if (async) {
            const values = await Promise.all(Array.from({ length: 40 }, (_, i) => peer.call("echo", { i }, { id: `parallel-${i}` })));
            values.forEach((value, i) => assert.deepEqual(value, { result: { i } }));
            responses.push({ method: "parallel-echo", value: values });
        }
        (socket ?? child.stdin).end(); assert.equal(await closed, 0, stderr);
        return responses;
    } finally {
        socket?.destroy(); if (child.exitCode === null) { child.kill(); await closed; }
        if (peer) { for (const entry of peer.pending.values()) clearTimeout(entry.timer); await writeFile(path.join(directory, `${name}.wire`), Buffer.concat(peer.bytes)); await json(path.join(directory, `${name}.frames.json`), peer.frames); }
        await writeFile(path.join(directory, `${name}.stderr.txt`), stderr); await json(path.join(directory, `${name}.callbacks.json`), trace);
        await json(path.join(directory, `${name}.responses.json`), responses);
    }
}
let requests = 0;
for (const pipe of [false, true]) for (const async of [false, true]) {
    const expected = await scenario("reference", async, pipe), actual = await scenario("candidate", async, pipe);
    assert.deepEqual(normalize(actual), normalize(expected)); requests += actual.length;
    console.log(`${async ? "JSON-RPC" : "MessagePack"} ${pipe ? "pipe" : "stdio"}: ${actual.length} response groups matched`);
}
const controls = [];
for (const [name, input, expect] of [
    ["jsonrpc-lone-surrogate", (() => { const body = Buffer.from('{"jsonrpc":"2.0","id":1,"method":"echo","params":"\\ud800"}'); return Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`), body]); })(), "preserve-wtf8"],
    ["jsonrpc-truncated-header", Buffer.from("Content-Length: 4"), "reject-truncated-frame"],
]) {
    const record = { name, policy: expect, inputSha256: sha256(input), inputBase64: input.toString("base64") };
    for (const label of ["reference", "candidate"]) {
        const args = ["--async", "--cwd", cwd];
        const child = spawn(label === "reference" ? executable : dotnet, label === "reference" ? args : [cli, "--api", ...args], { windowsHide: true });
        const stdout = [], stderr = []; child.stdout.on("data", bytes => {
            stdout.push(bytes);
            const content = Buffer.concat(stdout), header = content.indexOf("\r\n\r\n");
            if (header >= 0) {
                const length = /^Content-Length: (\d+)$/m.exec(content.subarray(0, header).toString());
                if (length && content.length >= header + 4 + Number(length[1])) child.stdin.end();
            }
        }); child.stderr.on("data", bytes => stderr.push(bytes));
        const done = new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
        child.stdin.on("error", () => {});
        if (name === "jsonrpc-truncated-header") child.stdin.end(input); else child.stdin.write(input);
        const code = await done, result = Buffer.concat(stdout), error = Buffer.concat(stderr).toString();
        record[label] = { exitCode: code, stdoutBase64: result.toString("base64"), stderr: error };
        await json(path.join(directory, `${name}-control.json`), record);
        if (name === "jsonrpc-lone-surrogate") {
            if (label === "reference") { assert.equal(code, 1); assert.match(error, /invalid surrogate pair/); assert.equal(result.length, 0); }
            else { assert.equal(code, 0); assert.equal(JSON.parse(result.subarray(result.indexOf("\r\n\r\n") + 4)).result, "\ud800"); }
        } else {
            assert.equal(code, label === "reference" ? 0 : 1); assert.equal(result.length, 0);
            if (label === "candidate") assert.match(error, /Truncated RPC header/);
        }
    }
    controls.push(record);
}
await json(path.join(directory, "difference-controls.json"), controls);
const ledger = JSON.parse(await readFile(path.join(root, "csharp/tests/fixtures/protocol/rpc-differences.json"), "utf8"));
assert.equal(ledger.referenceRevision, referenceRevision);
assert.deepEqual(controls, ledger.cases.map(({ rationale, reproduction, independentControl, ...control }) => control));
await json(path.join(directory, "summary.json"), { referenceRevision, requests, transportPairs: 4, differences: 0,
    explicitDifferenceControls: controls.map(({ name, inputSha256 }) => ({ name, inputSha256 })),
    compilerSha256: sha256(await readFile(path.join(path.dirname(cli), "TypeScript.Compiler.dll"))),
    normalization: ["snapshot identities retain allocation order", "timing durations and timestamps checked before replacement", "Go panic stack removed only for the exact callback test failure; complete messages retained"],
    adapter: "The API/session oracle adapts positions and AST protocol to the C# UTF-8 contract; transport and callback algorithms are original Go." });
