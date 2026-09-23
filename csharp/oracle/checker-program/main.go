package main

import (
	"bufio"
	"encoding/base64"
	"encoding/json"
	"os"

	"github.com/microsoft/TypeScript/tsc/internal/checker"
	"github.com/microsoft/TypeScript/tsc/internal/compiler"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
)

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 128*1024*1024)
	output := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var input struct {
			Files       map[string]string
			Roots       []string
			Options     map[string]any
			Concurrency int
			Aliases     bool
		}
		if err := json.Unmarshal(lines.Bytes(), &input); err != nil {
			panic(err)
		}
		files := map[string]string{}
		for name, text := range input.Files {
			data, err := base64.StdEncoding.DecodeString(text)
			if err != nil {
				panic(err)
			}
			files[name] = string(data)
		}
		if input.Options == nil {
			input.Options = map[string]any{}
		}
		input.Options["noLib"] = true
		config, _ := json.Marshal(map[string]any{"compilerOptions": input.Options, "files": input.Roots})
		files["/project/tsconfig.json"] = string(config)
		host := tsoptionstest.NewVFSParseConfigHost(files, "/project", true)
		parsed, _ := tsoptions.GetParsedCommandLineOfConfigFile("/project/tsconfig.json", nil, nil, host, nil)
		options := compiler.ProgramOptions{Config: parsed, Host: compiler.NewCompilerHost("/project", host.FS(), "", nil, nil, nil)}
		if input.Concurrency == 1 {
			options.SingleThreaded = core.TSTrue
		}
		program := compiler.NewProgram(options)
		c, _ := checker.NewChecker(program, nil)
		if err := output.Encode(c.CSharpProgramScopeProbe(input.Aliases)); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}
