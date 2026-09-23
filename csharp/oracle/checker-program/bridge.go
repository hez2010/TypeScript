// Development-only instrumentation, copied into the frozen reference checkout.
package checker

import (
	"encoding/base64"
	"slices"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
)

func (c *Checker) CSharpProgramScopeProbe(aliasQueries bool) any {
	nodes := []*ast.Node{}
	nodeIDs := map[*ast.Node]int{nil: 0}
	files := []any{}
	for _, file := range c.files {
		files = append(files, []any{file.FileName(), ast.IsExternalOrCommonJSModule(file)})
		pending := []*ast.Node{file.AsNode()}
		for len(pending) != 0 {
			node := pending[len(pending)-1]
			pending = pending[:len(pending)-1]
			nodes = append(nodes, node)
			nodeIDs[node] = len(nodes)
			children := []*ast.Node{}
			node.ForEachChild(func(child *ast.Node) bool { children = append(children, child); return false })
			for i := len(children) - 1; i >= 0; i-- {
				pending = append(pending, children[i])
			}
		}
	}
	symbols := []*ast.Symbol{}
	symbolIDs := map[*ast.Symbol]int{nil: 0}
	sid := func(symbol *ast.Symbol) int {
		if id, ok := symbolIDs[symbol]; ok {
			return id
		}
		symbols = append(symbols, symbol)
		symbolIDs[symbol] = len(symbols)
		return len(symbols)
	}
	types := []*Type{}
	typeIDs := map[*Type]int{nil: 0}
	tid := func(t *Type) int {
		if id, ok := typeIDs[t]; ok {
			return id
		}
		types = append(types, t)
		typeIDs[t] = len(types)
		return len(types)
	}
	tids := func(values []*Type) []int {
		if values == nil {
			return nil
		}
		result := []int{}
		for _, t := range values {
			result = append(result, tid(t))
		}
		return result
	}
	name := func(s string) string { return base64.StdEncoding.EncodeToString([]byte(ast.EscapeSymbolName(s))) }
	table := func(source ast.SymbolTable) []any {
		keys := []string{}
		for key := range source {
			keys = append(keys, key)
		}
		slices.Sort(keys)
		rows := []any{}
		for _, key := range keys {
			rows = append(rows, []any{name(key), sid(source[key])})
		}
		return rows
	}
	globals := table(c.globals)
	patterns := []any{}
	for _, p := range c.patternAmbientModules {
		patterns = append(patterns, []any{name(p.Pattern.Text), sid(p.Symbol)})
	}
	augmentations := table(c.patternAmbientModuleAugmentations)
	augmentationTargets := table(c.patternAmbientModuleAugmentationTargets)
	globalMap := map[string]*Type{
		"Array": c.globalArrayType, "Object": c.globalObjectType, "Function": c.globalFunctionType,
		"CallableFunction": c.globalCallableFunctionType, "NewableFunction": c.globalNewableFunctionType,
		"String": c.globalStringType, "Number": c.globalNumberType, "Boolean": c.globalBooleanType,
		"RegExp": c.globalRegExpType, "ReadonlyArray": c.globalReadonlyArrayType, "ThisType": c.globalThisType,
	}
	globalNames := []string{}
	for key := range globalMap {
		globalNames = append(globalNames, key)
	}
	slices.Sort(globalNames)
	globalTypes := []any{}
	for _, key := range globalNames {
		globalTypes = append(globalTypes, []any{key, tid(globalMap[key])})
	}
	specialTypes := []int{}
	for _, symbol := range []*ast.Symbol{c.undefinedSymbol, c.argumentsSymbol, c.unknownSymbol, c.globalThisSymbol} {
		specialTypes = append(specialTypes, tid(c.valueSymbolLinks.Get(symbol).resolvedType))
	}
	specialTypes = append(specialTypes, tid(c.anyArrayType), tid(c.autoArrayType), tid(c.anyReadonlyArrayType))
	declarations := []any{}
	for _, node := range nodes {
		if node.Symbol() != nil {
			declarations = append(declarations, []any{nodeIDs[node], sid(c.getSymbolOfDeclaration(node))})
		}
	}
	classes := []any{}
	for _, node := range nodes {
		if ast.IsClassLike(node) || ast.IsInterfaceDeclaration(node) {
			classes = append(classes, []any{nodeIDs[node], tid(c.getDeclaredTypeOfClassOrInterface(c.getSymbolOfDeclaration(node)))})
		}
	}
	scopes := []any{}
	for _, node := range nodes {
		if ast.IsTypeReferenceNode(node) || ast.IsThisTypeNode(node) || ast.IsTypeParameterDeclaration(node) {
			values := tids(c.getOuterTypeParameters(node, true))
			if values == nil {
				values = []int{}
			}
			scopes = append(scopes, []any{nodeIDs[node], values})
		}
	}
	aliasRows := []any{}
	if aliasQueries {
		seen := map[*ast.Symbol]bool{}
		for _, node := range nodes {
			symbol := c.getSymbolOfDeclaration(node)
			if symbol == nil || symbol.Flags&ast.SymbolFlagsAlias == 0 || seen[symbol] {
				continue
			}
			seen[symbol] = true
			id := sid(symbol)
			target := sid(c.resolveAlias(symbol))
			immediate := sid(c.getImmediateAliasedSymbol(symbol))
			flags := c.getSymbolFlags(symbol)
			withoutTypeOnly := c.getSymbolFlagsEx(symbol, true, false)
			withoutLocal := c.getSymbolFlagsEx(symbol, false, true)
			aliasRows = append(aliasRows, []any{
				id, target, immediate, flags, withoutTypeOnly, withoutLocal,
				nodeIDs[c.getTypeOnlyAliasDeclaration(symbol)], nodeIDs[c.getTypeOnlyAliasDeclarationEx(symbol, ast.SymbolFlagsValue)],
			})
		}
	}
	typeRows := []any{}
	for i := 0; i < len(types); i++ {
		t := types[i]
		symbol := sid(t.symbol)
		var target, this, constraint *Type
		var args, params []*Type
		outer := 0
		isThis := false
		intrinsic := ""
		if t.flags&TypeFlagsObject != 0 {
			target = t.AsObjectType().target
			if t.objectFlags&ObjectFlagsReference != 0 {
				args = t.AsTypeReference().resolvedTypeArguments
			}
			if t.objectFlags&ObjectFlagsClassOrInterface != 0 {
				d := t.AsInterfaceType()
				params = append([]*Type{}, d.allTypeParameters...)
				outer, this = d.outerTypeParameterCount, d.thisType
			}
		} else if t.flags&TypeFlagsTypeParameter != 0 {
			d := t.AsTypeParameter()
			target, constraint, isThis = d.target, d.constraint, d.isThisType
		}
		if t.flags&TypeFlagsIntrinsic != 0 {
			intrinsic = t.AsIntrinsicType().intrinsicName
		}
		targetID, argIDs, paramIDs := tid(target), tids(args), tids(params)
		typeRows = append(typeRows, []any{t.flags, t.objectFlags, symbol, targetID, argIDs, paramIDs, outer, tid(this), isThis, tid(constraint), intrinsic})
	}
	symbolRows := []any{}
	for i := 0; i < len(symbols); i++ {
		symbol := symbols[i]
		parent := sid(symbol.Parent)
		declarations := []int{}
		for _, node := range symbol.Declarations {
			declarations = append(declarations, nodeIDs[node])
		}
		symbolRows = append(symbolRows, []any{
			name(symbol.Name), symbol.Flags, symbol.CheckFlags, parent,
			declarations, nodeIDs[symbol.ValueDeclaration], table(symbol.Members), table(symbol.Exports),
		})
	}
	diagnostics := []int{}
	for _, diagnostic := range c.diagnostics.GetDiagnostics() {
		diagnostics = append(diagnostics, int(diagnostic.Code()))
	}
	slices.Sort(diagnostics)
	result := map[string]any{
		"files": files, "globals": globals, "patterns": patterns, "augmentations": augmentations,
		"augmentationTargets": augmentationTargets, "globalTypes": globalTypes, "specialTypes": specialTypes,
		"declarations": declarations, "classes": classes, "scopes": scopes, "types": typeRows, "symbols": symbolRows, "diagnostics": diagnostics,
	}
	if aliasQueries {
		result["aliases"] = aliasRows
	}
	return result
}
