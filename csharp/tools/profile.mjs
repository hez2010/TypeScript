import {
    access,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import {
    json,
    output,
    root,
    run,
    sha256,
} from "./common.mjs";

const toolVersion = "10.0.745401";
const suffix = process.platform === "win32" ? ".exe" : "";
const tool = path.join(output, "tools/dotnet-trace" + suffix);
const dotnet = process.argv[2] ?? "dotnet";
try {
    await access(tool);
}
catch {
    await run(dotnet, ["tool", "install", "dotnet-trace", "--version", toolVersion, "--tool-path", path.join(output, "tools")], { cwd: path.join(root, "csharp") });
}
const installed = await run(tool, ["--version"]);
if (!installed.startsWith(toolVersion)) throw new Error(`Expected dotnet-trace ${toolVersion}, found ${installed}`);
const trace = path.join(output, "profile.nettrace");
const log = await run(tool, ["collect", "--profile", "dotnet-sampled-thread-time,gc-verbose", "--providers", "TypeScript-Rewrite-Probe", "--format", "Speedscope", "--output", trace, "--show-child-io", "--", path.join(output, "native-host/TypeScript.Compatibility" + suffix), "--profile-workload", "3"]);
await writeFile(path.join(output, "profile.log"), log);
const profile = JSON.parse(await readFile(path.join(output, "profile.speedscope.json"), "utf8"));
const frames = profile.shared.frames.map(frame => frame.name);
await json(path.join(output, "profile-evidence.json"), {
    sdk: await run(dotnet, ["--version"], { cwd: path.join(root, "csharp") }),
    candidateSha256: sha256(await readFile(path.join(output, "native-host/TypeScript.Compatibility" + suffix))),
    instructionSet: "native",
    toolVersion: installed,
    host: `${process.platform}-${process.arch}`,
    traceSha256: sha256(await readFile(trace)),
    bytes: (await readFile(trace)).length,
    profiles: profile.profiles.length,
    frames,
    hasNamedWorkloadFrame: frames.some(frame => frame.includes("Experiments.ProfileWorkload")),
    assessment: "EventPipe transport and custom counters tested; CPU symbolization, allocation stacks, live heap sampling, and compatible profile export require further validation. This does not close the profiling gate.",
});
console.log(log);
console.log(`Profiling evidence: ${path.join(output, "profile-evidence.json")}`);
