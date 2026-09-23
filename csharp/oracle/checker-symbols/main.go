package main

import (
	"bufio"
	"encoding/json"
	"os"

	"github.com/microsoft/TypeScript/tsc/internal/checker"
	"github.com/microsoft/TypeScript/tsc/internal/compiler"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
)

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 128*1024*1024)
	out := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var input checker.CSharpSymbolInput
		if err := json.Unmarshal(lines.Bytes(), &input); err != nil {
			panic(err)
		}
		host := tsoptionstest.NewVFSParseConfigHost(map[string]string{"/tsconfig.json": `{"compilerOptions":{"noLib":true},"files":["/input.ts"]}`, "/input.ts": ""}, "/", true)
		config, _ := tsoptions.GetParsedCommandLineOfConfigFile("/tsconfig.json", nil, nil, host, nil)
		program := compiler.NewProgram(compiler.ProgramOptions{Config: config, Host: compiler.NewCompilerHost("/", host.FS(), "", nil, nil, nil)})
		c, _ := checker.NewChecker(program, nil)
		if err := out.Encode(c.CSharpMergeSymbols(input)); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}
