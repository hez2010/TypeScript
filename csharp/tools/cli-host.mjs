import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { json, output, referenceRevision, root, run, sha256 } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase8-validation/cli-host")));
const managed = path.resolve(option("--managed-directory", path.join(output, "phase8-build/bin/TypeScript.CommandLine/release")));
const candidate = path.join(managed, "tsgo-cs.exe");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const oracle = path.join(directory, "oracle.exe");
await mkdir(directory, { recursive: true });
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
if (!process.argv.includes("--no-build")) await run(go, ["-C", path.join(output, reference.sourceRelativePath, "tsc"),
    "build", "-tags=bundled", "-o", oracle, "./cmd/tsc"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const env = { ...process.env, DOTNET_ROOT: option("--dotnet-root", process.env.DOTNET_ROOT ?? "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64") };

function launch(executable, args, timeout = 30000) {
    const child = spawn(executable, args, { cwd: directory, windowsHide: true, env });
    let stdout = "", stderr = "", timedOut = false;
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    child.stdin.on("error", () => {});
    const timer = setTimeout(() => { timedOut = true; child.kill(); }, timeout);
    const exited = new Promise((resolve, reject) => {
        child.on("error", error => { clearTimeout(timer); reject(error); });
        child.on("close", (exitCode, signal) => { clearTimeout(timer); resolve({ exitCode, signal, timedOut, stdout, stderr }); });
    });
    return { child, exited };
}

const flags = [[], ["--pipe", "unused"], ["--stdio=false"], ["--stdio=invalid"], ["--missing"],
    ["--pprofDir"], ["--clientProcessId"], ["--stdio", "--clientProcessId", "invalid"],
    ["--stdio", "--clientProcessId=0"], ["--stdio", "--socket", "unused"], ["--stdio=true"], ["-stdio"],
    ["--stdio", "--", "ignored"], ["--stdio", "ignored", "--invalid"], ["--help"],
    ["---stdio"], ["-"], ["--stdio", "-", "--bad"], ["-=stdio"],
    ...["0x0", "00", "08", "0_0", "0b0", "0o0", "0x_0", "0__0", "0_", "_0", "0x", "0x_",
        "2147483648", "9223372036854775807", "9223372036854775808", "-9223372036854775808", "-9223372036854775809", " 0", "+0"].map(value => ["--stdio", `--clientProcessId=${value}`])];
const flagResults = [];
const helpResults = [];
for (const args of [["--help"], ["--all"], ["--help", "--locale", "ja"], ["--build", "--help"], ["--version"]]) {
    const pair = await Promise.all([oracle, candidate].map(async executable => {
        const process = launch(executable, args); process.child.stdin.end(); return await process.exited;
    }));
    assert.equal(pair[1].exitCode, pair[0].exitCode); assert.equal(pair[1].stdout.replaceAll("\r\n", "\n"), pair[0].stdout.replaceAll("\r\n", "\n"));
    assert.equal(pair[1].stderr, pair[0].stderr);
    helpResults.push({ args, stdoutSha256: sha256(pair[1].stdout) });
}
await json(path.join(directory, "help.json"), helpResults);
for (const args of flags) {
    const pair = await Promise.all([oracle, candidate].map(async executable => {
        const process = launch(executable, ["--lsp", ...args], 10000); process.child.stdin.end(); return await process.exited;
    }));
    flagResults.push({ args, reference: pair[0], candidate: pair[1] });
}
await json(path.join(directory, "flags.json"), flagResults);
const flagDifferences = flagResults.filter(result => result.reference.exitCode !== result.candidate.exitCode || result.candidate.timedOut);
await json(path.join(directory, "flag-differences.json"), flagDifferences);
assert.equal(flagDifferences.length, 0, "LSP command-line exit status");

async function session(executable, label, args = [], parentId, parentEnded) {
    const process = launch(executable, ["--lsp", "--stdio", ...args]);
    const pending = new Map(), frames = [];
    let nextId = 0, buffer = Buffer.alloc(0);
    const send = message => {
        const bytes = Buffer.from(JSON.stringify({ jsonrpc: "2.0", ...message }));
        process.child.stdin.write(Buffer.concat([Buffer.from(`Content-Length: ${bytes.length}\r\n\r\n`), bytes]));
    };
    const call = (method, params) => new Promise((resolve, reject) => {
        const id = ++nextId; pending.set(id, { resolve, reject }); send({ id, method, params });
    });
    process.exited.then(result => {
        for (const { reject } of pending.values()) reject(new Error(`${label} exited: ${JSON.stringify(result)}`)); pending.clear();
    });
    process.child.stdout.on("data", data => {
        buffer = Buffer.concat([buffer, data]);
        for (;;) {
            const header = buffer.indexOf("\r\n\r\n"); if (header < 0) break;
            const match = /Content-Length: (\d+)/i.exec(buffer.subarray(0, header).toString()); assert.ok(match);
            const end = header + 4 + Number(match[1]); if (buffer.length < end) break;
            const message = JSON.parse(buffer.subarray(header + 4, end)); buffer = buffer.subarray(end); frames.push(message);
            if (message.method) { if (message.id !== undefined) send({ id: message.id, result: message.method === "workspace/configuration" ? [null, null, null, null] : null }); }
            else { const request = pending.get(message.id); pending.delete(message.id); assert.ok(request); request.resolve(message); }
        }
    });
    try {
        const initialized = await call("initialize", { processId: parentId ?? null, rootUri: pathToFileURL(directory + path.sep).href,
            capabilities: {}, initializationOptions: { disablePushDiagnostics: true, logVerbosity: 0 } });
        assert.ok(!initialized.error, JSON.stringify(initialized));
        send({ method: "initialized", params: {} });
        if (parentEnded) {
            await parentEnded();
            const result = await process.exited;
            assert.equal(result.timedOut, false); assert.equal(result.exitCode, label.startsWith("reference") ? 1 : 0);
            if (label.startsWith("reference")) assert.match(result.stderr, /context canceled/);
            assert.match(result.stderr, /Parent process \d+ has exited/);
            await json(path.join(directory, `${label}.json`), { initialized, frames, result }); return result;
        }
        const file = path.join(directory, "index.ts"), text = "export const café: string = 1;\n";
        await writeFile(file, text);
        const uri = pathToFileURL(file).href;
        send({ method: "textDocument/didOpen", params: { textDocument: { uri, languageId: "typescript", version: 1, text } } });
        const diagnostics = await call("textDocument/diagnostic", { textDocument: { uri } });
        assert.ok(!diagnostics.error, JSON.stringify(diagnostics));
        assert.ok(diagnostics.result.items.some(item => item.code === 2322));
        const shutdown = await call("shutdown"); assert.ok(!shutdown.error);
        send({ method: "exit" }); process.child.stdin.end();
        const result = await process.exited;
        assert.equal(result.exitCode, label === "reference" ? 1 : 0, result.stderr); assert.equal(result.timedOut, false);
        if (label === "reference") assert.match(result.stderr, /context canceled/);
        await json(path.join(directory, `${label}.json`), { initialized, diagnostics, frames, result });
        return { version: initialized.result.serverInfo.version, diagnostics: diagnostics.result, result };
    }
    finally { if (process.child.exitCode === null) process.child.kill(); }
}

const sessions = [];
for (const [executable, label] of [[oracle, "reference"], [candidate, "candidate"]]) {
    const profiles = path.join(directory, label + "-profiles");
    sessions.push(await session(executable, label, ["--pprofDir", profiles]));
    const files = await readdir(profiles);
    for (const kind of ["cpu", "mem"]) {
        const file = files.find(file => file.endsWith(`-${kind}profile.pb.gz`)); assert.ok(file);
        const profile = path.join(profiles, file);
        const raw = await run(go, ["tool", "pprof", "-raw", profile]);
        assert.match(raw, kind === "cpu" ? /cpu\/nanoseconds/ : /alloc_space\/bytes/);
        await writeFile(path.join(directory, `${label}-${kind}-pprof.txt`), raw);
    }
    for (const override of [false, true]) {
        const parent = spawn(process.execPath, ["-e", "process.stdin.resume()"], { windowsHide: true });
        const parentExit = new Promise(resolve => parent.on("close", resolve));
        try {
            await session(executable, `${label}-parent-${override ? "flag" : "initialize"}`,
                override ? ["--clientProcessId", String(parent.pid)] : [], override ? 0 : parent.pid,
                async () => { parent.stdin.end(); await parentExit; });
        }
        finally { if (parent.exitCode === null) parent.kill(); }
    }
}
assert.equal(sessions[0].version, sessions[1].version);
assert.deepEqual(sessions[0].diagnostics, sessions[1].diagnostics);
const summary = { referenceRevision, helpCases: helpResults.length, flagCases: flags.length, liveSessions: 2, parentWatchdogs: 4, profilesReadByPprof: 4, differences: 0,
    classifiedDifferences: [{ scope: "Orderly LSP shutdown and parent watchdog cancellation", referenceExitCode: 1, candidateExitCode: 0,
        reason: "The original Go executable reports context canceled after successful shutdown/exit or expected parent cancellation. C# treats the requested shutdown as success." }],
    compilerSha256: sha256(await readFile(path.join(managed, "TypeScript.Compiler.dll"))),
    cliSha256: sha256(await readFile(path.join(managed, "tsgo-cs.dll"))), oracleSha256: sha256(await readFile(oracle)),
    profileContract: "Real process counters and cooperative compiler scopes; CLR allocation bytes are not Go allocation counts or statistical stack samples" };
await json(path.join(directory, "summary.json"), summary); console.log(JSON.stringify(summary));
