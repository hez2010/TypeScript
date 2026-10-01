package main

import (
	"bufio"
	"encoding/base64"
	stdjson "encoding/json"
	"fmt"
	goast "go/ast"
	goparser "go/parser"
	"go/token"
	"os"
	"strconv"
	"strings"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/checker"
	"github.com/microsoft/TypeScript/tsc/internal/compiler"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/json"
	"github.com/microsoft/TypeScript/tsc/internal/parser"
	"github.com/microsoft/TypeScript/tsc/internal/printer"
	"github.com/microsoft/TypeScript/tsc/internal/sourcemap"
	"github.com/microsoft/TypeScript/tsc/internal/testutil/parsetestutil"
	"github.com/microsoft/TypeScript/tsc/internal/transformers"
	"github.com/microsoft/TypeScript/tsc/internal/transformers/estransforms"
	"github.com/microsoft/TypeScript/tsc/internal/transformers/inliners"
	"github.com/microsoft/TypeScript/tsc/internal/transformers/jsxtransforms"
	"github.com/microsoft/TypeScript/tsc/internal/transformers/moduletransforms"
	"github.com/microsoft/TypeScript/tsc/internal/transformers/tstransforms"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
	"github.com/microsoft/TypeScript/tsc/internal/tspath"
)

type request struct {
	Name             string            `json:"name"`
	Text             string            `json:"text"`
	File             string            `json:"file"`
	Jsx              bool              `json:"jsx"`
	TransformJsx     bool              `json:"transformJsx"`
	TransformModules bool              `json:"transformModules"`
	NewLine          string            `json:"newLine"`
	Target           int               `json:"target"`
	RemoveComments   bool              `json:"removeComments"`
	OnlyJSDoc        bool              `json:"onlyJSDoc"`
	NeverAscii       bool              `json:"neverAscii"`
	InlineSources    bool              `json:"inlineSources"`
	OmitBraceMaps    bool              `json:"omitBraceMaps"`
	PreserveNewlines bool              `json:"preserveNewlines"`
	OmitSemicolon    bool              `json:"omitSemicolon"`
	Map              bool              `json:"map"`
	Synthetic        bool              `json:"synthetic"`
	Generated        bool              `json:"generated"`
	Json             bool              `json:"json"`
	FirstStatement   bool              `json:"firstStatement"`
	NoEmitHelpers    bool              `json:"noEmitHelpers"`
	ExternalHelpers  bool              `json:"externalHelpers"`
	Helpers          []helper          `json:"helpers"`
	EraseTypes       bool              `json:"eraseTypes"`
	Options          map[string]any    `json:"options,omitempty"`
	ElideImports     bool              `json:"elideImports"`
	Files            map[string]string `json:"files,omitempty"`
	Other            string            `json:"other,omitempty"`
	Environments     bool              `json:"environments"`
	RuntimeSyntax    bool              `json:"runtimeSyntax"`
	Metadata         bool              `json:"metadata"`
	LegacyDecorators bool              `json:"legacyDecorators"`
	LowerExpressions bool              `json:"lowerExpressions"`
	InlineConstants  bool              `json:"inlineConstants"`
	UseStrict        bool              `json:"useStrict"`
	ModuleFormat     int               `json:"moduleFormat"`
}

type helper struct {
	Name     string `json:"name"`
	Text     string `json:"text"`
	Owner    string `json:"owner"`
	Scoped   bool   `json:"scoped"`
	Priority *int   `json:"priority"`
	Factory  string `json:"factory"`
}

func must(err error) {
	if err != nil {
		panic(err)
	}
}

func emit(input request) any {
	fileName := input.File
	if fileName == "" {
		fileName = "/source/input.ts"
	}
	kind := core.ScriptKindTS
	if input.Jsx {
		kind = core.ScriptKindTSX
	}
	if input.Json {
		kind = core.ScriptKindJSON
	}
	source := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: fileName}, input.Text, kind)
	context := printer.NewEmitContext()
	if input.EraseTypes {
		options := &core.CompilerOptions{}
		if input.Options["verbatimModuleSyntax"] == true {
			options.VerbatimModuleSyntax = core.TSTrue
		}
		if input.Options["experimentalDecorators"] == true {
			options.ExperimentalDecorators = core.TSTrue
		}
		if input.Options["preserveConstEnums"] == true {
			options.PreserveConstEnums = core.TSTrue
		}
		if input.Options["isolatedModules"] == true {
			options.IsolatedModules = core.TSTrue
		}
		transformOptions := &transformers.TransformOptions{CompilerOptions: options, Context: context}
		if input.ElideImports || input.RuntimeSyntax || input.Metadata || input.LegacyDecorators || input.LowerExpressions || input.InlineConstants || input.UseStrict || input.TransformJsx || input.TransformModules {
			files := map[string]string{fileName: input.Text}
			for name, text := range input.Files {
				files[name] = text
			}
			configOptions := map[string]any{"noLib": true, "module": "esnext", "moduleResolution": "bundler"}
			for name, value := range input.Options {
				configOptions[name] = value
			}
			if input.RemoveComments {
				configOptions["removeComments"] = true
			}
			config, err := stdjson.Marshal(map[string]any{"compilerOptions": configOptions, "files": []string{fileName}})
			must(err)
			files["/source/tsconfig.json"] = string(config)
			host := tsoptionstest.NewVFSParseConfigHost(files, "/source", true)
			parsed, _ := tsoptions.GetParsedCommandLineOfConfigFile("/source/tsconfig.json", nil, nil, host, nil)
			program := compiler.NewProgram(compiler.ProgramOptions{Config: parsed, Host: compiler.NewCompilerHost("/source", host.FS(), "", nil, nil, nil), SingleThreaded: core.TSTrue})
			c, _ := checker.NewChecker(program, nil)
			source = program.GetSourceFile(fileName)
			transformOptions.CompilerOptions = parsed.CompilerOptions()
			transformOptions.EmitResolver = c.GetEmitResolver()
			transformOptions.Resolver = c.GetEmitResolver()
			transformOptions.GetEmitModuleFormatOfFile = program.GetEmitModuleFormatOfFile
		}
		if input.Metadata {
			source = tstransforms.NewMetadataTransformer(transformOptions).TransformSourceFile(source)
		}
		source = tstransforms.NewTypeEraserTransformer(transformOptions).TransformSourceFile(source)
		if input.ElideImports {
			source = tstransforms.NewImportElisionTransformer(transformOptions).TransformSourceFile(source)
		}
		if input.RuntimeSyntax {
			source = tstransforms.NewRuntimeSyntaxTransformer(transformOptions).TransformSourceFile(source)
		}
		if input.LegacyDecorators {
			source = tstransforms.NewLegacyDecoratorsTransformer(transformOptions).TransformSourceFile(source)
		}
		if input.TransformJsx && (transformOptions.CompilerOptions.Jsx == core.JsxEmitReact || transformOptions.CompilerOptions.Jsx == core.JsxEmitReactJSX || transformOptions.CompilerOptions.Jsx == core.JsxEmitReactJSXDev) {
			source = jsxtransforms.NewJSXTransformer(transformOptions).TransformSourceFile(source)
		}
		if input.LowerExpressions {
			if tx := estransforms.GetESTransformer(transformOptions); tx != nil {
				source = tx.TransformSourceFile(source)
			}
		}
		if (input.UseStrict || input.TransformModules) && input.ModuleFormat != 0 {
			transformOptions.GetEmitModuleFormatOfFile = func(file ast.HasFileName) core.ModuleKind {
				return core.ModuleKind(input.ModuleFormat)
			}
		}
		if input.UseStrict {
			source = estransforms.NewUseStrictTransformer(transformOptions).TransformSourceFile(source)
		}
		if input.TransformModules {
			var tx *transformers.Transformer
			switch transformOptions.CompilerOptions.GetEmitModuleKind() {
			case core.ModuleKindPreserve:
				tx = moduletransforms.NewESModuleTransformer(transformOptions)
			case core.ModuleKindESNext, core.ModuleKindES2022, core.ModuleKindES2020, core.ModuleKindES2015, core.ModuleKindNode20, core.ModuleKindNode18, core.ModuleKindNode16, core.ModuleKindNodeNext, core.ModuleKindCommonJS:
				tx = moduletransforms.NewImpliedModuleTransformer(transformOptions)
			default:
				tx = moduletransforms.NewCommonJSModuleTransformer(transformOptions)
			}
			source = tx.TransformSourceFile(source)
		}
		if input.InlineConstants && !transformOptions.CompilerOptions.GetIsolatedModules() {
			source = inliners.NewConstEnumInliningTransformer(transformOptions).TransformSourceFile(source)
		}
		context.AddEmitHelper(source.AsNode(), context.ReadEmitHelpers()...)
	}
	if input.Synthetic {
		var visitor *ast.NodeVisitor
		visitor = ast.NewNodeVisitor(func(node *ast.Node) *ast.Node {
			if node.Kind == ast.KindParenthesizedExpression {
				return visitor.VisitNode(node.AsParenthesizedExpression().Expression)
			}
			if node.Kind == ast.KindParenthesizedType {
				return visitor.VisitNode(node.AsParenthesizedTypeNode().Type)
			}
			result := visitor.VisitEachChild(node)
			result.Loc = core.UndefinedTextRange()
			return result
		}, nil, ast.NodeVisitorHooks{})
		source = visitor.VisitSourceFile(source)
		parsetestutil.MarkSyntheticRecursive(source.AsNode())
	}
	if input.Generated {
		prototypes := map[string]*ast.Node{}
		var visitor *ast.NodeVisitor
		visitor = ast.NewNodeVisitor(func(node *ast.Node) *ast.Node {
			if ast.IsMemberName(node) {
				text := node.Text()
				if strings.HasPrefix(text, "$auto") || strings.HasPrefix(text, "$loop") || strings.HasPrefix(text, "$unique") || strings.HasPrefix(text, "#$private") {
					prototype := prototypes[text]
					if prototype == nil {
						switch {
						case strings.HasPrefix(text, "$auto"):
							prototype = context.Factory.NewTempVariable()
						case strings.HasPrefix(text, "$loop"):
							prototype = context.Factory.NewLoopVariable()
						case strings.HasPrefix(text, "#$private"):
							prototype = context.Factory.NewUniquePrivateName("#field")
						default:
							prototype = context.Factory.NewUniqueName("value")
						}
						prototypes[text] = prototype
					}
					result := prototype.Clone(context.Factory)
					context.SetOriginalEx(result, node, true)
					result.Loc = node.Loc
					return result
				}
			}
			return visitor.VisitEachChild(node)
		}, context.Factory.AsNodeFactory(), ast.NodeVisitorHooks{})
		source = visitor.VisitSourceFile(source)
	}
	newLine := input.NewLine
	if input.Environments {
		f := context.Factory
		var visitor *ast.NodeVisitor
		visitor = context.NewNodeVisitor(func(node *ast.Node) *ast.Node {
			if ast.IsIdentifier(node) && (node.Text() == "$var" || node.Text() == "$let") {
				temporary := f.NewTempVariable()
				if node.Text() == "$var" {
					context.AddVariableDeclaration(temporary)
				} else {
					context.AddLexicalDeclaration(temporary)
				}
				return temporary
			}
			if ast.IsExpressionStatement(node) && ast.IsCallExpression(node.Expression()) && ast.IsIdentifier(node.Expression().Expression()) {
				switch node.Expression().Expression().Text() {
				case "split":
					statement := func(name string) *ast.Node {
						return f.NewExpressionStatement(f.NewCallExpression(f.NewIdentifier(name), nil, nil, f.NewNodeList(nil), ast.NodeFlagsNone))
					}
					return f.NewSyntaxList([]*ast.Node{statement("first"), statement("second")})
				case "remove":
					return nil
				}
			}
			return visitor.VisitEachChild(node)
		})
		source = visitor.VisitSourceFile(source)
	}
	if input.ExternalHelpers {
		context.AddEmitFlags(source.AsNode(), printer.EFExternalHelpers)
	}
	for _, item := range input.Helpers {
		owner := source.AsNode()
		if item.Owner == "body" {
			var find func(*ast.Node)
			find = func(node *ast.Node) {
				if owner != source.AsNode() {
					return
				}
				if node.Kind == ast.KindBlock {
					owner = node
					return
				}
				node.ForEachChild(func(child *ast.Node) bool { find(child); return false })
			}
			find(source.AsNode())
		}
		value := &printer.EmitHelper{Name: item.Name, Text: item.Text, Scoped: item.Scoped}
		if item.Priority != nil {
			value.Priority = &printer.Priority{Value: *item.Priority}
		}
		if item.Factory != "" {
			value.TextCallback = func(unique func(string) string) string { return "var " + unique(item.Factory) + " = {};" }
		}
		context.AddEmitHelper(owner, value)
	}
	if newLine == "" {
		newLine = "\n"
	}
	newlineKind := core.NewLineKindLF
	if newLine == "\r\n" {
		newlineKind = core.NewLineKindCRLF
	}
	target := core.ScriptTargetES2015
	if input.Target >= 2015 && input.Target <= 2025 {
		target += core.ScriptTarget(input.Target - 2015)
	}
	if input.Target > 2025 {
		target = core.ScriptTargetESNext
	}
	p := printer.NewPrinter(printer.PrinterOptions{
		RemoveComments: input.RemoveComments, NewLine: newlineKind, OmitTrailingSemicolon: input.OmitSemicolon,
		Target: target, SourceMap: input.Map, InlineSources: input.InlineSources, OmitBraceSourceMapPositions: input.OmitBraceMaps,
		OnlyPrintJSDocStyle: input.OnlyJSDoc, NeverAsciiEscape: input.NeverAscii, PreserveSourceNewlines: input.PreserveNewlines,
		NoEmitHelpers: input.NoEmitHelpers,
	}, printer.PrintHandlers{}, context)
	var generator *sourcemap.Generator
	if input.Map {
		generator = sourcemap.NewGenerator("output.js", "", "/out", tspath.ComparePathsOptions{UseCaseSensitiveFileNames: true, CurrentDirectory: "/"})
	}
	writer := printer.NewTextWriter(newLine, 4)
	node := source.AsNode()
	if input.FirstStatement {
		node = source.Statements.Nodes[0]
	}
	p.Write(node, source, writer, generator)
	result := map[string]any{"textBase64": base64.StdEncoding.EncodeToString([]byte(writer.String()))}
	if generator != nil {
		result["map"] = generator.RawSourceMap()
	}
	return result
}

func fixtures(file string) {
	source, err := goparser.ParseFile(token.NewFileSet(), file, nil, 0)
	must(err)
	cases := []request{}
	goast.Inspect(source, func(node goast.Node) bool {
		literal, ok := node.(*goast.CompositeLit)
		if !ok {
			return true
		}
		values := map[string]string{}
		for _, element := range literal.Elts {
			item, ok := element.(*goast.KeyValueExpr)
			if !ok {
				continue
			}
			name, ok := item.Key.(*goast.Ident)
			if !ok {
				continue
			}
			if value, ok := item.Value.(*goast.BasicLit); ok && value.Kind == token.STRING {
				text, err := strconv.Unquote(value.Value)
				must(err)
				values[name.Name] = text
			} else if value, ok := item.Value.(*goast.Ident); ok {
				values[name.Name] = value.Name
			}
		}
		if name, ok := values["title"]; ok {
			if text, ok := values["input"]; ok {
				cases = append(cases, request{Name: name, Text: text, Other: values["other"], Jsx: values["jsx"] == "true", Options: map[string]any{"verbatimModuleSyntax": values["vms"] == "true"}})
			}
		}
		return true
	})
	data, err := json.Marshal(cases)
	must(err)
	_, err = os.Stdout.Write(append(data, '\n'))
	must(err)
}

func safeEmit(input request) (result any) {
	defer func() {
		if failure := recover(); failure != nil {
			result = map[string]any{"textBase64": "", "error": fmt.Sprint(failure)}
		}
	}()
	return emit(input)
}

func main() {
	if len(os.Args) == 3 && os.Args[1] == "--fixtures" {
		fixtures(os.Args[2])
		return
	}
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 32*1024*1024)
	for lines.Scan() {
		var input request
		must(stdjson.Unmarshal(lines.Bytes(), &input))
		data, err := json.Marshal(safeEmit(input))
		must(err)
		_, err = os.Stdout.Write(append(data, '\n'))
		must(err)
	}
	must(lines.Err())
}
