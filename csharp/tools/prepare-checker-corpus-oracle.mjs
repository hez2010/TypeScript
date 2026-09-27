import {
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";

// Patch only the frozen development oracle. The runner normally returns a program
// that has already emitted; retain its original semantic snapshot for Phase 4.
export async function prepareCheckerCorpusOracle(source) {
    const file = path.join(source, "internal/testutil/harnessutil/harnessutil.go");
    let text = await readFile(file, "utf8");
    if (text.includes("CSharpSemanticDiagnostics")) return;
    const replacements = [
        ["preErrors = append(preErrors, preProgram.GetSemanticDiagnostics(ctx, nil)...)", "preSemantic := preProgram.GetSemanticDiagnostics(ctx, nil)\n\tpreErrors = append(preErrors, preSemantic...)"],
        ["preErrors = append(preErrors, preProgram.GetGlobalDiagnostics(ctx)...)", "preGlobal := preProgram.GetGlobalDiagnostics(ctx)\n\tpreErrors = append(preErrors, preGlobal...)"],
        ["return newCompilationResult(host, config.CompilerOptions(), postProgram, emitResult, errors, harnessOptions)", "result := newCompilationResult(host, config.CompilerOptions(), postProgram, emitResult, errors, harnessOptions)\n\tresult.CSharpSemanticDiagnostics = preSemantic\n\tresult.CSharpGlobalDiagnostics = preGlobal\n\treturn result"],
        ["type CompilationResult struct {", "type CompilationResult struct {\n\tCSharpSemanticDiagnostics []*ast.Diagnostic\n\tCSharpGlobalDiagnostics []*ast.Diagnostic"],
    ];
    for (const [before, after] of replacements) {
        if (text.split(before).length !== 2) throw Error(`Oracle snapshot patch does not match: ${before}`);
        text = text.replace(before, after);
    }
    await writeFile(file, text);
}
