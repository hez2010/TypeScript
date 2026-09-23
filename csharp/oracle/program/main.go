package main

import (
	"bufio"
	"encoding/json"
	"os"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/bundled"
	"github.com/microsoft/TypeScript/tsc/internal/compiler"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
)

type request struct {
	Name, Directory, Config string
	Files, Symlinks         map[string]string
	Options                 json.RawMessage
	Roots                   []string
	Sensitive, UseSources   bool
	Concurrency             int
}

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 128*1024*1024)
	out := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var input request
		if err := json.Unmarshal(lines.Bytes(), &input); err != nil {
			panic(err)
		}
		if err := out.Encode(process(input)); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}

func process(input request) any {
	if input.Files == nil {
		input.Files = map[string]string{}
	}
	if input.Config == "" {
		input.Config = input.Directory + "/tsconfig.json"
		bytes, _ := json.Marshal(map[string]any{"compilerOptions": input.Options, "files": input.Roots})
		input.Files[input.Config] = string(bytes)
	}
	host := tsoptionstest.NewVFSParseConfigHostWithSymlinks(input.Files, input.Symlinks, input.Directory, input.Sensitive)
	host.Vfs = bundled.WrapFS(host.Vfs)
	config, errors := tsoptions.GetParsedCommandLineOfConfigFile(input.Config, nil, nil, host, nil)
	if config == nil {
		panic("invalid root config")
	}
	options := compiler.ProgramOptions{Config: config, Host: compiler.NewCompilerHost(input.Directory, host.FS(), bundled.LibPath(), nil, nil, nil), UseSourceOfProjectReference: input.UseSources}
	if input.Concurrency == 1 {
		options.SingleThreaded = core.TSTrue
	}
	program := compiler.NewProgram(options)
	program.BindSourceFiles()
	files := []any{}
	for _, file := range program.GetSourceFiles() {
		meta := program.GetSourceFileMetaData(file.Path())
		imports := []any{}
		for _, node := range file.Imports() {
			r := program.GetResolvedModuleFromModuleSpecifier(file, node)
			target := ""
			if r != nil {
				target = r.ResolvedFileName
			}
			imports = append(imports, []any{node.Text(), target})
		}
		files = append(files, []any{file.FileName(), ast.IsExternalOrCommonJSModule(file), meta.ImpliedNodeFormat, meta.PackageJsonType, imports})
	}
	codes := []int32{}
	for _, d := range append(append(errors, config.Errors...), program.GetProgramDiagnostics()...) {
		codes = append(codes, d.Code())
	}
	for _, file := range program.GetSourceFiles() {
		for _, d := range program.GetIncludeProcessorDiagnostics(file) {
			codes = append(codes, d.Code())
		}
	}
	return []any{files, codes}
}
