package main

import (
	"bufio"
	"encoding/json"
	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/collections"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/modulespecifiers"
	"github.com/microsoft/TypeScript/tsc/internal/parser"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
	"os"
)

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 16*1024*1024)
	output := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var input struct {
			Operation, Source, FileName, Directory, Preference, OldSpecifier, Target, SourceDirectory, BaseDirectory string
			Sensitive                                                                                                bool
			DefaultMode, Mode                                                                                        core.ResolutionMode
			Files                                                                                                    map[string]string
			Options                                                                                                  json.RawMessage
			Roots                                                                                                    []string
			Endings                                                                                                  []modulespecifiers.ModuleSpecifierEnding
			Paths                                                                                                    [][2]json.RawMessage
		}
		if err := json.Unmarshal(lines.Bytes(), &input); err != nil {
			panic(err)
		}
		if input.Files == nil {
			input.Files = map[string]string{}
		}
		input.Files[input.FileName] = input.Source
		config, _ := json.Marshal(map[string]any{"compilerOptions": input.Options, "files": []string{input.FileName}})
		configPath := input.Directory + "/tsconfig.json"
		input.Files[configPath] = string(config)
		host := tsoptionstest.NewVFSParseConfigHost(input.Files, input.Directory, input.Sensitive)
		parsed, _ := tsoptions.GetParsedCommandLineOfConfigFile(configPath, nil, nil, host, nil)
		file := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: input.FileName}, input.Source, core.EnsureScriptKindFromFileName(input.FileName))
		paths := collections.OrderedMap[string, []string]{}
		for _, pair := range input.Paths {
			var key string
			var values []string
			if err := json.Unmarshal(pair[0], &key); err != nil {
				panic(err)
			}
			if err := json.Unmarshal(pair[1], &values); err != nil {
				panic(err)
			}
			paths.Set(key, values)
		}
		result := modulespecifiers.CSharpSpecifierPaths(input.Operation, file, parsed.CompilerOptions(),
			&modulespecifiers.CSharpPathHost{Directory: input.Directory, Sensitive: input.Sensitive, DefaultMode: input.DefaultMode, Exists: host.FS().FileExists},
			input.Mode, input.Preference, input.OldSpecifier, input.Target, input.SourceDirectory, input.BaseDirectory, input.Roots, input.Endings, &paths)
		if err := output.Encode(result); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}
