package main

import (
	"cmp"
	"encoding/json"
	"slices"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/compiler"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/modulespecifiers"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
)

func programSpecifiers(data []byte) any {
	var input struct {
		Directory, Config, GlobalTypingsCache string
		Files, FileLinks                      map[string]string
		Options                               json.RawMessage
		Roots                                 []string
		Sensitive, UseSources                 bool
		Concurrency                           int
	}
	if err := json.Unmarshal(data, &input); err != nil {
		panic(err)
	}
	if input.Config == "" {
		input.Config = input.Directory + "/tsconfig.json"
		value, _ := json.Marshal(map[string]any{"compilerOptions": input.Options, "files": input.Roots})
		input.Files[input.Config] = string(value)
	}
	host := tsoptionstest.NewVFSParseConfigHostWithSymlinks(input.Files, input.FileLinks, input.Directory, input.Sensitive)
	config, errors := tsoptions.GetParsedCommandLineOfConfigFile(input.Config, nil, nil, host, nil)
	if config == nil {
		panic("Invalid specifier program configuration")
	}
	opts := compiler.ProgramOptions{Config: config, Host: compiler.NewCompilerHost(input.Directory, host.FS(), "", nil, nil, nil), UseSourceOfProjectReference: input.UseSources, TypingsLocation: input.GlobalTypingsCache}
	if input.Concurrency == 1 {
		opts.SingleThreaded = core.TSTrue
	}
	program := compiler.NewProgram(opts)
	program.BindSourceFiles()
	files, queries := []any{}, []any{}
	for _, source := range program.GetSourceFiles() {
		output := ""
		if ref := program.GetProjectReferenceFromSource(source.Path()); ref != nil {
			output = ref.OutputDts
		}
		imports := []any{}
		for _, imp := range source.Imports() {
			resolved := program.GetResolvedModuleFromModuleSpecifier(source, imp)
			target := ""
			if resolved != nil {
				target = resolved.ResolvedFileName
			}
			imports = append(imports, []any{imp.Text(), target, int(program.GetModeForUsageLocation(source, imp))})
		}
		files = append(files, []any{source.FileName(), program.GetSourceOfProjectReferenceIfOutputIncluded(source), output, int(program.GetDefaultResolutionModeForFile(source)), program.SourceFileMayBeEmitted(source, false), imports})
		for _, target := range program.GetSourceFiles() {
			original := program.GetSourceOfProjectReferenceIfOutputIncluded(target)
			paths := modulespecifiers.CSharpAllModulePaths(program.GetSourceOfProjectReferenceIfOutputIncluded(source), target.FileName(), program, config.CompilerOptions())
			for _, preference := range []modulespecifiers.ImportModuleSpecifierPreference{"shortest", "project-relative", "non-relative"} {
				for _, mode := range []core.ResolutionMode{core.ResolutionModeNone, core.ResolutionModeCommonJS, core.ResolutionModeESM} {
					names, kind := modulespecifiers.GetModuleSpecifiersForFileWithInfo(source, original, config.CompilerOptions(), program, modulespecifiers.UserPreferences{ImportModuleSpecifierPreference: preference}, modulespecifiers.ModuleSpecifierOptions{OverrideImportMode: mode}, false)
					if names == nil {
						names = []string{}
					}
					queries = append(queries, []any{source.FileName(), target.FileName(), preference, int(mode), int(kind), names, paths})
				}
			}
		}
	}
	codes := []int32{}
	diagnostics := append(append(errors, config.Errors...), program.GetProgramDiagnostics()...)
	for _, diagnostic := range diagnostics {
		codes = append(codes, diagnostic.Code())
	}
	for _, source := range program.GetSourceFiles() {
		for _, diagnostic := range program.GetIncludeProcessorDiagnostics(source) {
			codes = append(codes, diagnostic.Code())
			diagnostics = append(diagnostics, diagnostic)
		}
	}
	slices.Sort(codes)
	slices.SortFunc(diagnostics, func(a, b *ast.Diagnostic) int {
		af, bf := "", ""
		if a.File() != nil {
			af = a.File().FileName()
		}
		if b.File() != nil {
			bf = b.File().FileName()
		}
		if c := cmp.Compare(af, bf); c != 0 {
			return c
		}
		if c := cmp.Compare(a.Pos(), b.Pos()); c != 0 {
			return c
		}
		return cmp.Compare(a.Code(), b.Code())
	})
	return []any{program.CommonSourceDirectory(), files, queries, codes, programDiagnosticRecords(diagnostics)}
}

func programDiagnosticRecords(diagnostics []*ast.Diagnostic) []any {
	result := []any{}
	for _, d := range diagnostics {
		file := ""
		if d.File() != nil {
			file = d.File().FileName()
		}
		args := d.MessageArgs()
		if args == nil {
			args = []string{}
		}
		result = append(result, map[string]any{"file": file, "start": d.Pos(), "length": d.Len(), "code": d.Code(), "category": d.Category(), "key": d.MessageKey(), "arguments": args, "chain": programDiagnosticRecords(d.MessageChain()), "related": programDiagnosticRecords(d.RelatedInformation())})
	}
	return result
}
