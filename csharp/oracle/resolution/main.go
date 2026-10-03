// Development-only reference probe; never invoked by the candidate backend.
package main

import (
	"bufio"
	"encoding/json"
	"os"
	"slices"

	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/module"
	"github.com/microsoft/TypeScript/tsc/internal/packagejson"
	"github.com/microsoft/TypeScript/tsc/internal/semver"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
)

type request struct {
	Operation, Path, Other, Directory, ContainingFile string
	Sensitive                                         bool
	Mode                                              core.ResolutionMode
	Files, Symlinks                                   map[string]string
	Options                                           json.RawMessage
	ExtraExtensions                                   []string
	Recursive                                         bool
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
	switch input.Operation {
	case "version":
		a, err := semver.TryParseVersion(input.Path)
		if err != nil {
			return []any{false}
		}
		b, err := semver.TryParseVersion(input.Other)
		var compare any
		if err == nil {
			compare = a.Compare(&b)
		}
		return []any{true, a.String(), compare}
	case "range":
		r, ok := semver.TryParseVersionRange(input.Path)
		if !ok {
			return []any{false}
		}
		v, err := semver.TryParseVersion(input.Other)
		var test any
		if err == nil {
			test = r.Test(&v)
		}
		return []any{true, r.String(), test}
	}
	if input.Files == nil {
		input.Files = map[string]string{}
	}
	config, err := json.Marshal(map[string]any{"compilerOptions": input.Options, "files": []string{input.ContainingFile}})
	if err != nil {
		panic(err)
	}
	configPath := input.Directory + "/tsconfig.json"
	input.Files[configPath] = string(config)
	host := tsoptionstest.NewVFSParseConfigHostWithSymlinks(input.Files, input.Symlinks, input.Directory, input.Sensitive)
	parsed, _ := tsoptions.GetParsedCommandLineOfConfigFile(configPath, nil, nil, host, nil)
	if parsed == nil {
		panic("config parse failed")
	}
	resolver := module.NewResolver(host, parsed.CompilerOptions(), "", "", input.ExtraExtensions)
	if input.Operation == "entrypoints" {
		result := []any{}
		pkg := &packagejson.InfoCacheEntry{PackageDirectory: input.Other, DirectoryExists: host.FS().DirectoryExists(input.Other)}
		if data, ok := host.FS().ReadFile(input.Other + "/package.json"); ok {
			fields, err := packagejson.Parse([]byte(data))
			pkg.Contents = &packagejson.PackageJson{Fields: fields, Parseable: err == nil}
		}
		for _, entry := range resolver.GetEntrypointsFromPackageJsonInfo(pkg, input.Path, input.Recursive) {
			included, excluded := []string{}, []string{}
			for condition := range entry.IncludeConditions.Keys() { included = append(included, condition) }
			for condition := range entry.ExcludeConditions.Keys() { excluded = append(excluded, condition) }
			slices.Sort(included)
			slices.Sort(excluded)
			result = append(result, []any{entry.OriginalFileName, entry.ResolvedFileName, entry.ModuleSpecifier, entry.Ending, included, excluded})
		}
		return result
	}
	if input.Operation == "automatic" {
		result := module.GetAutomaticTypeDirectiveNames(parsed.CompilerOptions(), host)
		if result == nil {
			return []string{}
		}
		return result
	}
	if input.Operation == "types" {
		r, _ := resolver.ResolveTypeReferenceDirective(input.Path, input.ContainingFile, input.Mode, nil)
		codes := []int32{}
		for _, d := range r.ResolutionDiagnostics {
			codes = append(codes, d.Code())
		}
		return []any{r.ResolvedFileName, r.OriginalPath, r.IsExternalLibraryImport, r.Primary, r.PackageId, codes}
	}
	r, traces := resolver.ResolveModuleName(input.Path, input.ContainingFile, input.Mode, nil)
	if input.Operation == "config" {
		r = module.ResolveConfig(input.Path, input.ContainingFile, host)
	}
	codes := []int32{}
	for _, d := range r.ResolutionDiagnostics {
		codes = append(codes, d.Code())
	}
	result := []any{r.ResolvedFileName, r.Extension, r.OriginalPath, r.IsExternalLibraryImport, r.ResolvedUsingTsExtension, r.ResolvedUsingExtraExtensions, r.PackageId, r.AlternateResult, codes}
	if input.Operation == "resolveTrace" {
		rows := []any{}
		for _, trace := range traces {
			rows = append(rows, []any{trace.Message.Code(), trace.Args})
		}
		return []any{result, rows}
	}
	return result
}
