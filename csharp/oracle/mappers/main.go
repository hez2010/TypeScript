package main

import (
	"bufio"
	"encoding/hex"
	"encoding/json"
	"os"

	"github.com/microsoft/TypeScript/tsc/internal/contentmapper"
	tsjson "github.com/microsoft/TypeScript/tsc/internal/json"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
)

type input struct {
	Operation, Original, Encoding, Source, Name, Version, ConfigIdentity string
	Result, MapperOptions                                                json.RawMessage
	Options                                                              json.RawMessage
	Declared                                                             []string
	Dynamic                                                              bool
}

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 64*1024*1024)
	writer := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var r input
		if err := json.Unmarshal(lines.Bytes(), &r); err != nil {
			panic(err)
		}
		if err := writer.Encode(process(r)); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}

func process(r input) any {
	if r.Operation == "identity" {
		raw, _ := json.Marshal(map[string]any{"compilerOptions": r.Options, "files": []string{"a.ts"}})
		host := tsoptionstest.NewVFSParseConfigHost(map[string]string{"/project/tsconfig.json": string(raw)}, "/project", true)
		config, _ := tsoptions.GetParsedCommandLineOfConfigFile("/project/tsconfig.json", nil, nil, host, nil)
		mapper := &contentmapper.Mapper{Definition: contentmapper.Definition{Options: tsjson.Value(r.MapperOptions)}, Manifest: contentmapper.Manifest{Name: r.Name, Version: r.Version, CompilerOptions: r.Declared, DynamicConfig: r.Dynamic}}
		hash := mapper.TransformIdentity(config.CompilerOptions()).Bytes()
		identity := mapper.Identity() + ":" + hex.EncodeToString(hash[:])
		if r.Dynamic {
			identity = contentmapper.CSharpCombinedIdentity(mapper, r.ConfigIdentity, config.CompilerOptions())
		}
		declared, _ := mapper.MarshalDeclaredOptions(config.CompilerOptions())
		values, _ := tsjson.Marshal(declared)
		return []any{identity, json.RawMessage(values), string(values)}
	}
	result, err := contentmapper.CSharpDecodeResult(r.Result, r.Original, r.Encoding, r.Source)
	if err != nil {
		return []any{false}
	}
	if err := result.Mappings.Validate(result.Text, r.Original); err != nil {
		return []any{false}
	}
	mapOutput := func(mapped contentmapper.MappedResult) []any {
		raw, _ := mapped.Mappings.Marshal()
		directives := []any{}
		for _, d := range mapped.DiagnosticDirectives {
			directives = append(directives, []any{d.OriginalRange.Pos(), d.OriginalRange.End(), d.VirtualRange.Pos(), d.VirtualRange.End(), d.Policy, d.Source, d.UnusedCode, d.UnusedMessageText})
		}
		return []any{mapped.Text, mapped.VirtualExtension, json.RawMessage(raw), directives}
	}
	supplemental := []any{}
	for _, s := range result.Supplemental {
		if err := s.Mappings.Validate(s.Text, r.Original); err != nil {
			return []any{false}
		}
		supplemental = append(supplemental, mapOutput(s))
	}
	diagnostics := []any{}
	for _, d := range result.Diagnostics {
		diagnostics = append(diagnostics, []any{d.Pos(), d.End() - d.Pos(), d.Code(), d.Source(), d.MessageText()})
	}
	return []any{true, mapOutput(contentmapper.MappedResult{Text: result.Text, VirtualExtension: result.VirtualExtension, Mappings: result.Mappings, DiagnosticDirectives: result.DiagnosticDirectives}), supplemental, diagnostics}
}
