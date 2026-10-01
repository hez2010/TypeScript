package main

import (
	"bufio"
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/bundled"
	"github.com/microsoft/TypeScript/tsc/internal/compiler"
	"github.com/microsoft/TypeScript/tsc/internal/contentmapper"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/locale"
	"github.com/microsoft/TypeScript/tsc/internal/testutil/contentmappertest"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
	"github.com/microsoft/TypeScript/tsc/internal/tspath"
	"os"
	"sort"
	"strings"
	"sync"
)

type request struct {
	Text            string            `json:"text"`
	File            string            `json:"file"`
	Cwd             string            `json:"cwd"`
	Files           map[string]string `json:"files"`
	Options         map[string]any    `json:"options"`
	Roots           []string          `json:"roots"`
	Targets         []string          `json:"targets"`
	Only            compiler.EmitOnly `json:"only"`
	Force           bool              `json:"force"`
	CaseInsensitive bool              `json:"caseInsensitive"`
	FailWrite       string            `json:"failWrite"`
	SkipWrite       string            `json:"skipWrite"`
	CheckOnly       string            `json:"checkOnly"`
	Corpus          bool              `json:"corpus"`
	ConfigFileName  string            `json:"configFileName"`
	Symlinks        map[string]string `json:"symlinks"`
	ContentMappers  int               `json:"contentMappers"`
	Config          string            `json:"config"`
}

func diagnosticText(item *ast.Diagnostic, depth int) string {
	text := item.String()
	for _, child := range item.MessageChain() {
		text += "\n" + strings.Repeat("  ", depth+1) + diagnosticText(child, depth+1)
	}
	return text
}

func diagnostics(items []*ast.Diagnostic) []map[string]any {
	result := make([]map[string]any, 0, len(items))
	for _, item := range items {
		file := ""
		if item.File() != nil {
			file = item.File().FileName()
		}
		result = append(result, map[string]any{"file": file, "code": item.Code(), "start": item.Pos(), "length": item.Len(),
			"message": diagnosticText(item, 0), "related": diagnostics(item.RelatedInformation())})
	}
	return result
}

func emit(input request) (result any) {
	defer func() {
		if value := recover(); value != nil {
			result = map[string]any{"error": fmt.Sprint(value)}
		}
	}()
	if input.Cwd == "" {
		input.Cwd = "/source"
	}
	if input.File == "" {
		input.File = tspath.CombinePaths(input.Cwd, "input.ts")
	}
	files := map[string]string{}
	if !input.Corpus {
		files[input.File] = input.Text
	}
	for name, text := range input.Files {
		files[name] = text
	}
	options := map[string]any{"noLib": true, "module": "esnext", "moduleResolution": "bundler", "outDir": "/out"}
	if input.Corpus {
		options = map[string]any{}
	}
	for name, value := range input.Options {
		options[name] = value
	}
	roots := input.Roots
	if roots == nil {
		roots = []string{input.File}
	}
	config, err := json.Marshal(map[string]any{"compilerOptions": options, "files": roots})
	if err != nil {
		panic(err)
	}
	configPath := tspath.CombinePaths(input.Cwd, "tsconfig.json")
	if !input.Corpus {
		files[configPath] = string(config)
	}
	if input.Config != "" {
		files[configPath] = input.Config
	}
	host := tsoptionstest.NewVFSParseConfigHostWithSymlinks(files, input.Symlinks, input.Cwd, !input.CaseInsensitive)
	library := ""
	if input.Corpus {
		host.Vfs = bundled.WrapFS(host.Vfs)
		library = bundled.LibPath()
	}
	var parsed *tsoptions.ParsedCommandLine
	if input.Corpus {
		for _, option := range tsoptions.OptionsDeclarations {
			if value, ok := options[option.Name].(string); ok && option.Kind == tsoptions.CommandLineOptionTypeEnum {
				if identity, found := option.EnumMap().Get(strings.ToLower(value)); found {
					options[option.Name] = identity
				}
			}
		}
		encoded, err := json.Marshal(options)
		if err != nil {
			panic(err)
		}
		compilerOptions := &core.CompilerOptions{}
		if err := json.Unmarshal(encoded, compilerOptions); err != nil {
			panic(err)
		}
		compilerOptions.ConfigFilePath = input.ConfigFileName
		parsed = &tsoptions.ParsedCommandLine{ParsedConfig: &tsoptions.ParsedOptions{FileNames: roots, CompilerOptions: compilerOptions}}
		if input.ContentMappers != 0 {
			parsed, _ = tsoptions.GetParsedCommandLineOfConfigFile(input.ConfigFileName, compilerOptions, nil, host, nil)
			if len(parsed.ContentMappers()) != input.ContentMappers {
				panic("Content-mapper configuration count differs from the reference input")
			}
			parsed.ParsedConfig.CompilerOptions = compilerOptions
			parsed.ParsedConfig.FileNames = roots
		}
	} else {
		parsed, _ = tsoptions.GetParsedCommandLineOfConfigFile(configPath, nil, nil, host, nil)
	}
	var mapperProject contentmapper.Project
	if input.ContentMappers != 0 && parsed.CompilerOptions().RunExternalCode.IsTrue() {
		mapperHost := contentmapper.NewHost(context.Background(), contentmappertest.NewSpawner(), locale.Default)
		defer mapperHost.Close()
		mapperProject = mapperHost.Project(contentmapper.ProjectSpec{ConfigFileName: parsed.ConfigName(), Mappers: parsed.ContentMappers(), CompilerOptions: parsed.CompilerOptions()})
		defer mapperProject.Close()
	}
	program := compiler.NewProgram(compiler.ProgramOptions{Config: parsed, Host: compiler.NewCompilerHost(input.Cwd, host.FS(), library, nil, nil, mapperProject), SingleThreaded: core.TSTrue})
	if input.CheckOnly != "" {
		source := program.GetSourceFile(input.CheckOnly)
		items := append(program.GetSyntacticDiagnostics(context.Background(), source), program.GetSemanticDiagnostics(context.Background(), source)...)
		return map[string]any{"diagnostics": diagnostics(items)}
	}
	var targets []*ast.SourceFile
	if input.Targets != nil {
		targets = make([]*ast.SourceFile, 0, len(input.Targets))
		for _, name := range input.Targets {
			targets = append(targets, program.GetSourceFile(name))
		}
	}
	writes := []map[string]any{}
	var writeLock sync.Mutex
	emitted := program.Emit(context.Background(), compiler.EmitOptions{TargetSourceFiles: targets, EmitOnly: input.Only, ForceEmit: input.Force,
		WriteFile: func(path, text string, data *compiler.WriteFileData) error {
			writeLock.Lock()
			defer writeLock.Unlock()
			if path == input.FailWrite {
				return fmt.Errorf("test write failure")
			}
			if path == input.SkipWrite {
				data.SkippedDtsWrite = true
			}
			writes = append(writes, map[string]any{"path": path, "textBase64": base64.StdEncoding.EncodeToString([]byte(text)), "source": data.SourceFile.FileName(),
				"mapPosition": data.SourceMapUrlPos, "diagnostics": diagnostics(data.Diagnostics)})
			return nil
		}})
	maps := []map[string]any{}
	sort.SliceStable(writes, func(i, j int) bool { return writes[i]["path"].(string) < writes[j]["path"].(string) })
	for _, item := range emitted.SourceMaps {
		maps = append(maps, map[string]any{"file": item.GeneratedFile, "inputs": item.InputSourceFileNames, "map": item.SourceMap})
	}
	names := emitted.EmittedFiles
	if names == nil {
		names = []string{}
	}
	output := map[string]any{"skipped": emitted.EmitSkipped, "diagnostics": diagnostics(emitted.Diagnostics), "emitted": names, "writes": writes, "maps": maps}
	if input.Corpus {
		sources := []any{}
		for _, file := range program.GetSourceFiles() {
			sources = append(sources, []string{file.FileName(), fmt.Sprintf("%x", sha256.Sum256([]byte(file.Text())))})
		}
		output["sources"] = sources
	}
	return output
}

func main() {
	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 4096), 64*1024*1024)
	writer := bufio.NewWriter(os.Stdout)
	defer writer.Flush()
	for scanner.Scan() {
		var input request
		if err := json.Unmarshal(scanner.Bytes(), &input); err != nil {
			panic(err)
		}
		output, err := json.Marshal(emit(input))
		if err != nil {
			panic(err)
		}
		fmt.Fprintln(writer, string(output))
	}
	if err := scanner.Err(); err != nil {
		panic(err)
	}
}
