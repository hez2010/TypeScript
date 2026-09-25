package main

import (
	"bufio"
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"math"
	"os"
	"slices"
	"strings"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/checker"
	"github.com/microsoft/TypeScript/tsc/internal/compiler"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/jsnum"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
)

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 128*1024*1024)
	output := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var input struct {
			Files                map[string]string
			Roots                []string
			Options              map[string]any
			Concurrency          int
			Aliases              bool
			TypeNodes            bool
			Members              bool
			Values               bool
			Properties           bool
			Signatures           bool
			Identity             bool
			Assignability        bool
			Indexing             bool
			Constants            bool
			Expressions          bool
			Awaited              bool
			References           bool
			Flow                 bool
			Identifiers          bool
			Access               bool
			Calls                bool
			Assertions           bool
			Semantic             bool
			SemanticDetails      bool
			TypeDisplays         bool
			Locations            bool
			SymbolLocations      bool
			DocumentationSymbols bool
			ScopeServices        bool
			ContextQueries       bool
			NumberStrings        []string
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
		if input.TypeDisplays {
			rows := [][]string{}
			pending := []*ast.Node{program.GetSourceFile("/project/main.ts").AsNode()}
			for len(pending) != 0 {
				node := pending[len(pending)-1]
				pending = pending[:len(pending)-1]
				if ast.IsVariableDeclaration(node) && ast.IsIdentifier(node.Name()) && strings.HasPrefix(node.Name().Text(), "show") && node.Type() != nil {
					rows = append(rows, []string{node.Name().Text(), c.TypeToString(c.GetTypeFromTypeNode(node.Type()))})
				}
				children := []*ast.Node{}
				node.ForEachChild(func(child *ast.Node) bool { children = append(children, child); return false })
				for i := len(children) - 1; i >= 0; i-- {
					pending = append(pending, children[i])
				}
			}
			if err := output.Encode(map[string]any{"typeDisplays": rows}); err != nil {
				panic(err)
			}
			continue
		}
		if input.Semantic {
			codes := []int{}
			diagnostics := c.GetDiagnostics(context.Background(), program.GetSourceFile("/project/main.ts"))
			for _, diagnostic := range diagnostics {
				codes = append(codes, int(diagnostic.Code()))
			}
			slices.Sort(codes)
			result := map[string]any{"semanticDiagnostics": codes}
			if input.SemanticDetails {
				result["semanticDiagnosticDetails"] = diagnosticRecords(diagnostics)
			}
			if err := output.Encode(result); err != nil {
				panic(err)
			}
			continue
		}
		result := c.CSharpProgramScopeProbe(input.Aliases, input.TypeNodes, input.Members, input.Values, input.Properties, input.Signatures, input.Identity, input.Assignability, input.Indexing, input.Constants, input.Expressions, input.Awaited, input.References, input.Flow, input.Identifiers, input.Access, input.Calls, input.Assertions, input.Locations, input.SymbolLocations, input.DocumentationSymbols, input.ScopeServices, input.ContextQueries).(map[string]any)
		if input.NumberStrings != nil {
			rows := []string{}
			for _, text := range input.NumberStrings {
				data, err := base64.StdEncoding.DecodeString(text)
				if err != nil {
					panic(err)
				}
				value := jsnum.FromString(string(data))
				if value.IsNaN() {
					rows = append(rows, "nan")
				} else {
					rows = append(rows, fmt.Sprintf("%016x", math.Float64bits(float64(value))))
				}
			}
			result["numberStrings"] = rows
		}
		if err := output.Encode(result); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}

func diagnosticRecords(diagnostics []*ast.Diagnostic) []map[string]any {
	result := make([]map[string]any, 0, len(diagnostics))
	for _, diagnostic := range diagnostics {
		file := ""
		if diagnostic.File() != nil {
			file = diagnostic.File().FileName()
		}
		result = append(result, map[string]any{
			"file": file, "start": diagnostic.Pos(), "length": diagnostic.End() - diagnostic.Pos(), "code": diagnostic.Code(),
			"category": diagnostic.Category(), "key": diagnostic.MessageKey(), "arguments": append([]string{}, diagnostic.MessageArgs()...),
			"chain": diagnosticRecords(diagnostic.MessageChain()), "related": diagnosticRecords(diagnostic.RelatedInformation()),
		})
	}
	return result
}
