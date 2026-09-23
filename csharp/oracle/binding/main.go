package main

import (
	"bufio"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"os"
	"sort"
	"strings"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/binder"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/diagnostics"
	"github.com/microsoft/TypeScript/tsc/internal/parser"
)

type input struct {
	Name, FileName, Text string
	Tree                 *ast.CSharpSyntaxTree
}

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 128*1024*1024)
	writer := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var request input
		if err := json.Unmarshal(lines.Bytes(), &request); err != nil {
			panic(err)
		}
		data, err := base64.StdEncoding.DecodeString(request.Text)
		if err != nil {
			panic(err)
		}
		file := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: request.FileName}, string(data), core.GetScriptKindFromFileName(request.FileName))
		if request.Tree != nil {
			ast.CSharpLoadSyntax(file, *request.Tree)
			if request.Tree.ParseErrors == 0 {
				file.SetDiagnostics(nil)
			} else if len(file.Diagnostics()) == 0 {
				file.SetDiagnostics([]*ast.Diagnostic{ast.NewCompilerDiagnostic(diagnostics.Expression_expected)})
			}
		}
		fingerprint := ""
		if request.Tree != nil {
			fingerprint = syntaxFingerprint(file)
		}
		binder.BindSourceFile(file)
		var result any = encode(file)
		if request.Tree != nil {
			result = map[string]any{"binding": result, "syntaxFingerprint": fingerprint}
		}
		if err := writer.Encode(result); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}

func syntaxFingerprint(file *ast.SourceFile) string {
	rows := []any{}
	stack := []*ast.Node{file.AsNode()}
	for len(stack) > 0 {
		n := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		children := []*ast.Node{}
		n.ForEachChild(func(c *ast.Node) bool { children = append(children, c); return false })
		rows = append(rows, []any{n.Kind, n.Flags, n.Pos(), n.End(), len(children), ast.CSharpScalarProperties(n), ast.CSharpListProperties(n, func(p int) int { return p })})
		for i := len(children) - 1; i >= 0; i-- {
			stack = append(stack, children[i])
		}
	}
	bytes, _ := json.Marshal(rows)
	hash := sha256.Sum256(bytes)
	return hex.EncodeToString(hash[:])
}

func encode(file *ast.SourceFile) any {
	nodes := []*ast.Node{nil}
	nodeIds := map[*ast.Node]int{nil: 0}
	stack := []*ast.Node{file.AsNode()}
	for len(stack) > 0 {
		node := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		nodeIds[node] = len(nodes)
		nodes = append(nodes, node)
		children := []*ast.Node{}
		node.ForEachChild(func(c *ast.Node) bool { children = append(children, c); return false })
		for i := len(children) - 1; i >= 0; i-- {
			stack = append(stack, children[i])
		}
	}
	symbols := []*ast.Symbol{nil}
	symbolIds := map[*ast.Symbol]int{nil: 0}
	sid := func(s *ast.Symbol) int {
		if id, ok := symbolIds[s]; ok {
			return id
		}
		id := len(symbols)
		symbolIds[s] = id
		symbols = append(symbols, s)
		return id
	}
	flows := []*ast.FlowNode{nil}
	flowIds := map[*ast.FlowNode]int{nil: 0}
	fid := func(f *ast.FlowNode) int {
		if id, ok := flowIds[f]; ok {
			return id
		}
		id := len(flows)
		flowIds[f] = id
		flows = append(flows, f)
		return id
	}
	name := func(s *ast.Symbol) string {
		if len(s.Declarations) > 0 && s.Declarations[0].Kind == ast.KindModuleDeclaration && strings.HasPrefix(s.Name, ast.InternalSymbolNamePrefix+"\"") && strings.Contains(s.Name, "pattern@") {
			attr := s.Declarations[0].AsModuleDeclaration().Attributes
			i := strings.LastIndex(s.Name, "@")
			return ast.EscapeSymbolName(s.Name[:i+1]) + stringId(nodeIds[attr])
		}
		if strings.HasPrefix(s.Name, ast.InternalSymbolNamePrefix+"#") && s.Parent != nil {
			i := strings.Index(s.Name, "@")
			return "__#" + stringId(nodeIds[s.Parent.Declarations[0]]) + s.Name[i:]
		}
		return ast.EscapeSymbolName(s.Name)
	}
	table := func(t ast.SymbolTable) []any {
		values := []*ast.Symbol{}
		for _, s := range t {
			values = append(values, s)
		}
		sort.Slice(values, func(i, j int) bool { return name(values[i]) < name(values[j]) })
		rows := []any{}
		for _, s := range values {
			rows = append(rows, []any{name(s), sid(s)})
		}
		return rows
	}
	nr := []any{}
	for _, n := range nodes[1:] {
		var flow, end, ret *ast.FlowNode
		if d := n.FlowNodeData(); d != nil {
			flow = d.FlowNode
		}
		if d := n.BodyData(); d != nil {
			end = d.EndFlowNode
		}
		switch n.Kind {
		case ast.KindConstructor:
			ret = n.AsConstructorDeclaration().ReturnFlowNode
		case ast.KindClassStaticBlockDeclaration:
			ret = n.AsClassStaticBlockDeclaration().ReturnFlowNode
		case ast.KindCaseClause, ast.KindDefaultClause:
			end = n.AsCaseOrDefaultClause().FallthroughFlowNode
		}
		nr = append(nr, []any{int(n.Kind), n.Pos(), n.End(), sid(n.Symbol()), sid(n.LocalSymbol()), table(n.Locals()), fid(flow), fid(end), fid(ret), uint32(n.Flags & (ast.NodeFlagsExportContext | ast.NodeFlagsContainsThis | ast.NodeFlagsReachabilityAndEmitFlags | ast.NodeFlagsUnreachable))})
	}
	globals := table(file.GlobalExports)
	sr := []any{}
	for i := 1; i < len(symbols); i++ {
		s := symbols[i]
		ds := []int{}
		for _, n := range s.Declarations {
			ds = append(ds, nodeIds[n])
		}
		sr = append(sr, []any{uint32(s.Flags), name(s), sid(s.Parent), sid(s.ExportSymbol), nodeIds[s.ValueDeclaration], ds, table(s.Members), table(s.Exports)})
	}
	fr := []any{}
	for i := 1; i < len(flows); i++ {
		f := flows[i]
		ants := []int{}
		for a := f.Antecedents; a != nil; a = a.Next {
			ants = append(ants, fid(a.Flow))
		}
		nid := nodeIds[f.Node]
		start, end, target := 0, 0, 0
		reduced := []int{}
		if f.Flags&ast.FlowFlagsSwitchClause != 0 {
			d := f.Node.AsFlowSwitchClauseData()
			nid = nodeIds[d.SwitchStatement]
			start = int(d.ClauseStart)
			end = int(d.ClauseEnd)
		}
		if f.Flags&ast.FlowFlagsReduceLabel != 0 {
			d := f.Node.AsFlowReduceLabelData()
			target = fid(d.Target)
			for a := d.Antecedents; a != nil; a = a.Next {
				reduced = append(reduced, fid(a.Flow))
			}
		}
		fr = append(fr, []any{uint32(f.Flags), nid, fid(f.Antecedent), ants, start, end, target, reduced})
	}
	ds := []any{}
	for _, d := range file.BindDiagnostics() {
		ds = append(ds, diagnosticValue(d))
	}
	return []any{nr, sr, fr, ds, globals, nodeIds[file.CommonJSModuleIndicator]}
}

func diagnosticValue(d *ast.Diagnostic) []any {
	args := []string{}
	for _, a := range d.MessageArgs() {
		args = append(args, base64.StdEncoding.EncodeToString([]byte(a)))
	}
	related := []any{}
	for _, r := range d.RelatedInformation() {
		related = append(related, diagnosticValue(r))
	}
	return []any{d.Code(), d.Pos(), d.End() - d.Pos(), args, related}
}

func stringId(id int) string {
	if id == 0 {
		return "0"
	}
	var b [20]byte
	i := len(b)
	for id > 0 {
		i--
		b[i] = byte('0' + id%10)
		id /= 10
	}
	return string(b[i:])
}
