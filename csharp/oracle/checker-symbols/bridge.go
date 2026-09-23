// Development-only access to the unchanged reference merge algorithms.
package checker

import (
	"sort"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/parser"
)

type CSharpSymbolDefinition struct {
	Name                string
	Flags               ast.SymbolFlags
	Declaration         ast.Kind
	Members, Exports    []int
	Parent, AliasTarget int
}
type CSharpSymbolOperation struct {
	Left, Right    int
	Unidirectional bool
}
type CSharpSymbolInput struct {
	Symbols    []CSharpSymbolDefinition
	Operations []CSharpSymbolOperation
	Flags      []ast.SymbolFlags
}

func (c *Checker) CSharpMergeSymbols(input CSharpSymbolInput) any {
	file := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: "/declarations.ts"}, "namespace N{} function f(){} let x=1; x=y; x.z; x['z']; f();", core.ScriptKindTS)
	declarations := map[ast.Kind]*ast.Node{}
	stack := []*ast.Node{file.AsNode()}
	for len(stack) > 0 {
		n := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		if declarations[n.Kind] == nil {
			declarations[n.Kind] = n
		}
		children := []*ast.Node{}
		n.ForEachChild(func(child *ast.Node) bool { children = append(children, child); return false })
		for i := len(children) - 1; i >= 0; i-- {
			stack = append(stack, children[i])
		}
	}
	symbols := []*ast.Symbol{nil}
	for _, d := range input.Symbols {
		s := &ast.Symbol{Name: d.Name, Flags: d.Flags}
		if d.Declaration != 0 {
			s.ValueDeclaration = declarations[d.Declaration]
			s.Declarations = []*ast.Node{s.ValueDeclaration}
		}
		symbols = append(symbols, s)
	}
	for i, d := range input.Symbols {
		s := symbols[i+1]
		s.Parent = symbols[d.Parent]
		if d.AliasTarget != 0 {
			t := c.unknownSymbol
			if d.AliasTarget > 0 {
				t = symbols[d.AliasTarget]
			}
			c.aliasSymbolLinks.Get(s).aliasTarget = t
		}
		if len(d.Members) > 0 {
			s.Members = ast.SymbolTable{}
			for _, id := range d.Members {
				s.Members[symbols[id].Name] = symbols[id]
			}
		}
		if len(d.Exports) > 0 {
			s.Exports = ast.SymbolTable{}
			for _, id := range d.Exports {
				s.Exports[symbols[id].Name] = symbols[id]
			}
		}
	}
	for _, op := range input.Operations {
		symbols = append(symbols, c.mergeSymbol(symbols[op.Left], symbols[op.Right], op.Unidirectional))
	}
	ids := map[*ast.Symbol]int{nil: 0}
	queue := []*ast.Symbol{}
	ref := func(s *ast.Symbol) int {
		if id, ok := ids[s]; ok {
			return id
		}
		id := len(queue) + 1
		ids[s] = id
		queue = append(queue, s)
		return id
	}
	results := []int{}
	merges := []int{}
	for _, s := range symbols[1:] {
		results = append(results, ref(s))
		merges = append(merges, ref(c.getMergedSymbol(s)))
	}
	table := func(t ast.SymbolTable) []any {
		keys := []string{}
		for k := range t {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		r := []any{}
		for _, k := range keys {
			r = append(r, []any{k, ref(t[k])})
		}
		return r
	}
	rows := []any{}
	for i := 0; i < len(queue); i++ {
		s := queue[i]
		decls := []int{}
		for _, d := range s.Declarations {
			decls = append(decls, int(d.Kind))
		}
		value := 0
		if s.ValueDeclaration != nil {
			value = int(s.ValueDeclaration.Kind)
		}
		rows = append(rows, []any{s.Name, uint32(s.Flags), ref(s.Parent), value, decls, table(s.Members), table(s.Exports)})
	}
	flags := []uint32{}
	for _, f := range input.Flags {
		flags = append(flags, uint32(getExcludedSymbolFlags(f)))
	}
	return []any{results, merges, rows, flags}
}
