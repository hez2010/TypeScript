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
		var input struct {
			Strict, Exact bool
			Steps         []checker.CSharpTypeStep
			Mappers       []checker.CSharpMapperStep
			Queries       []checker.CSharpMapperQuery
			Resolutions   []checker.CSharpResolutionOperation
			Comparisons   [][]int
		}
		if err := json.Unmarshal(lines.Bytes(), &input); err != nil {
			panic(err)
		}
		configJSON, _ := json.Marshal(map[string]any{"compilerOptions": map[string]any{"noLib": true, "strictNullChecks": input.Strict, "exactOptionalPropertyTypes": input.Exact}, "files": []string{"/input.ts"}})
		host := tsoptionstest.NewVFSParseConfigHost(map[string]string{"/tsconfig.json": string(configJSON), "/input.ts": ""}, "/", true)
		config, errors := tsoptions.GetParsedCommandLineOfConfigFile("/tsconfig.json", nil, nil, host, nil)
		if len(errors) != 0 {
			panic(errors)
		}
		program := compiler.NewProgram(compiler.ProgramOptions{Config: config, Host: compiler.NewCompilerHost("/", host.FS(), "", nil, nil, nil)})
		c, _ := checker.NewChecker(program, nil)
		var result any
		if input.Resolutions != nil {
			result = c.CSharpResolutionProbe(input.Resolutions)
		} else {
			result = c.CSharpTypeProbe(input.Steps, input.Mappers, input.Queries, input.Comparisons)
		}
		if err := out.Encode(result); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}
