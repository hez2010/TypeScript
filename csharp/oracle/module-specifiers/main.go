package main

import (
	"bufio"
	"encoding/json"
	"os"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/collections"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/modulespecifiers"
	"github.com/microsoft/TypeScript/tsc/internal/packagejson"
	"github.com/microsoft/TypeScript/tsc/internal/parser"
	"github.com/microsoft/TypeScript/tsc/internal/symlinks"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
	"github.com/microsoft/TypeScript/tsc/internal/tspath"
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
			PackageDirectory, PackageName, CommonDirectory                                                           string
			PackageMap                                                                                               json.RawMessage
			Conditions, MapperExtensions                                                                             []string
			MatchMode                                                                                                modulespecifiers.MatchingMode
			Imports, PreferTypeScript                                                                                bool
			PackageNameOnly, Redirect                                                                                bool
			GlobalTypingsCache                                                                                       string
			ReferenceOutput, OriginalSource, Relative, ExcludedPrefix                                                string
			Redirects                                                                                                []string
			Symlinks                                                                                                 map[string][]string
			ImportTargets                                                                                            map[string]string
			ImportModes                                                                                              map[string]core.ResolutionMode
			ModulePaths                                                                                              []modulespecifiers.ModulePath
			PathsOnly, ForAutoImport                                                                                 bool
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
		if input.Operation == "all-paths" || input.Operation == "local" || input.Operation == "select" || input.Operation == "generate" {
			file := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: input.FileName}, input.Source, core.EnsureScriptKindFromFileName(input.FileName))
			links := symlinks.NewKnownSymlink(input.Directory, input.Sensitive)
			for real, names := range input.Symlinks {
				for _, name := range names {
					links.SetDirectory(name, tspath.ToPath(name, input.Directory, input.Sensitive).EnsureTrailingDirectorySeparator(), &symlinks.KnownDirectoryLink{Real: tspath.EnsureTrailingDirectorySeparator(real), RealPath: tspath.ToPath(real, input.Directory, input.Sensitive).EnsureTrailingDirectorySeparator()})
				}
			}
			common := input.CommonDirectory
			if common == "" {
				common = input.Directory
			}
			result := modulespecifiers.CSharpGeneration(input.Operation, file, parsed.CompilerOptions(), &modulespecifiers.CSharpPathHost{
				Directory: input.Directory, Sensitive: input.Sensitive, DefaultMode: input.DefaultMode, Exists: host.FS().FileExists, Read: host.FS().ReadFile,
				CommonDirectory: common, MapperExtensions: input.MapperExtensions, GlobalTypingsCache: input.GlobalTypingsCache, ReferenceOutput: input.ReferenceOutput,
				Redirects: input.Redirects, Links: links, OriginalSource: input.OriginalSource, ImportTargets: input.ImportTargets, ImportModes: input.ImportModes,
			},
				input.Target, input.Relative, input.Preference, input.ExcludedPrefix, input.Mode, input.PathsOnly, input.ForAutoImport, input.ModulePaths)
			if err := output.Encode(result); err != nil {
				panic(err)
			}
			continue
		}
		if input.Operation == "node-modules" {
			file := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: input.FileName}, input.Source, core.EnsureScriptKindFromFileName(input.FileName))
			result := modulespecifiers.CSharpNodeModuleSpecifier(file, parsed.CompilerOptions(),
				&modulespecifiers.CSharpPathHost{
					Directory: input.Directory, Sensitive: input.Sensitive, DefaultMode: input.DefaultMode,
					Exists: host.FS().FileExists, Read: host.FS().ReadFile, GlobalTypingsCache: input.GlobalTypingsCache,
				},
				input.Target, input.Preference, input.Mode, input.PackageNameOnly, input.Redirect)
			if err := output.Encode(result); err != nil {
				panic(err)
			}
			continue
		}
		if input.Operation == "package-map" || input.Operation == "package-exports" || input.Operation == "package-imports" || input.Operation == "package-conditions" || input.Operation == "output-paths" {
			valueText := input.PackageMap
			if len(valueText) == 0 {
				valueText = []byte("null")
			}
			fields, err := packagejson.Parse(append(append([]byte("{\"exports\":"), valueText...), '}'))
			if err != nil {
				panic(err)
			}
			common := input.CommonDirectory
			if common == "" {
				common = input.Directory
			}
			result := modulespecifiers.CSharpPackageSpecifiers(input.Operation, parsed.CompilerOptions(),
				&modulespecifiers.CSharpPathHost{Directory: input.Directory, Sensitive: input.Sensitive, DefaultMode: input.DefaultMode, Exists: host.FS().FileExists, Read: host.FS().ReadFile, CommonDirectory: common, MapperExtensions: input.MapperExtensions},
				input.Target, input.PackageDirectory, input.PackageName, input.SourceDirectory, fields.Exports, input.Conditions, input.MatchMode, input.Imports, input.PreferTypeScript, input.Mode)
			if err := output.Encode(result); err != nil {
				panic(err)
			}
			continue
		}
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
