package main

import (
	"bufio"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"os"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/binder"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/diagnostics"
	"github.com/microsoft/TypeScript/tsc/internal/parser"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
)

type input struct {
	FileName, Text string
	Options        map[string]any
	ExcludeGlobals bool
	Tree           *ast.CSharpSyntaxTree
}

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 128*1024*1024)
	out := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var request input
		if err := json.Unmarshal(lines.Bytes(), &request); err != nil {
			panic(err)
		}
		if err := out.Encode(process(request)); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}

func process(request input) any {
	text, err := base64.StdEncoding.DecodeString(request.Text)
	if err != nil {
		panic(err)
	}
	config, _ := json.Marshal(map[string]any{"compilerOptions": request.Options, "files": []string{request.FileName}})
	host := tsoptionstest.NewVFSParseConfigHost(map[string]string{"/project/tsconfig.json": string(config), request.FileName: string(text)}, "/project", true)
	parsed, _ := tsoptions.GetParsedCommandLineOfConfigFile("/project/tsconfig.json", nil, nil, host, nil)
	if parsed == nil {
		panic("Invalid configuration")
	}
	options := parsed.CompilerOptions()
	file := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: request.FileName}, string(text), core.GetScriptKindFromFileName(request.FileName))
	parseErrors := len(file.Diagnostics())
	fingerprint := ""
	if request.Tree != nil {
		ast.CSharpLoadSyntax(file, *request.Tree)
		if request.Tree.ParseErrors == 0 {
			file.SetDiagnostics(nil)
		} else if len(file.Diagnostics()) == 0 {
			file.SetDiagnostics([]*ast.Diagnostic{ast.NewCompilerDiagnostic(diagnostics.Expression_expected)})
		}
		fingerprint = syntaxFingerprint(file)
	}
	binder.BindSourceFile(file)
	nodes := []*ast.Node{nil}
	ids := map[*ast.Node]int{nil: 0}
	stack := []*ast.Node{file.AsNode()}
	for len(stack) > 0 {
		n := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		ids[n] = len(nodes)
		nodes = append(nodes, n)
		children := []*ast.Node{}
		n.ForEachChild(func(child *ast.Node) bool { children = append(children, child); return false })
		for i := len(children) - 1; i >= 0; i-- {
			stack = append(stack, children[i])
		}
	}
	symbols := []*ast.Symbol{nil}
	sids := map[*ast.Symbol]int{nil: 0}
	sid := func(symbol *ast.Symbol) int {
		if id, ok := sids[symbol]; ok {
			return id
		}
		id := len(symbols)
		symbols = append(symbols, symbol)
		sids[symbol] = id
		return id
	}
	events := []any{}
	cache := map[*ast.Node]core.Tristate{}
	r := &binder.NameResolver{CompilerOptions: options, RequireSymbol: &ast.Symbol{Name: "require", Flags: ast.SymbolFlagsProperty}}
	if !ast.IsExternalOrCommonJSModule(file) {
		r.Globals = file.Locals
	}
	r.Error = func(n *ast.Node, m *diagnostics.Message, args ...any) *ast.Diagnostic {
		if args == nil {
			args = []any{}
		}
		events = append(events, []any{0, ids[n], m.Code(), args})
		return nil
	}
	r.SymbolReferenced = func(s *ast.Symbol, m ast.SymbolFlags) { events = append(events, []any{1, sid(s), uint32(m)}) }
	r.GetRequiresScopeChangeCache = func(n *ast.Node) core.Tristate { return cache[n] }
	r.SetRequiresScopeChangeCache = func(n *ast.Node, v core.Tristate) {
		cache[n] = v
		value := 0
		if v == core.TSTrue {
			value = 1
		}
		events = append(events, []any{5, ids[n], value})
	}
	r.OnFailedToResolveSymbol = func(n *ast.Node, name string, m ast.SymbolFlags, message *diagnostics.Message) {
		events = append(events, []any{3, name, uint32(m), message.Code()})
	}
	r.OnSuccessfullyResolvedSymbol = func(n *ast.Node, s *ast.Symbol, m ast.SymbolFlags, last, associated *ast.Node, deferred bool) {
		d := 0
		if deferred {
			d = 1
		}
		events = append(events, []any{2, sid(s), uint32(m), ids[last], ids[associated], d})
	}
	r.OnPropertyWithInvalidInitializer = func(n *ast.Node, name string, declaration *ast.Node, s *ast.Symbol) bool {
		events = append(events, []any{4, ids[declaration], sid(s)})
		return false
	}
	rows := []any{}
	for _, node := range nodes[1:] {
		if node.Kind != ast.KindIdentifier {
			continue
		}
		for _, meaning := range []ast.SymbolFlags{ast.SymbolFlagsValue, ast.SymbolFlagsType, ast.SymbolFlagsNamespace, ast.SymbolFlagsValue | ast.SymbolFlagsAlias | ast.SymbolFlagsExportValue, ast.SymbolFlagsAll, 0} {
			events = []any{}
			result := r.Resolve(node, node.Text(), meaning, diagnostics.Cannot_find_name_0, true, request.ExcludeGlobals)
			rows = append(rows, []any{ids[node], uint32(meaning), sid(result), events})
		}
	}
	references := binder.NewReferenceResolver(options, binder.ReferenceResolverHooks{ResolveName: r.Resolve})
	rr := []any{}
	for _, node := range nodes[1:] {
		if node.Kind == ast.KindIdentifier {
			decls := []int{}
			for _, decl := range references.GetReferencedValueDeclarations(node) {
				decls = append(decls, ids[decl])
			}
			rr = append(rr, []any{ids[node], ids[references.GetReferencedExportContainer(node, false)], ids[references.GetReferencedExportContainer(node, true)], ids[references.GetReferencedImportDeclaration(node)], ids[references.GetReferencedValueDeclaration(node)], decls, ids[references.GetReferencedMemberValueDeclaration(node)]})
		}
	}
	sr := []any{}
	for _, s := range symbols[1:] {
		decls := []int{}
		for _, decl := range s.Declarations {
			decls = append(decls, ids[decl])
		}
		sr = append(sr, []any{ast.EscapeSymbolName(s.Name), uint32(s.Flags), ids[s.ValueDeclaration], decls})
	}
	data := []any{rows, sr, rr}
	if request.Tree != nil {
		return map[string]any{"data": data, "syntaxFingerprint": fingerprint, "parseErrors": parseErrors}
	}
	return data
}

func syntaxFingerprint(file *ast.SourceFile) string {
	rows := []any{}
	stack := []*ast.Node{file.AsNode()}
	for len(stack) > 0 {
		n := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		children := []*ast.Node{}
		n.ForEachChild(func(child *ast.Node) bool { children = append(children, child); return false })
		rows = append(rows, []any{n.Kind, n.Flags, n.Pos(), n.End(), len(children), ast.CSharpScalarProperties(n), ast.CSharpListProperties(n, func(p int) int { return p })})
		for i := len(children) - 1; i >= 0; i-- {
			stack = append(stack, children[i])
		}
	}
	bytes, _ := json.Marshal(rows)
	hash := sha256.Sum256(bytes)
	return hex.EncodeToString(hash[:])
}
