package main

import (
	"bufio"
	"encoding/base64"
	stdjson "encoding/json"
	"os"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/binder"
	"github.com/microsoft/TypeScript/tsc/internal/parser"

	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/json"
	"github.com/microsoft/TypeScript/tsc/internal/printer"
	"github.com/microsoft/TypeScript/tsc/internal/sourcemap"
	"github.com/microsoft/TypeScript/tsc/internal/tspath"
)

type step struct {
	Kind         string `json:"kind"`
	Text         string `json:"text"`
	TextBase64   string `json:"textBase64"`
	Force        bool   `json:"force"`
	Line         int    `json:"line"`
	Column       int    `json:"column"`
	Source       int    `json:"source"`
	SourceLine   int    `json:"sourceLine"`
	SourceColumn int    `json:"sourceColumn"`
	Name         int    `json:"name"`
	Flags        int    `json:"flags"`
	Prefix       string `json:"prefix"`
	Suffix       string `json:"suffix"`
	Index        int    `json:"index"`
	Target       string `json:"target"`
	Reuse        bool   `json:"reuse"`
}

type request struct {
	Operation        string   `json:"operation"`
	NewLine          string   `json:"newLine"`
	IndentSize       int      `json:"indentSize"`
	File             string   `json:"file"`
	SourceRoot       string   `json:"sourceRoot"`
	Directory        string   `json:"directory"`
	CurrentDirectory string   `json:"currentDirectory"`
	CaseSensitive    *bool    `json:"caseSensitive"`
	Steps            []step   `json:"steps"`
	Source           string   `json:"source"`
	Blocked          []string `json:"blocked"`
}

func text(s step) string {
	if s.TextBase64 != "" {
		bytes, err := base64.StdEncoding.DecodeString(s.TextBase64)
		must(err)
		return string(bytes)
	}
	return s.Text
}

func must(err error) {
	if err != nil {
		panic(err)
	}
}

func run(input request) any {
	if input.Operation == "names" {
		context := printer.NewEmitContext()
		blocked := map[string]bool{}
		for _, name := range input.Blocked {
			blocked[name] = true
		}
		generator := printer.NameGenerator{Context: context, GetTextOfNode: (*ast.Node).Text,
			IsFileLevelUniqueNameInCurrentFile: func(name string, private bool) bool { return !blocked[name] }}
		source := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: "/names.ts"}, input.Source, core.ScriptKindTS)
		binder.BindSourceFile(source)
		names := []*ast.Node{}
		result := []string{}
		for _, s := range input.Steps {
			options := printer.AutoGenerateOptions{Flags: printer.GeneratedIdentifierFlags(s.Flags), Prefix: s.Prefix, Suffix: s.Suffix}
			switch s.Kind {
			case "auto":
				names = append(names, context.Factory.NewTempVariableEx(options))
			case "loop":
				names = append(names, context.Factory.NewLoopVariableEx(options))
			case "unique":
				names = append(names, context.Factory.NewUniqueNameEx(text(s), options))
			case "private":
				names = append(names, context.Factory.NewUniquePrivateNameEx(text(s), options))
			case "identifierNode":
				names = append(names, context.Factory.NewGeneratedNameForNodeEx(context.Factory.NewIdentifier(text(s)), options))
			case "node", "privateNode":
				var target *ast.Node
				var find func(*ast.Node) bool
				find = func(node *ast.Node) bool {
					if node.Kind.String() == "Kind"+s.Target {
						target = node
						return true
					}
					return node.ForEachChild(find)
				}
				find(source.AsNode())
				if target == nil {
					panic("name target not found: " + s.Target)
				}
				if s.Kind == "privateNode" {
					names = append(names, context.Factory.NewGeneratedPrivateNameForNodeEx(target, options))
				} else {
					names = append(names, context.Factory.NewGeneratedNameForNodeEx(target, options))
				}
			case "clone":
				names = append(names, names[s.Index].Clone(context.Factory))
			case "push":
				generator.PushScope(s.Reuse)
			case "pop":
				generator.PopScope(s.Reuse)
			case "generate":
				result = append(result, generator.GenerateName(names[s.Index]))
			case "helper":
				result = append(result, generator.MakeFileLevelOptimisticUniqueName(text(s)))
			default:
				panic("unknown name step: " + s.Kind)
			}
		}
		return result
	}
	if input.Operation == "writer" {
		newLine := input.NewLine
		if newLine == "" {
			newLine = "\n"
		}
		writer := printer.NewTextWriter(newLine, input.IndentSize)
		result := []map[string]any{}
		for _, s := range input.Steps {
			switch s.Kind {
			case "write":
				writer.Write(text(s))
			case "raw":
				writer.RawWrite(text(s))
			case "comment":
				writer.WriteComment(text(s))
			case "line":
				writer.WriteLineForce(s.Force)
			case "indent":
				writer.IncreaseIndent()
			case "dedent":
				writer.DecreaseIndent()
			case "clear":
				writer.Clear()
			default:
				panic("unknown writer step")
			}
			result = append(result, map[string]any{
				"textBase64": base64.StdEncoding.EncodeToString([]byte(writer.String())),
				"line":       writer.GetLine(), "column": writer.GetColumn(), "position": writer.GetTextPos(),
				"indent": writer.GetIndent(), "lineStart": writer.IsAtStartOfLine(),
				"trailingComment": writer.HasTrailingComment(), "trailingWhitespace": writer.HasTrailingWhitespace(),
			})
		}
		return result
	}
	if input.Operation == "map" {
		currentDirectory := input.CurrentDirectory
		if currentDirectory == "" {
			currentDirectory = "/"
		}
		sensitive := input.CaseSensitive == nil || *input.CaseSensitive
		generator := sourcemap.NewGenerator(input.File, input.SourceRoot, input.Directory,
			tspath.ComparePathsOptions{UseCaseSensitiveFileNames: sensitive, CurrentDirectory: currentDirectory})
		ids := []int{}
		for _, s := range input.Steps {
			switch s.Kind {
			case "source":
				ids = append(ids, int(generator.AddSource(text(s))))
			case "name":
				ids = append(ids, int(generator.AddName(text(s))))
			case "content":
				must(generator.SetSourceContent(sourcemap.SourceIndex(s.Source), text(s)))
			case "generated":
				must(generator.AddGeneratedMapping(s.Line, core.UTF16Offset(s.Column)))
			case "mapping":
				must(generator.AddSourceMapping(s.Line, core.UTF16Offset(s.Column), sourcemap.SourceIndex(s.Source), s.SourceLine, core.UTF16Offset(s.SourceColumn)))
			case "named":
				must(generator.AddNamedSourceMapping(s.Line, core.UTF16Offset(s.Column), sourcemap.SourceIndex(s.Source), s.SourceLine, core.UTF16Offset(s.SourceColumn), sourcemap.NameIndex(s.Name)))
			default:
				panic("unknown map step")
			}
		}
		return map[string]any{"ids": ids, "map": generator.RawSourceMap()}
	}
	panic("unknown operation")
}

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 16*1024*1024)
	for lines.Scan() {
		var input request
		must(stdjson.Unmarshal(lines.Bytes(), &input))
		bytes, err := json.Marshal(run(input))
		must(err)
		_, err = os.Stdout.Write(append(bytes, '\n'))
		must(err)
	}
	must(lines.Err())
}
