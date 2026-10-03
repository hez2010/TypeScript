import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { output, root, run, sha256 } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.join(output, "phase7-validation/import-normalization");
await mkdir(directory, { recursive: true });
const source = path.join(directory, "main.go");
await writeFile(source, `package main
import("crypto/sha256";"encoding/binary";"fmt";"strings";"unicode";"golang.org/x/text/unicode/norm")
func main(){
 h:=sha256.New();cases:=0
 add:=func(s string){cases++;key:=strings.ToLower(strings.Map(func(r rune)rune{if unicode.Is(unicode.Mn,r){return -1};return r},norm.NFD.String(s)));for _,v:=range []string{strings.ToLower(s),key}{var n [4]byte;binary.LittleEndian.PutUint32(n[:],uint32(len(v)));h.Write(n[:]);h.Write([]byte(v))}}
 for r:=rune(0);r<=unicode.MaxRune;r++ {if r>=0xD800&&r<=0xDFFF{continue};add(string(r))}
 seed:=uint32(0x7354162a);next:=func()uint32{seed^=seed<<13;seed^=seed>>17;seed^=seed<<5;return seed}
 alphabet:=[]rune{0x41,0xe9,0x345,0x1d165,0x302e,0x302f,0x3099,0x315,0x300,0x1e9b,0x344,0x1161,0x11a8,0xac00,0x1fa,0x034f,0x0f73,0x0f75,0x0f81,0x33c4,0x1f600,0x5d0,0x5b0,0x10ffff}
 for i:=0;i<10000;i++ {r:=[]rune{};n:=int(next()%90);for j:=0;j<n;j++ {r=append(r,alphabet[next()%uint32(len(alphabet))])};add(string(r))}
 for r:=0;r<256;r++ {add(string([]byte{byte(r),0xed,0xa0,0x80,0xff}))}
 fmt.Printf("%d %x\\n",cases,h.Sum(nil))
}
`);
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const reference = await run(go, ["run", source], { cwd: path.join(root, "tsc"), env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
await writeFile(path.join(directory, "reference.txt"), reference + "\n");
const managedDirectory = path.resolve(option("--managed-directory", "built/csharp/phase7-literals-build/bin/TypeScript.Compatibility/release"));
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const candidate = await run(dotnet, [path.join(managedDirectory, "TypeScript.Compatibility.dll"), "--import-sort-normalization"]);
await writeFile(path.join(directory, "candidate.txt"), candidate + "\n");
const summary = { sourceSha256: sha256(await readFile(source)), compilerSha256: sha256(await readFile(path.join(managedDirectory, "TypeScript.Compiler.dll"))),
    reference, candidate, match: reference === candidate, scope: "All Unicode scalar values, 10,000 combining-mark/Hangul/stream-safe sequences, and 256 invalid UTF-8 sequences; lower-case text and canonical import-sort keys" };
await writeFile(path.join(directory, "summary.json"), JSON.stringify(summary, null, 2) + "\n");
console.log(JSON.stringify(summary));
if (!summary.match) process.exitCode = 1;
