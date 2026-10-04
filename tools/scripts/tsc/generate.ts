import { fileURLToPath } from "node:url";
import generateEncoder from "./generate-encoder.ts";
import generateGoAST from "./generate-go-ast.ts";
import generateTSAST from "./generate-ts-ast.ts";

export default function generate() {
    generateEncoder();
    if (process.argv.includes("--oracle")) generateGoAST();
    generateTSAST();
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
    generate();
}
