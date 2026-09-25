// Development-only instrumentation, copied into the frozen reference checkout.
package checker

import (
	"encoding/base64"
	"fmt"
	"math"
	"slices"
	"strconv"
	"strings"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/jsnum"
	"github.com/microsoft/TypeScript/tsc/internal/nodebuilder"
	"github.com/microsoft/TypeScript/tsc/internal/printer"
)

func (c *Checker) csharpAccessibilityQueries(nodes []*ast.Node, nodeIDs map[*ast.Node]int, sid func(*ast.Symbol) int) []any {
	targets := []*ast.Symbol{}
	seen := map[*ast.Symbol]bool{}
	locations := []*ast.Node{}
	for _, node := range nodes {
		file := ast.GetSourceFileOfNode(node)
		if file.FileName() != "/project/globals.d.ts" && ast.IsDeclaration(node) {
			if s := c.getSymbolOfDeclaration(node); s != nil && !seen[s] {
				seen[s] = true
				targets = append(targets, s)
			}
		}
		if strings.HasPrefix(file.FileName(), "/project/main.") {
			switch node.Kind {
			case ast.KindSourceFile, ast.KindModuleDeclaration, ast.KindClassDeclaration, ast.KindClassExpression, ast.KindFunctionDeclaration:
				locations = append(locations, node)
			}
		}
	}
	nids := func(nodes []*ast.Node) []int {
		ids := []int{}
		for _, node := range nodes {
			ids = append(ids, nodeIDs[node])
		}
		slices.Sort(ids)
		return ids
	}
	rows := []any{}
	for _, location := range locations {
		for _, target := range targets {
			targetID := sid(target)
			for _, meaning := range []ast.SymbolFlags{ast.SymbolFlagsValue, ast.SymbolFlagsType, ast.SymbolFlagsNamespace} {
				containers := []int{}
				for _, container := range c.getContainersOfSymbol(target, location, meaning) {
					containers = append(containers, sid(container))
				}
				rows = append(rows, []any{0, nodeIDs[location], targetID, uint32(meaning), containers})
				for _, aliases := range []bool{false, true} {
					for _, modules := range []bool{false, true} {
						result := c.isSymbolAccessibleWorker(target, location, meaning, aliases, modules)
						rows = append(rows, []any{1, nodeIDs[location], targetID, uint32(meaning), aliases, modules, int(result.Accessibility), nids(result.AliasesToMakeVisible), nodeIDs[result.ErrorNode]})
					}
				}
				rows = append(rows, []any{2, nodeIDs[location], targetID, uint32(meaning), c.IsSymbolAccessibleByFlags(target, location, meaning)})
			}
			rows = append(rows, []any{3, nodeIDs[location], targetID, c.IsTypeSymbolAccessible(target, location), c.IsValueSymbolAccessible(target, location)})
		}
	}
	for _, node := range nodes {
		if strings.HasPrefix(ast.GetSourceFileOfNode(node).FileName(), "/project/main.") && (ast.IsEntityName(node) || ast.IsEntityNameExpression(node)) {
			result := c.GetEmitResolver().IsEntityNameVisible(node, node)
			rows = append(rows, []any{4, nodeIDs[node], int(result.Accessibility), nids(result.AliasesToMakeVisible), result.ErrorSymbolName, result.ErrorModuleName, nodeIDs[result.ErrorNode]})
		}
	}
	return rows
}

func (c *Checker) csharpSymbolChains(nodes []*ast.Node, nodeIDs map[*ast.Node]int, sid func(*ast.Symbol) int) []any {
	targets := []*ast.Symbol{}
	seen := map[*ast.Symbol]bool{}
	locations := []*ast.Node{nil}
	for _, node := range nodes {
		file := ast.GetSourceFileOfNode(node)
		if file.FileName() != "/project/globals.d.ts" && ast.IsDeclaration(node) {
			if symbol := c.getSymbolOfDeclaration(node); symbol != nil && !seen[symbol] {
				seen[symbol] = true
				targets = append(targets, symbol)
			}
		}
		if strings.HasPrefix(file.FileName(), "/project/main.") {
			switch node.Kind {
			case ast.KindSourceFile, ast.KindBlock, ast.KindModuleDeclaration, ast.KindClassDeclaration, ast.KindClassExpression, ast.KindInterfaceDeclaration, ast.KindFunctionDeclaration, ast.KindFunctionExpression, ast.KindArrowFunction:
				locations = append(locations, node)
			}
		}
	}
	if global := c.globals["globalValue"]; global != nil {
		targets = append(targets, global)
	}
	rows := []any{}
	for _, location := range locations {
		for _, target := range targets {
			for _, meaning := range []ast.SymbolFlags{ast.SymbolFlagsValue, ast.SymbolFlagsType, ast.SymbolFlagsNamespace} {
				for _, externalOnly := range []bool{false, true} {
					targetID := sid(target)
					chain := c.GetAccessibleSymbolChain(target, location, meaning, externalOnly)
					var result any
					if len(chain) > 0 {
						ids := []int{}
						for _, symbol := range chain {
							ids = append(ids, sid(symbol))
						}
						result = ids
					}
					rows = append(rows, []any{nodeIDs[location], targetID, uint32(meaning), externalOnly, result})
				}
			}
		}
	}
	return rows
}

func (c *Checker) csharpVisibilityQueries(nodes []*ast.Node, nodeIDs map[*ast.Node]int, sid func(*ast.Symbol) int) []any {
	type entry struct {
		node               *ast.Node
		owner              int
		documentationIndex int
	}
	type declaration struct {
		node   *ast.Node
		symbol *ast.Symbol
	}
	entries := []entry{}
	declarations := []declaration{}
	seen := map[*ast.Symbol]bool{}
	files := []*ast.SourceFile{}
	for _, node := range nodes {
		file := ast.GetSourceFileOfNode(node)
		if !strings.HasPrefix(file.FileName(), "/project/main.") {
			continue
		}
		entries = append(entries, entry{node, nodeIDs[node], -1})
		index := 0
		for _, comment := range node.JSDoc(file) {
			pending := []*ast.Node{comment}
			for len(pending) > 0 {
				part := pending[len(pending)-1]
				pending = pending[:len(pending)-1]
				entries = append(entries, entry{part, nodeIDs[node], index})
				index++
				children := []*ast.Node{}
				part.ForEachChild(func(child *ast.Node) bool { children = append(children, child); return false })
				for i := len(children) - 1; i >= 0; i-- {
					pending = append(pending, children[i])
				}
			}
		}
		if ast.IsSourceFile(node) {
			files = append(files, node.AsSourceFile())
		}
		if ast.IsDeclaration(node) {
			if symbol := c.getSymbolOfDeclaration(node); symbol != nil && !seen[symbol] {
				seen[symbol] = true
				declarations = append(declarations, declaration{node, symbol})
			}
		}
	}
	r := c.GetEmitResolver()
	rows := []any{}
	visibility := func(phase int) {
		for _, entry := range entries {
			rows = append(rows, []any{phase, entry.owner, entry.documentationIndex, int(entry.node.Kind), r.IsDeclarationVisible(entry.node)})
		}
	}
	visibility(0)
	for _, file := range files {
		r.PrecalculateDeclarationEmitVisibility(file)
	}
	visibility(1)
	for _, declaration := range declarations {
		for _, compute := range []bool{false, true} {
			result := r.hasVisibleDeclarations(declaration.symbol, compute)
			var aliases any
			if result != nil {
				ids := []int{}
				for _, alias := range result.AliasesToMakeVisible {
					ids = append(ids, nodeIDs[alias])
				}
				slices.Sort(ids)
				aliases = ids
			}
			rows = append(rows, []any{2, nodeIDs[declaration.node], sid(declaration.symbol), compute, aliases})
		}
	}
	visibility(3)
	for _, file := range files {
		r.PrecalculateDeclarationEmitVisibility(file)
	}
	visibility(4)
	return rows
}

func (c *Checker) csharpContextQueries(nodes []*ast.Node, nodeIDs map[*ast.Node]int, tid func(*Type) int, sid func(*ast.Symbol) int) ([]any, []any) {
	rows, graph := []any{}, []any{}
	queue := []*Signature{}
	ids := map[*Signature]int{nil: 0}
	qid := func(signature *Signature) int {
		if id, ok := ids[signature]; ok {
			return id
		}
		queue = append(queue, signature)
		ids[signature] = len(queue)
		return len(queue)
	}
	qids := func(signatures []*Signature) []int {
		result := make([]int, len(signatures))
		for i, signature := range signatures {
			result[i] = qid(signature)
		}
		return result
	}
	for _, node := range nodes {
		if !strings.HasPrefix(ast.GetSourceFileOfNode(node).FileName(), "/project/main.") {
			continue
		}
		if ast.IsExpressionNode(node) {
			for _, flags := range []ContextFlags{ContextFlagsNone, ContextFlagsSignature, ContextFlagsNoConstraints, ContextFlagsIgnoreNodeInferences, ContextFlagsSkipBindingPatterns, ContextFlagsNoConstraints | ContextFlagsIgnoreNodeInferences} {
				rows = append(rows, []any{0, nodeIDs[node], uint32(flags), tid(c.GetContextualType(node, flags))})
			}
		}
		if node.Parent != nil && ast.IsObjectLiteralExpression(node.Parent) {
			rows = append(rows, []any{1, nodeIDs[node], tid(c.GetContextualTypeForObjectLiteralElement(node, ContextFlagsNone))})
		}
		if ast.IsArrayLiteralExpression(node) {
			contextual := c.GetContextualType(node, ContextFlagsNone)
			for i := 0; i <= len(node.Elements()); i++ {
				position := node.End()
				if i < len(node.Elements()) {
					position = node.Elements()[i].Pos()
				}
				rows = append(rows, []any{2, nodeIDs[node], i, tid(c.GetContextualTypeForArrayLiteralAtPosition(contextual, node, position))})
			}
		}
		if ast.IsJsxAttribute(node) || ast.IsJsxSpreadAttribute(node) {
			rows = append(rows, []any{3, nodeIDs[node], tid(c.GetContextualTypeForJsxAttribute(node))})
		}
		if !ast.IsCallLikeExpression(node) {
			continue
		}
		signature := c.GetResolvedSignature(node)
		rows = append(rows, []any{4, nodeIDs[node], qid(signature), tid(c.GetReturnTypeOfSignature(signature))})
		count := 1
		if ast.IsCallExpression(node) || ast.IsNewExpression(node) {
			count = len(node.Arguments())
		}
		for i := 0; i <= count; i++ {
			rows = append(rows, []any{5, nodeIDs[node], i, tid(c.GetContextualTypeForArgumentAtIndex(node, i))})
		}
		for _, argumentCount := range []int{0, count, count + 1} {
			selected, candidates := GetResolvedSignatureForSignatureHelp(node, argumentCount, c)
			rows = append(rows, []any{6, nodeIDs[node], argumentCount, qid(selected), qids(candidates)})
		}
		if ast.IsCallExpression(node) || ast.IsNewExpression(node) {
			for _, argument := range node.Arguments() {
				if ast.IsStringLiteral(argument) {
					rows = append(rows, []any{7, nodeIDs[node], nodeIDs[argument], qids(c.GetCandidateSignaturesForStringLiteralCompletions(node, argument))})
				}
			}
		}
	}
	for i := 0; i < len(queue); i++ {
		s := queue[i]
		generic := []int{}
		for _, parameter := range s.typeParameters {
			generic = append(generic, tid(parameter))
		}
		receiver := sid(s.thisParameter)
		parameters := []any{}
		for _, parameter := range s.parameters {
			parameters = append(parameters, []any{sid(parameter), tid(c.getTypeOfSymbol(parameter))})
		}
		result := tid(c.GetReturnTypeOfSignature(s))
		target := qid(s.target)
		var union any
		var parts []int
		if s.composite != nil {
			union = s.composite.isUnion
			parts = qids(s.composite.signatures)
		}
		var predicate any
		if p := s.resolvedTypePredicate; p != nil {
			predicate = []any{p.kind, p.parameterIndex, p.parameterName, tid(p.t)}
		}
		graph = append(graph, []any{s.flags, nodeIDs[s.declaration], s.minArgumentCount, s.resolvedMinArgumentCount, generic, receiver, parameters, result, target, union, parts, predicate})
	}
	return rows, graph
}

func (c *Checker) CSharpProgramScopeProbe(aliasQueries bool, typeNodes bool, memberQueries bool, valueQueries bool, propertyQueries bool, signatureQueries bool, identityQueries bool, assignabilityQueries bool, indexingQueries bool, constantQueries bool, expressionQueries bool, awaitedQueries bool, referenceQueries bool, flowQueries bool, identifierQueries bool, accessQueries bool, callQueries bool, assertionQueries bool, locations bool, symbolLocations bool, documentationSymbols bool, scopeServices bool, contextQueries bool, declarationVisibility bool, symbolChains bool, accessibility bool, symbolDisplay bool, symbolFormats bool, symbolFormatValues []SymbolFormatFlags, symbolTypeNodes bool, typeSyntax bool, signatureSyntax bool, emitQueries bool, emitReferences bool, emitSerialization bool, emitLinks bool, emitJsx bool, emitServices bool, emitSyntax bool, typeSyntaxFlags ...nodebuilder.Flags) any {
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
	// Inferred parameter sets come from a Go map. Canonicalize only their
	// serialized order; leave the checker's parameter lists and mappers intact.
	orderedInferences := func(values []*Type) []*Type {
		result := slices.Clone(values)
		groups := map[*ast.Node][]int{}
		for i, t := range values {
			if t.symbol == nil || len(t.symbol.Declarations) == 0 {
				continue
			}
			d := t.symbol.Declarations[0]
			if d.Parent == nil || !ast.IsInferTypeNode(d.Parent) {
				continue
			}
			for owner := d.Parent.Parent; owner != nil; owner = owner.Parent {
				if ast.IsConditionalTypeNode(owner) {
					groups[owner] = append(groups[owner], i)
					break
				}
			}
		}
		for _, positions := range groups {
			parameters := make([]*Type, len(positions))
			for i, position := range positions {
				parameters[i] = values[position]
			}
			slices.SortFunc(parameters, func(a, b *Type) int { return nodeIDs[a.symbol.Declarations[0]] - nodeIDs[b.symbol.Declarations[0]] })
			for i, position := range positions {
				result[position] = parameters[i]
			}
		}
		return result
	}
	// Private member names contain process-global allocator IDs. Only serialization
	// replaces them with the declaring class's stable AST node ID.
	privateOwners := map[string]int{}
	for _, node := range nodes {
		if ast.IsClassLike(node) && node.Symbol() != nil {
			for _, members := range []ast.SymbolTable{node.Symbol().Members, node.Symbol().Exports} {
				for key := range members {
					if strings.HasPrefix(key, ast.InternalSymbolNamePrefix+"#") {
						if end := strings.IndexByte(key, '@'); end > 0 {
							privateOwners[key[:end]] = nodeIDs[node]
						}
					}
				}
			}
		}
	}
	canonicalName := func(s string) string {
		// Unique-symbol keys embed an allocator ID. Preserve the symbol name and
		// identity using its declaration, without resolving any additional types.
		if strings.HasPrefix(s, ast.InternalSymbolNamePrefix+"@") {
			for symbol, unique := range c.uniqueESSymbolTypes {
				if unique.AsUniqueESSymbolType().name == s && len(symbol.Declarations) != 0 {
					return ast.InternalSymbolNamePrefix + "@" + symbol.Name + "@node" + strconv.Itoa(nodeIDs[symbol.Declarations[0]])
				}
			}
		}
		if end := strings.IndexByte(s, '@'); end > 0 {
			if owner, ok := privateOwners[s[:end]]; ok {
				return ast.InternalSymbolNamePrefix + "#node" + strconv.Itoa(owner) + s[end:]
			}
		}
		return s
	}
	name := func(s string) string {
		return base64.StdEncoding.EncodeToString([]byte(ast.EscapeSymbolName(canonicalName(s))))
	}
	table := func(source ast.SymbolTable) []any {
		keys := []string{}
		for key := range source {
			keys = append(keys, key)
		}
		slices.SortFunc(keys, func(a, b string) int { return strings.Compare(canonicalName(a), canonicalName(b)) })
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
			symbol := node.Symbol()
			if referenceQueries {
				symbol = c.getMergedSymbol(symbol)
			} else {
				symbol = c.getSymbolOfDeclaration(node)
			}
			declarations = append(declarations, []any{nodeIDs[node], sid(symbol)})
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
			values := tids(orderedInferences(c.getOuterTypeParameters(node, true)))
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
	var accessibilityRows []any
	var symbolDisplayRows []any
	var typeSyntaxRows []any
	if typeSyntax {
		typeSyntaxRows = c.csharpTypeSyntax(nodes, nodeIDs, typeSyntaxFlags)
	}
	if signatureSyntax {
		typeSyntaxRows = c.csharpSignatureSyntax(nodes, nodeIDs)
	}
	var emitRows []any
	if emitQueries {
		emitRows = c.csharpEmitQueries(nodes, nodeIDs)
	}
	if emitReferences {
		emitRows = c.csharpEmitReferences(nodes, nodeIDs, sid)
	}
	if emitSerialization {
		emitRows = c.csharpEmitSerialization(nodes, nodeIDs)
	}
	if emitLinks {
		emitRows = c.csharpEmitLinks(nodes, nodeIDs)
	}
	if emitJsx {
		emitRows = c.csharpEmitJsx(nodes, nodeIDs)
	}
	if emitServices {
		emitRows = c.csharpEmitServices(nodes, nodeIDs)
	}
	if emitSyntax {
		emitRows = c.csharpEmitSyntax(nodes, nodeIDs)
	}
	var symbolTypeNodeRows []any
	if symbolTypeNodes {
		symbolTypeNodeRows = c.csharpSymbolTypeNodes(nodes, nodeIDs, sid)
	}
	var symbolFormatRows []any
	if symbolFormats {
		symbolFormatRows = c.csharpSymbolDisplayQueries(nodes, nodeIDs, sid, true, symbolFormatValues)
	}
	if symbolDisplay {
		symbolDisplayRows = c.csharpSymbolDisplayQueries(nodes, nodeIDs, sid, false, nil)
	}
	if accessibility {
		accessibilityRows = c.csharpAccessibilityQueries(nodes, nodeIDs, sid)
	}
	var chainRows []any
	if symbolChains {
		chainRows = c.csharpSymbolChains(nodes, nodeIDs, sid)
	}
	var visibilityRows []any
	if declarationVisibility {
		visibilityRows = c.csharpVisibilityQueries(nodes, nodeIDs, sid)
	}
	var contextRows, contextSignatures []any
	if contextQueries {
		contextRows, contextSignatures = c.csharpContextQueries(nodes, nodeIDs, tid, sid)
	}
	serviceQueries := []any{}
	if scopeServices {
		orderedSymbols := func(source []*ast.Symbol) []int {
			slices.SortFunc(source, func(a, b *ast.Symbol) int { return strings.Compare(canonicalName(a.Name), canonicalName(b.Name)) })
			ids := make([]int, len(source))
			for i, symbol := range source {
				ids[i] = sid(symbol)
			}
			return ids
		}
		seenModules, seenAliases := map[*ast.Symbol]bool{}, map[*ast.Symbol]bool{}
		for _, node := range nodes {
			if !strings.HasPrefix(ast.GetSourceFileOfNode(node).FileName(), "/project/main.") {
				continue
			}
			for _, meaning := range []ast.SymbolFlags{ast.SymbolFlagsValue, ast.SymbolFlagsType, ast.SymbolFlagsNamespace, ast.SymbolFlagsAlias, ast.SymbolFlagsAll} {
				serviceQueries = append(serviceQueries, []any{0, nodeIDs[node], uint32(meaning), orderedSymbols(c.GetSymbolsInScope(node, meaning))})
			}
			symbol := c.GetSymbolAtLocation(node)
			if symbol != nil {
				serviceQueries = append(serviceQueries, []any{1, nodeIDs[node], sid(symbol), tid(c.GetTypeOfSymbolAtLocation(symbol, node)), tid(c.GetTypeOfSymbolAtLocation(symbol, nil))})
				if symbol.Flags&ast.SymbolFlagsAlias != 0 && !seenAliases[symbol] {
					seenAliases[symbol] = true
					serviceQueries = append(serviceQueries, []any{3, nodeIDs[node], sid(symbol), sid(c.GetAliasedSymbol(symbol))})
				}
			}
			if ast.IsDeclaration(node) {
				module := c.getSymbolOfDeclaration(node)
				if module != nil && module.Flags&ast.SymbolFlagsModule != 0 && !seenModules[module] {
					seenModules[module] = true
					serviceQueries = append(serviceQueries, []any{2, nodeIDs[node], sid(module), orderedSymbols(c.GetExportsOfModule(module))})
				}
			}
			if ast.IsExportSpecifier(node) {
				serviceQueries = append(serviceQueries, []any{4, nodeIDs[node], sid(c.GetExportSpecifierLocalTargetSymbol(node))})
			}
			if ast.IsShorthandPropertyAssignment(node) {
				serviceQueries = append(serviceQueries, []any{5, nodeIDs[node], sid(c.GetShorthandAssignmentValueSymbol(node))})
			}
			if ast.IsParameterPropertyDeclaration(node, node.Parent) && ast.IsIdentifier(node.Name()) {
				parameter, property := c.GetSymbolsOfParameterPropertyDeclaration(node, node.Name().Text())
				serviceQueries = append(serviceQueries, []any{6, nodeIDs[node], sid(parameter), sid(property)})
			}
		}
	}
	symbolLocationQueries := []any{}
	if symbolLocations {
		for _, node := range nodes {
			if strings.HasPrefix(ast.GetSourceFileOfNode(node).FileName(), "/project/main.") {
				symbolLocationQueries = append(symbolLocationQueries, []any{nodeIDs[node], sid(c.GetSymbolAtLocation(node))})
			}
		}
	}
	documentationQueries := []any{}
	if documentationSymbols {
		for _, owner := range nodes {
			file := ast.GetSourceFileOfNode(owner)
			if !strings.HasPrefix(file.FileName(), "/project/main.") {
				continue
			}
			for _, comment := range owner.JSDoc(file) {
				pending := []*ast.Node{comment}
				for len(pending) != 0 {
					node := pending[len(pending)-1]
					pending = pending[:len(pending)-1]
					documentationQueries = append(documentationQueries, []any{nodeIDs[owner], int(node.Kind), node.Pos(), node.End(), sid(c.GetSymbolAtLocation(node))})
					children := []*ast.Node{}
					node.ForEachChild(func(child *ast.Node) bool { children = append(children, child); return false })
					for i := len(children) - 1; i >= 0; i-- {
						pending = append(pending, children[i])
					}
				}
			}
		}
	}
	locationQueries := []any{}
	if locations {
		for _, node := range nodes {
			if strings.HasPrefix(ast.GetSourceFileOfNode(node).FileName(), "/project/main.") {
				locationQueries = append(locationQueries, []any{nodeIDs[node], tid(c.GetTypeAtLocation(node))})
			}
		}
	}
	typeQueries := []any{}
	if typeNodes {
		for _, node := range nodes {
			var result *Type
			if ast.IsTypeOrJSTypeAliasDeclaration(node) || ast.IsEnumDeclaration(node) {
				result = c.getDeclaredTypeOfSymbol(c.getSymbolOfDeclaration(node))
			} else if node.Type() != nil && (ast.IsFunctionLike(node) || ast.IsVariableDeclaration(node) || ast.IsPropertyDeclaration(node) || ast.IsPropertySignatureDeclaration(node) || ast.IsParameterDeclaration(node)) {
				result = c.getTypeFromTypeNode(node.Type())
			}
			if result != nil {
				typeQueries = append(typeQueries, []any{nodeIDs[node], tid(result)})
			}
		}
	}
	accessRows, accessSymbols := []any{}, []any{}
	if accessQueries {
		for _, node := range nodes {
			if ast.IsCallExpression(node) && ast.IsIdentifier(node.Expression()) && node.Expression().Text() == "__access" {
				for _, argument := range node.Arguments() {
					accessRows = append(accessRows, []any{nodeIDs[argument], tid(c.checkExpression(argument))})
				}
			}
		}
	}
	if accessQueries {
		for _, node := range nodes {
			if ast.IsAccessExpression(node) || ast.IsQualifiedName(node) {
				accessSymbols = append(accessSymbols, []any{nodeIDs[node], sid(c.getResolvedSymbolOrNil(node))})
			}
		}
	}
	identifierRows, identifierAliases := []any{}, []any{}
	if identifierQueries {
		for _, node := range nodes {
			if ast.IsCallExpression(node) && ast.IsIdentifier(node.Expression()) && node.Expression().Text() == "__expr" {
				for _, argument := range node.Arguments() {
					identifierRows = append(identifierRows, []any{nodeIDs[argument], tid(c.checkExpression(argument))})
				}
			}
		}
	}
	if identifierQueries {
		seen := map[*ast.Symbol]bool{}
		for _, node := range nodes {
			symbol := c.getSymbolOfDeclaration(node)
			if symbol != nil && symbol.Flags&ast.SymbolFlagsAlias != 0 && !seen[symbol] {
				seen[symbol] = true
				identifierAliases = append(identifierAliases, []any{sid(symbol), c.aliasSymbolLinks.Get(symbol).referenced})
			}
		}
	}
	flowRows := []any{}
	if flowQueries {
		for _, node := range nodes {
			if ast.IsCallExpression(node) && ast.IsIdentifier(node.Expression()) && node.Expression().Text() == "__flow" {
				for _, argument := range node.Arguments() {
					if ast.IsIdentifier(argument) {
						declared := c.getTypeOfSymbol(c.getResolvedSymbol(argument))
						flowRows = append(flowRows, []any{nodeIDs[argument], tid(declared), tid(c.getFlowTypeOfReference(argument, declared)), c.isReachableFlowNode(getFlowNodeOfNode(argument))})
					}
				}
			}
		}
	}
	assignmentRows := []any{}
	if flowQueries {
		for _, node := range nodes {
			if ast.IsVariableDeclaration(node) || ast.IsParameterDeclaration(node) {
				symbol := c.getSymbolOfDeclaration(node)
				if symbol != nil && c.isParameterOrMutableLocalVariable(symbol) {
					c.ensureAssignmentsMarked(symbol)
					data := c.markedAssignmentSymbolLinks.Get(symbol)
					assignmentRows = append(assignmentRows, []any{nodeIDs[node], int(data.lastAssignmentPos), data.hasDefiniteAssignment})
				}
			}
		}
	}
	referenceRows, referenceSyntax, declarationOrder := []any{}, []any{}, []any{}
	if referenceQueries {
		for _, node := range nodes {
			if ast.IsCallExpression(node) && ast.IsIdentifier(node.Expression()) && node.Expression().Text() == "__order" {
				for _, argument := range node.Arguments() {
					if ast.IsPropertyAccessExpression(argument) {
						owner := ast.GetContainingClass(argument)
						var declaration *ast.Node
						for _, candidate := range nodes {
							if (ast.IsPropertyDeclaration(candidate) || ast.IsMethodDeclaration(candidate) || ast.IsParameterPropertyDeclaration(candidate, candidate.Parent)) && ast.GetContainingClass(candidate) == owner && candidate.Name().Text() == argument.Name().Text() {
								declaration = candidate
								break
							}
						}
						declarationOrder = append(declarationOrder, []any{nodeIDs[argument], nodeIDs[declaration], c.isBlockScopedNameDeclaredBeforeUse(declaration, argument.Name())})
					}
				}
			}
			access := 0
			if ast.IsWriteOnlyAccess(node) {
				access = 1
			} else if ast.IsWriteAccess(node) {
				access = 2
			}
			var target *ast.Node
			assignment := AssignmentKindNone
			if node.Parent != nil {
				target = ast.GetAssignmentTarget(node)
				assignment = getAssignmentTargetKind(node)
			}
			referenceSyntax = append(referenceSyntax, []any{nodeIDs[node], ast.IsExpressionNode(node), ast.IsValidTypeOnlyAliasUseSite(node), access, nodeIDs[target], int(assignment)})
			if ast.IsCallExpression(node) && ast.IsIdentifier(node.Expression()) && node.Expression().Text() == "__use" {
				for _, argument := range node.Arguments() {
					if ast.IsIdentifier(argument) {
						referenceRows = append(referenceRows, []any{nodeIDs[argument], sid(c.getResolvedSymbol(argument))})
					}
				}
			}
		}
	}
	awaitedRows := []any{}
	if awaitedQueries {
		for _, node := range nodes {
			if (ast.IsTypeAliasDeclaration(node) || ast.IsInterfaceDeclaration(node)) && len(node.Name().Text()) > 1 && node.Name().Text()[0] == 'A' && node.Name().Text()[1] >= '0' && node.Name().Text()[1] <= '9' {
				t := c.getDeclaredTypeOfSymbol(c.getSymbolOfDeclaration(node))
				id := tid(t)
				promised := tid(c.getPromisedTypeOfPromiseEx(t, nil, nil))
				plain := tid(c.getAwaitedTypeNoAlias(t))
				wrapped := tid(c.getAwaitedType(t))
				needed := c.isAwaitedTypeNeeded(t)
				thenable := c.isThenableType(t)
				awaitedRows = append(awaitedRows, []any{nodeIDs[node], id, promised, plain, wrapped, needed, thenable})
			}
		}
	}
	constantRows, expressionRows := []any{}, []any{}
	if constantQueries || expressionQueries {
		for _, node := range nodes {
			if expressionQueries && ast.IsVariableDeclaration(node) && node.Initializer() != nil {
				expressionRows = append(expressionRows, []any{nodeIDs[node.Initializer()], tid(c.checkExpression(node.Initializer()))})
			}
			if constantQueries && ast.IsEnumMember(node) {
				value := c.getEnumMemberValue(node)
				var scalar any
				switch v := value.Value.(type) {
				case string:
					scalar = []any{"string", base64.StdEncoding.EncodeToString([]byte(v))}
				case jsnum.Number:
					scalar = []any{"number", fmt.Sprintf("%016x", math.Float64bits(float64(v)))}
				}
				constantRows = append(constantRows, []any{nodeIDs[node], scalar, value.IsSyntacticallyString, value.ResolvedOtherFiles, value.HasExternalReferences})
			}
		}
	}
	keyRows, indexRows := []any{}, []any{}
	if indexingQueries {
		for _, node := range nodes {
			if ast.IsTypeOperatorNode(node) && node.AsTypeOperatorNode().Operator == ast.KindKeyOfKeyword {
				t := c.getTypeFromTypeNode(node.Type())
				for flags := IndexFlags(0); flags < 8; flags++ {
					keyRows = append(keyRows, []any{nodeIDs[node], flags, tid(c.getIndexTypeEx(t, flags))})
				}
			}
			if ast.IsIndexedAccessTypeNode(node) {
				d := node.AsIndexedAccessTypeNode()
				objectType, indexType := c.getTypeFromTypeNode(d.ObjectType), c.getTypeFromTypeNode(d.IndexType)
				for _, flags := range []AccessFlags{0, 1, 2, 3, 4, 5, 16, 32, 64, 128} {
					t := c.getIndexedAccessTypeEx(objectType, indexType, flags, nil, nil)
					id := tid(t)
					read := tid(c.getSimplifiedType(t, false))
					write := tid(c.getSimplifiedType(t, true))
					indexRows = append(indexRows, []any{nodeIDs[node], flags, id, read, write})
				}
			}
		}
	}
	memberRoots, memberRows := []any{}, []any{}
	valueRows := []any{}
	signatureRows := []any{}
	callRows := []any{}
	if memberQueries {
		pending := []*Type{}
		seen := map[*Type]bool{}
		mtid := func(t *Type) int {
			id := tid(t)
			if t != nil && (t.flags&TypeFlagsObject != 0 || signatureQueries && t.flags&TypeFlagsStructuredType != 0) && !seen[t] {
				seen[t] = true
				pending = append(pending, t)
			}
			return id
		}
		for _, node := range nodes {
			var t *Type
			if ast.IsInterfaceDeclaration(node) || ast.IsTypeAliasDeclaration(node) {
				t = c.getDeclaredTypeOfSymbol(c.getSymbolOfDeclaration(node))
			} else if ast.IsFunctionDeclaration(node) {
				t = c.getTypeOfSymbol(c.getSymbolOfDeclaration(node))
			}
			if t != nil {
				memberRoots = append(memberRoots, []any{nodeIDs[node], mtid(t)})
			}
		}
		if valueQueries {
			seenSymbols := map[*ast.Symbol]bool{}
			for _, node := range nodes {
				symbol := c.getSymbolOfDeclaration(node)
				if symbol == nil || symbol.Flags&(ast.SymbolFlagsValue|ast.SymbolFlagsAlias) == 0 || seenSymbols[symbol] {
					continue
				}
				seenSymbols[symbol] = true
				valueRows = append(valueRows, []any{nodeIDs[node], sid(symbol), mtid(c.getTypeOfSymbol(symbol)), mtid(c.getWriteTypeOfSymbol(symbol))})
			}
		}
		signatureIDs := map[*Signature]int{nil: 0}
		signatureQueue := []*Signature{}
		qid := func(s *Signature) int {
			if id, ok := signatureIDs[s]; ok {
				return id
			}
			signatureQueue = append(signatureQueue, s)
			signatureIDs[s] = len(signatureQueue)
			return len(signatureQueue)
		}
		signatureRow := func(s *Signature) any {
			stid := mtid
			if signatureQueries {
				stid = tid
			}
			generic := []int{}
			for _, p := range s.typeParameters {
				generic = append(generic, stid(p))
			}
			var receiver any
			if s.thisParameter != nil {
				receiver = []any{sid(s.thisParameter), stid(c.getTypeOfSymbol(s.thisParameter))}
			}
			parameters := []any{}
			for _, p := range s.parameters {
				parameters = append(parameters, []any{sid(p), stid(c.getTypeOfSymbol(p))})
			}
			result := stid(c.getReturnTypeOfSignature(s))
			var predicate any
			if p := c.getTypePredicateOfSignature(s); p != nil {
				predicate = []any{p.kind, p.parameterIndex, p.parameterName, stid(p.t)}
			}
			row := []any{s.flags, nodeIDs[s.declaration], s.minArgumentCount, generic, receiver, parameters, result, predicate}
			if !signatureQueries {
				return row
			}
			id := qid(s)
			count, minimum, syntacticMinimum := c.getParameterCount(s), c.getMinArgumentCount(s), c.getMinArgumentCountEx(s, MinArgumentCountFlagsVoidIsNonOptional)
			rest, effectiveRest := c.hasEffectiveRestParameter(s), tid(c.getEffectiveRestType(s))
			positions := []any{}
			for i := 0; i <= count; i++ {
				name := ""
				if i < count {
					name = c.getParameterNameAtPosition(s, i)
				}
				positions = append(positions, []any{name, tid(c.tryGetTypeAtPosition(s, i)), nodeIDs[c.getNameableDeclarationAtPosition(s, i)]})
			}
			restAt := tid(c.getRestTypeAtPosition(s, 0, false))
			return append(row, []any{id, count, minimum, syntacticMinimum, rest, effectiveRest, positions, restAt})
		}
		if callQueries {
			for _, node := range nodes {
				if ast.IsTaggedTemplateExpression(node) || (ast.IsCallExpression(node) || ast.IsNewExpression(node)) && !(ast.IsIdentifier(node.Expression()) && node.Expression().Text() == "__expr") {
					callRows = append(callRows, []any{nodeIDs[node], qid(c.getResolvedSignature(node, nil, CheckModeNormal))})
				}
			}
		}
		for i := 0; i < len(pending); i++ {
			t := pending[i]
			m := c.resolveStructuredTypeMembers(t)
			properties, calls, constructors, indexes := []any{}, []any{}, []any{}, []any{}
			for _, p := range m.properties {
				property := []any{sid(p), mtid(c.getTypeOfSymbol(p))}
				if valueQueries {
					property = append(property, mtid(c.getWriteTypeOfSymbol(p)))
				}
				properties = append(properties, property)
			}
			for _, s := range m.CallSignatures() {
				calls = append(calls, signatureRow(s))
			}
			for _, s := range m.ConstructSignatures() {
				constructors = append(constructors, signatureRow(s))
			}
			for _, ix := range m.indexInfos {
				indexes = append(indexes, []any{mtid(ix.keyType), mtid(ix.valueType), ix.isReadonly, nodeIDs[ix.declaration]})
			}
			memberRows = append(memberRows, []any{tid(t), properties, calls, constructors, indexes})
		}
		if signatureQueries {
			for i := 0; i < len(signatureQueue); i++ {
				s := signatureQueue[i]
				generic := tids(s.typeParameters)
				if generic == nil {
					generic = []int{}
				}
				receiver := sid(s.thisParameter)
				parameters := []int{}
				for _, p := range s.parameters {
					parameters = append(parameters, sid(p))
				}
				result := tid(s.resolvedReturnType)
				var predicate any
				if p := s.resolvedTypePredicate; p != nil {
					predicate = []any{p.kind, p.parameterIndex, p.parameterName, tid(p.t)}
				}
				target := qid(s.target)
				var isUnion any
				var parts []int
				if s.composite != nil {
					isUnion = s.composite.isUnion
					parts = []int{}
					for _, s := range s.composite.signatures {
						parts = append(parts, qid(s))
					}
				}
				signatureRows = append(signatureRows, []any{s.flags, nodeIDs[s.declaration], s.minArgumentCount, s.resolvedMinArgumentCount, generic, receiver, parameters, result, predicate, target, isUnion, parts})
			}
		}
	}
	propertyRows := []any{}
	if propertyQueries {
		for _, node := range nodes {
			if !ast.IsTypeAliasDeclaration(node) && !ast.IsInterfaceDeclaration(node) && !ast.IsClassLike(node) && !ast.IsTypeParameterDeclaration(node) {
				continue
			}
			t := c.getDeclaredTypeOfSymbol(c.getSymbolOfDeclaration(node))
			row := []any{nodeIDs[node], tid(t), tid(c.getApparentType(t)), tid(c.getReducedType(t))}
			properties := []any{}
			for _, p := range c.getPropertiesOfType(t) {
				properties = append(properties, []any{sid(p), tid(c.getTypeOfSymbol(p)), tid(c.getWriteTypeOfSymbol(p))})
			}
			names := []any{}
			for _, name := range []string{"value", "kind", "optional", "missing", "toString", "apply", "0", "1", "length"} {
				p := c.getPropertyOfType(t, name)
				raw := p
				if t.flags&TypeFlagsUnionOrIntersection != 0 {
					raw = c.getUnionOrIntersectionProperty(t, name, false)
				}
				pid, rid := sid(p), sid(raw)
				var read, write *Type
				if raw != nil {
					read, write = c.getTypeOfSymbol(raw), c.getWriteTypeOfSymbol(raw)
				}
				names = append(names, []any{name, pid, rid, tid(read), tid(write)})
			}
			indexes := []any{}
			for _, ix := range c.getIndexInfosOfType(t) {
				indexes = append(indexes, []any{tid(ix.keyType), tid(ix.valueType), ix.isReadonly, nodeIDs[ix.declaration]})
			}
			row = append(row, properties, names, indexes)
			propertyRows = append(propertyRows, row)
		}
	}
	relationRows := []any{}
	varianceRows := []any{}
	factRows := []any{}
	relationKeyRows := []any{}
	if identityQueries {
		type root struct {
			node *ast.Node
			t    *Type
		}
		roots := []root{}
		for _, node := range nodes {
			if ast.IsTypeAliasDeclaration(node) || ast.IsInterfaceDeclaration(node) || ast.IsClassLike(node) {
				name := node.Name().Text()
				if len(name) > 1 && name[0] == 'R' && name[1] >= '0' && name[1] <= '9' {
					roots = append(roots, root{node, c.getDeclaredTypeOfSymbol(c.getSymbolOfDeclaration(node))})
				}
			}
		}
		keys := map[CacheHashKey]int{}
		for _, s := range roots {
			for _, t := range roots {
				sid, tid2 := tid(s.t), tid(t.t)
				if assignabilityQueries {
					results := []any{}
					for _, r := range []*Relation{c.identityRelation, c.subtypeRelation, c.strictSubtypeRelation, c.assignableRelation, c.comparableRelation} {
						result := c.isTypeRelatedTo(s.t, t.t, r)
						results = append(results, []any{result, r.size()})
					}
					relationRows = append(relationRows, []any{nodeIDs[s.node], nodeIDs[t.node], sid, tid2, results})
					continue
				}
				related := c.isTypeIdenticalTo(s.t, t.t)
				count := c.identityRelation.size()
				key, constrained := getRelationKey(s.t, t.t, IntersectionStateNone, true, false)
				id, ok := keys[key]
				if !ok {
					id = len(keys) + 1
					keys[key] = id
				}
				entry := c.identityRelation.get(key)
				simple := []bool{}
				for _, r := range []*Relation{c.identityRelation, c.subtypeRelation, c.strictSubtypeRelation, c.assignableRelation, c.comparableRelation} {
					simple = append(simple, c.isSimpleTypeRelatedTo(s.t, t.t, r, nil))
				}
				relationRows = append(relationRows, []any{nodeIDs[s.node], nodeIDs[t.node], sid, tid2, related, count, id, entry, constrained, simple})
			}
		}
		if assignabilityQueries {
			seen := map[*ast.Symbol]bool{}
			for _, node := range nodes {
				symbol := c.getSymbolOfDeclaration(node)
				if symbol == nil || seen[symbol] {
					continue
				}
				seen[symbol] = true
				if !c.varianceLinks.Has(symbol) {
					continue
				}
				flags := c.varianceLinks.Get(symbol).variances
				if flags == nil {
					continue
				}
				varianceRows = append(varianceRows, []any{nodeIDs[node], flags})
			}
			for _, r := range roots {
				facts := c.getTypeFacts(r.t, TypeFactsAll)
				nonNullable := tid(c.GetNonNullableType(r.t))
				nonUndefined := tid(c.getTypeWithFacts(r.t, TypeFactsNEUndefined))
				nonNull := tid(c.getAdjustedTypeWithFacts(r.t, TypeFactsNENull))
				factRows = append(factRows, []any{nodeIDs[r.node], facts, nonNullable, nonUndefined, nonNull})
			}
		}
		keyRoots := []root{}
		for _, node := range nodes {
			if ast.IsTypeAliasDeclaration(node) {
				name := node.Name().Text()
				if len(name) > 1 && name[0] == 'K' && name[1] >= '0' && name[1] <= '9' {
					keyRoots = append(keyRoots, root{node, c.getNormalizedType(c.getDeclaredTypeOfSymbol(c.getSymbolOfDeclaration(node)), false)})
				}
			}
		}
		for _, s := range keyRoots {
			for _, t := range keyRoots {
				for _, identity := range []bool{false, true} {
					for _, state := range []IntersectionState{IntersectionStateNone, IntersectionStateSource, IntersectionStateTarget} {
						for _, broad := range []bool{false, true} {
							sid, tid2 := tid(s.t), tid(t.t)
							key, constrained := getRelationKey(s.t, t.t, state, identity, broad)
							id, ok := keys[key]
							if !ok {
								id = len(keys) + 1
								keys[key] = id
							}
							relationKeyRows = append(relationKeyRows, []any{nodeIDs[s.node], nodeIDs[t.node], sid, tid2, identity, state, broad, id, constrained})
						}
					}
				}
			}
		}
	}
	if assertionQueries {
		for _, node := range nodes {
			if (node.Kind == ast.KindAsExpression || node.Kind == ast.KindTypeAssertionExpression) && c.assertionLinks.Get(node).exprType != nil {
				c.checkAssertionDeferred(node)
			}
		}
	}
	typeRows := []any{}
	for i := 0; i < len(types); i++ {
		t := types[i]
		if typeNodes && t.objectFlags&ObjectFlagsReference != 0 {
			c.getTypeArguments(t)
		}
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
			if t.objectFlags&(ObjectFlagsClassOrInterface|ObjectFlagsTuple) != 0 {
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
		containsVariables := false
		if awaitedQueries {
			containsVariables = c.couldContainTypeVariables(t)
		}
		targetID, argIDs, paramIDs := tid(target), tids(args), tids(params)
		row := []any{t.flags, t.objectFlags, symbol, targetID, argIDs, paramIDs, outer, tid(this), isThis, tid(constraint), intrinsic}
		if typeNodes {
			shape := map[string]any{}
			if awaitedQueries {
				shape["couldContainTypeVariables"] = containsVariables
			}
			if t.alias != nil {
				args := tids(t.alias.typeArguments)
				if args == nil {
					args = []int{}
				}
				shape["alias"] = []any{sid(t.alias.symbol), args}
			}
			if d := t.AsConstrainedType(); d != nil {
				shape["baseConstraint"] = tid(d.resolvedBaseConstraint)
			}
			switch {
			case t.flags&TypeFlagsFreshable != 0:
				d := t.AsLiteralType()
				shape["fresh"], shape["regular"] = tid(d.freshType), tid(d.regularType)
				switch value := d.value.(type) {
				case string:
					shape["value"] = base64.StdEncoding.EncodeToString([]byte(value))
				case jsnum.Number:
					shape["value"] = fmt.Sprintf("%016x", math.Float64bits(float64(value)))
				case jsnum.PseudoBigInt:
					shape["value"] = value.String()
				default:
					shape["value"] = value
				}
			case t.flags&TypeFlagsUnionOrIntersection != 0:
				shape["parts"] = tids(t.Types())
				if t.flags&TypeFlagsUnion != 0 {
					shape["origin"] = tid(t.AsUnionType().origin)
				}
			case t.flags&TypeFlagsTypeParameter != 0:
				d := t.AsTypeParameter()
				shape["distributed"] = d.isDistributed
				shape["default"] = tid(d.resolvedDefaultType)
				shape["distributedType"] = tid(d.distributedType)
			case t.flags&TypeFlagsIndex != 0:
				shape["target"] = tid(t.AsIndexType().target)
				shape["indexFlags"] = t.AsIndexType().indexFlags
			case t.flags&TypeFlagsIndexedAccess != 0:
				d := t.AsIndexedAccessType()
				shape["object"] = tid(d.objectType)
				shape["index"] = tid(d.indexType)
				shape["accessFlags"] = d.accessFlags
			case t.flags&TypeFlagsTemplateLiteral != 0:
				d := t.AsTemplateLiteralType()
				texts := []string{}
				for _, text := range d.texts {
					texts = append(texts, base64.StdEncoding.EncodeToString([]byte(text)))
				}
				shape["texts"] = texts
				shape["parts"] = tids(d.types)
			case t.flags&TypeFlagsStringMapping != 0:
				shape["target"] = tid(t.AsStringMappingType().target)
			case t.flags&TypeFlagsSubstitution != 0:
				shape["base"] = tid(t.AsSubstitutionType().baseType)
				shape["constraint"] = tid(t.AsSubstitutionType().constraint)
			case t.flags&TypeFlagsConditional != 0:
				d := t.AsConditionalType()
				shape["root"] = []any{nodeIDs[d.root.node.AsNode()], tid(d.root.checkType), tid(d.root.extendsType), d.root.isDistributive, tids(orderedInferences(d.root.outerTypeParameters)), tids(orderedInferences(d.root.inferTypeParameters))}
				shape["check"] = tid(d.checkType)
				shape["extends"] = tid(d.extendsType)
				shape["true"] = tid(d.resolvedTrueType)
				shape["false"] = tid(d.resolvedFalseType)
				shape["inferredTrue"] = tid(d.resolvedInferredTrueType)
				shape["defaultConstraint"] = tid(d.resolvedDefaultConstraint)
				shape["distributiveConstraint"] = tid(d.resolvedConstraintOfDistributive)
			}
			if t.objectFlags&(ObjectFlagsReference|ObjectFlagsClassOrInterface) != 0 {
				shape["node"] = nodeIDs[t.AsTypeReference().node]
			}
			if t.flags&TypeFlagsObject != 0 && t.objectFlags&ObjectFlagsInstantiationExpressionType != 0 {
				shape["node"] = nodeIDs[t.AsInstantiationExpressionType().node]
			}
			if t.objectFlags&ObjectFlagsTuple != 0 {
				d := t.AsTupleType()
				infos := []any{}
				for _, info := range d.elementInfos {
					infos = append(infos, []any{info.flags, nodeIDs[info.labeledDeclaration]})
				}
				shape["tuple"] = []any{infos, d.minLength, d.fixedLength, d.combinedFlags, d.readonly}
			}
			if t.objectFlags&ObjectFlagsMapped != 0 {
				d := t.AsMappedType()
				shape["parameter"] = tid(d.typeParameter)
				shape["constraint"] = tid(d.constraintType)
				shape["template"] = tid(d.templateType)
				shape["name"] = tid(d.nameType)
			}
			if t.objectFlags&ObjectFlagsReverseMapped != 0 {
				d := t.AsReverseMappedType()
				shape["reverse"] = []any{tid(d.source), tid(d.mappedType), tid(d.constraintType)}
			}
			row = append(row, shape)
		}
		typeRows = append(typeRows, row)
	}
	privateReferences := []any{}
	if accessQueries {
		for i, symbol := range symbols {
			declaration := symbol.ValueDeclaration
			if declaration != nil && (ast.HasModifier(declaration, ast.ModifierFlagsPrivate) || declaration.Name() != nil && ast.IsPrivateIdentifier(declaration.Name())) {
				privateReferences = append(privateReferences, []any{i + 1, uint32(c.symbolReferenceLinks.Get(symbol).referenceKinds)})
			}
		}
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
	if typeNodes {
		result["typeQueries"] = typeQueries
	}
	if locations {
		result["locationQueries"] = locationQueries
	}
	if symbolLocations {
		result["symbolLocationQueries"] = symbolLocationQueries
	}
	if scopeServices {
		result["serviceQueries"] = serviceQueries
	}
	if contextQueries {
		result["contextQueries"], result["querySignatureGraph"] = contextRows, contextSignatures
	}
	if declarationVisibility {
		result["visibilityQueries"] = visibilityRows
	}
	if symbolChains {
		result["symbolChainQueries"] = chainRows
	}
	if accessibility {
		result["accessibilityQueries"] = accessibilityRows
	}
	if symbolDisplay {
		result["symbolDisplayQueries"] = symbolDisplayRows
	}
	if typeSyntax || signatureSyntax {
		result["typeSyntaxQueries"] = typeSyntaxRows
	}
	if emitQueries || emitReferences || emitSerialization || emitLinks || emitJsx || emitServices || emitSyntax {
		result["emitQueries"] = emitRows
	}
	if symbolTypeNodes {
		result["symbolTypeNodeQueries"] = symbolTypeNodeRows
	}
	if symbolFormats {
		result["symbolFormatQueries"] = symbolFormatRows
	}
	if documentationSymbols {
		result["documentationSymbolQueries"] = documentationQueries
	}
	if memberQueries {
		result["memberQueries"], result["members"] = memberRoots, memberRows
	}
	if valueQueries {
		result["valueQueries"] = valueRows
	}
	if propertyQueries {
		result["propertyQueries"] = propertyRows
	}
	if signatureQueries {
		result["signatureGraph"] = signatureRows
	}
	if callQueries {
		result["resolvedCalls"] = callRows
	}
	if identityQueries {
		result["relations"] = relationRows
		result["relationKeys"] = relationKeyRows
	}
	if assignabilityQueries {
		result["variances"] = varianceRows
		result["facts"] = factRows
	}
	if indexingQueries {
		result["keyQueries"], result["indexQueries"] = keyRows, indexRows
	}
	if constantQueries {
		result["constantQueries"] = constantRows
	}
	if accessQueries {
		result["accessQueries"] = accessRows
		result["accessSymbols"] = accessSymbols
		result["privateReferences"] = privateReferences
		result["deferredAccessDiagnostics"] = len(c.deferredDiagnosticCallbacks)
		suggestions := []int{}
		for _, d := range c.suggestionDiagnostics.GetDiagnostics() {
			suggestions = append(suggestions, int(d.Code()))
		}
		slices.Sort(suggestions)
		result["accessSuggestions"] = suggestions
	}
	if identifierQueries {
		result["identifierQueries"] = identifierRows
		result["identifierAliasReferences"] = identifierAliases
		hints := []int{}
		for _, diagnostic := range c.diagnostics.GetDiagnostics() {
			for _, related := range diagnostic.RelatedInformation() {
				if related.Code() == 6212 || related.Code() == 6213 {
					hints = append(hints, int(related.Code()))
				}
			}
		}
		slices.Sort(hints)
		result["assignmentHints"] = hints
		suggestions := []int{}
		for _, d := range c.suggestionDiagnostics.GetDiagnostics() {
			suggestions = append(suggestions, int(d.Code()))
		}
		slices.Sort(suggestions)
		result["identifierSuggestions"] = suggestions
	}
	if flowQueries {
		result["flowQueries"] = flowRows
		result["assignmentMarks"] = assignmentRows
		result["flowState"] = []any{len(c.flowLoopCache), len(c.flowLoopStack), len(c.sharedFlows), c.flowAnalysisDisabled, len(c.flowNodeReachable)}
	}
	if referenceQueries {
		result["referenceQueries"] = referenceRows
		result["referenceSyntax"] = referenceSyntax
		result["declarationOrder"] = declarationOrder
		suggestions := []int{}
		for _, d := range c.suggestionDiagnostics.GetDiagnostics() {
			suggestions = append(suggestions, int(d.Code()))
		}
		slices.Sort(suggestions)
		result["referenceSuggestions"] = suggestions
	}
	if awaitedQueries {
		result["awaitedQueries"] = awaitedRows
	}
	if identifierQueries {
		instantiationErrors := []any{}
		for _, diagnostic := range c.diagnostics.GetDiagnostics() {
			if diagnostic.Code() == 2635 {
				instantiationErrors = append(instantiationErrors, diagnostic.MessageArgs()[0])
			}
		}
		result["instantiationErrors"] = instantiationErrors
	}
	if expressionQueries {
		result["expressionQueries"] = expressionRows
		suggestions := []int{}
		for _, d := range c.suggestionDiagnostics.GetDiagnostics() {
			suggestions = append(suggestions, int(d.Code()))
		}
		slices.Sort(suggestions)
		result["suggestions"] = suggestions
	}
	return result
}

func (c *Checker) csharpSymbolDisplayQueries(nodes []*ast.Node, nodeIDs map[*ast.Node]int, sid func(*ast.Symbol) int, formats bool, formatValues []SymbolFormatFlags) []any {
	targets := []*ast.Symbol{}
	seen := map[*ast.Symbol]bool{}
	locations := []*ast.Node{nil}
	for _, node := range nodes {
		file := ast.GetSourceFileOfNode(node)
		if file.FileName() != "/project/globals.d.ts" && ast.IsDeclaration(node) {
			if s := c.getSymbolOfDeclaration(node); s != nil && !seen[s] {
				seen[s] = true
				targets = append(targets, s)
			}
		}
		if formats && slices.ContainsFunc(formatValues, func(f SymbolFormatFlags) bool { return f&5 == 5 }) &&
			(node.Kind == ast.KindPropertyAccessExpression || node.Kind == ast.KindElementAccessExpression) {
			if s := c.GetSymbolAtLocation(node); s != nil && !seen[s] {
				seen[s] = true
				targets = append(targets, s)
			}
		}
		if strings.HasPrefix(file.FileName(), "/project/main.") {
			switch node.Kind {
			case ast.KindSourceFile, ast.KindModuleDeclaration, ast.KindClassDeclaration, ast.KindClassExpression, ast.KindFunctionDeclaration:
				locations = append(locations, node)
			}
		}
	}
	rows := []any{}
	for _, location := range locations {
		for _, target := range targets {
			meanings := []ast.SymbolFlags{ast.SymbolFlagsValue, ast.SymbolFlagsType, ast.SymbolFlagsNamespace}
			if location == nil {
				meanings = []ast.SymbolFlags{ast.SymbolFlagsAll}
			}
			for _, meaning := range meanings {
				if formats {
					values := formatValues
					if values == nil {
						values = []SymbolFormatFlags{0, 1, 2, 6, 8, 10, 12, 14, 16, 17, 32, 34, 36, 38, 40, 42, 44, 46, 64}
					}
					for _, flags := range values {
						rows = append(rows, []any{nodeIDs[location], sid(target), uint32(meaning), uint32(flags), c.symbolToStringEx(target, location, meaning, flags)})
					}
					continue
				}
				rows = append(rows, []any{0, nodeIDs[location], sid(target), uint32(meaning), c.symbolToStringEx(target, location, meaning, SymbolFormatFlagsAllowAnyNodeKind)})
				if location == nil {
					continue
				}
				for _, aliases := range []bool{false, true} {
					for _, modules := range []bool{false, true} {
						result := c.isSymbolAccessibleWorker(target, location, meaning, aliases, modules)
						ids := []int{}
						for _, alias := range result.AliasesToMakeVisible {
							ids = append(ids, nodeIDs[alias])
						}
						slices.Sort(ids)
						rows = append(rows, []any{1, nodeIDs[location], sid(target), uint32(meaning), aliases, modules, int(result.Accessibility), ids, result.ErrorSymbolName, result.ErrorModuleName, nodeIDs[result.ErrorNode]})
					}
				}
			}
		}
	}
	return rows
}

func (c *Checker) csharpSymbolTypeNodes(nodes []*ast.Node, nodeIDs map[*ast.Node]int, sid func(*ast.Symbol) int) []any {
	targets := []*ast.Symbol{}
	seen := map[*ast.Symbol]bool{}
	locations := []*ast.Node{nil}
	for _, node := range nodes {
		file := ast.GetSourceFileOfNode(node)
		if file.FileName() != "/project/globals.d.ts" && ast.IsDeclaration(node) {
			if s := c.getSymbolOfDeclaration(node); s != nil && !seen[s] {
				seen[s] = true
				targets = append(targets, s)
			}
		}
		if strings.HasPrefix(file.FileName(), "/project/main.") {
			switch node.Kind {
			case ast.KindSourceFile, ast.KindModuleDeclaration, ast.KindClassDeclaration, ast.KindClassExpression, ast.KindFunctionDeclaration, ast.KindImportDeclaration, ast.KindExportDeclaration, ast.KindImportType:
				locations = append(locations, node)
			}
		}
	}
	rows := []any{}
	for _, location := range locations {
		for _, target := range targets {
			for _, meaning := range []ast.SymbolFlags{ast.SymbolFlagsValue, ast.SymbolFlagsType, ast.SymbolFlagsNamespace} {
				for mode := 0; mode < 8; mode++ {
					for arguments := 0; arguments < 3; arguments++ {
						b, release := c.getNodeBuilder()
						flags := nodebuilder.FlagsIgnoreErrors
						if mode&1 != 0 {
							flags |= nodebuilder.FlagsUseOnlyExternalAliasing
						}
						if mode&2 != 0 {
							flags |= nodebuilder.FlagsUseAliasDefinedOutsideCurrentScope
						}
						if mode&4 != 0 {
							flags |= nodebuilder.FlagsForbidIndexedAccessSymbolReferences
						}
						b.enterContext(location, flags, nodebuilder.InternalFlagsNone, nil)
						f := b.impl.f
						var args *ast.NodeList
						if arguments == 1 {
							args = f.NewNodeList([]*ast.Node{f.NewKeywordTypeNode(ast.KindNumberKeyword)})
						}
						if arguments == 2 {
							args = f.NewNodeList([]*ast.Node{f.NewArrayTypeNode(f.NewKeywordTypeNode(ast.KindNumberKeyword)), f.NewLiteralTypeNode(f.NewStringLiteral("é", ast.TokenFlagsNone))})
						}
						result := b.exitContext(b.impl.symbolToTypeNode(target, meaning, args))
						text := ""
						if result != nil {
							writer, put := printer.GetSingleLineStringWriter()
							p := printer.NewPrinter(printer.PrinterOptions{RemoveComments: true, OmitTrailingSemicolon: true, NeverAsciiEscape: location != nil && location.Kind == ast.KindSourceFile}, printer.PrintHandlers{}, b.EmitContext())
							p.Write(result, ast.GetSourceFileOfNode(location), writer, nil)
							text = writer.String()
							put()
						}
						release()
						rows = append(rows, []any{nodeIDs[location], sid(target), uint32(meaning), mode, arguments, text})
					}
				}
			}
		}
	}
	return rows
}

func (c *Checker) csharpEmitServices(nodes []*ast.Node, nodeIDs map[*ast.Node]int) []any {
	r := c.GetEmitResolver()
	rows := []any{}
	scalar := func(v any) any {
		switch x := v.(type) {
		case string:
			return []any{"string", base64.StdEncoding.EncodeToString([]byte(x))}
		case jsnum.Number:
			return []any{"number", fmt.Sprintf("%016x", math.Float64bits(float64(x)))}
		}
		return nil
	}
	for _, n := range nodes {
		if !strings.HasPrefix(ast.GetSourceFileOfNode(n).FileName(), "/project/main.") {
			continue
		}
		if ast.IsDeclaration(n) && n.Parent != nil {
			for _, mask := range []ast.ModifierFlags{ast.ModifierFlagsAll, ast.ModifierFlagsExport | ast.ModifierFlagsAmbient, ast.ModifierFlagsPrivate | ast.ModifierFlagsProtected} {
				rows = append(rows, []any{0, nodeIDs[n], uint32(mask), uint32(r.GetEffectiveDeclarationFlags(n, mask))})
			}
		}
		if ast.IsEnumMember(n) {
			v := r.GetEnumMemberValue(n)
			rows = append(rows, []any{1, nodeIDs[n], scalar(v.Value), v.IsSyntacticallyString, v.ResolvedOtherFiles, v.HasExternalReferences})
		}
		if ast.IsEnumMember(n) || ast.IsPropertyAccessExpression(n) || ast.IsElementAccessExpression(n) {
			rows = append(rows, []any{2, nodeIDs[n], scalar(r.GetConstantValue(n))})
		}
		if ast.IsPropertyDeclaration(n) || ast.IsBinaryExpression(n) && ast.IsInJSFile(n) {
			rows = append(rows, []any{3, nodeIDs[n], r.IsThisPropertyAssignmentDeclarationRedundant(n)})
		}
	}
	return rows
}

func (c *Checker) csharpEmitJsx(nodes []*ast.Node, nodeIDs map[*ast.Node]int) []any {
	locations := []*ast.Node{nil}
	rows := []any{}
	r := c.GetEmitResolver()
	for _, n := range nodes {
		if strings.HasPrefix(ast.GetSourceFileOfNode(n).FileName(), "/project/main.") && (n.Kind == ast.KindSourceFile || n.Kind == ast.KindJsxOpeningFragment || ast.IsJsxOpeningLikeElement(n)) {
			locations = append(locations, n)
		}
	}
	locations = append(locations, nil)
	for _, location := range locations {
		for _, fragment := range []bool{false, true} {
			var result *ast.Node
			if fragment {
				result = r.GetJsxFragmentFactoryEntity(location)
			} else {
				result = r.GetJsxFactoryEntity(location)
			}
			var value any
			if result != nil {
				writer, put := printer.GetSingleLineStringWriter()
				p := printer.NewPrinter(printer.PrinterOptions{RemoveComments: true}, printer.PrintHandlers{}, printer.NewEmitContext())
				p.Write(result, ast.GetSourceFileOfNode(location), writer, nil)
				value = writer.String()
				put()
			}
			rows = append(rows, []any{nodeIDs[location], fragment, value})
		}
	}
	return rows
}

func (c *Checker) csharpEmitLinks(nodes []*ast.Node, nodeIDs map[*ast.Node]int) []any {
	rows := []any{}
	r := c.GetEmitResolver()
	for pass := 0; pass < 3; pass++ {
		if pass > 0 {
			for _, file := range c.files {
				if strings.HasPrefix(file.FileName(), "/project/main.") {
					r.MarkLinkedReferencesRecursively(file)
				}
			}
		}
		for _, node := range nodes {
			if strings.HasPrefix(ast.GetSourceFileOfNode(node).FileName(), "/project/main.") && ast.IsAliasSymbolDeclaration(node) {
				rows = append(rows, []any{pass, nodeIDs[node], r.IsReferencedAliasDeclaration(node)})
			}
		}
	}
	return rows
}

func (c *Checker) csharpEmitSerialization(nodes []*ast.Node, nodeIDs map[*ast.Node]int) []any {
	rows := []any{}
	locations := []*ast.Node{nil}
	for _, n := range nodes {
		if strings.HasPrefix(ast.GetSourceFileOfNode(n).FileName(), "/project/main.") && (n.Kind == ast.KindSourceFile || n.Kind == ast.KindClassDeclaration || n.Kind == ast.KindFunctionDeclaration) {
			locations = append(locations, n)
		}
	}
	for _, n := range nodes {
		if strings.HasPrefix(ast.GetSourceFileOfNode(n).FileName(), "/project/main.") && n.Kind == ast.KindTypeReference {
			for _, location := range locations {
				rows = append(rows, []any{nodeIDs[n], nodeIDs[location], int(c.GetEmitResolver().GetTypeReferenceSerializationKind(n.AsTypeReferenceNode().TypeName, location))})
			}
		}
	}
	return rows
}

func (c *Checker) csharpEmitReferences(nodes []*ast.Node, nodeIDs map[*ast.Node]int, sid func(*ast.Symbol) int) []any {
	r := c.GetEmitResolver()
	rows := []any{}
	for _, n := range nodes {
		if !strings.HasPrefix(ast.GetSourceFileOfNode(n).FileName(), "/project/main.") {
			continue
		}
		if n.Kind == ast.KindIdentifier {
			values := []int{}
			for _, d := range r.GetReferencedValueDeclarations(n) {
				values = append(values, nodeIDs[d])
			}
			rows = append(rows, []any{0, nodeIDs[n], nodeIDs[r.GetReferencedExportContainer(n, false)], nodeIDs[r.GetReferencedExportContainer(n, true)], nodeIDs[r.GetReferencedImportDeclaration(n)], nodeIDs[r.GetReferencedValueDeclaration(n)], values})
		}
		if n.Kind == ast.KindPropertyAccessExpression || n.Kind == ast.KindElementAccessExpression || n.Kind == ast.KindQualifiedName {
			before := r.GetReferencedMemberValueDeclaration(n)
			c.GetSymbolAtLocation(n)
			after := r.GetReferencedMemberValueDeclaration(n)
			rows = append(rows, []any{1, nodeIDs[n], nodeIDs[before], nodeIDs[after]})
		}
		if n.Kind == ast.KindElementAccessExpression {
			rows = append(rows, []any{2, nodeIDs[n], r.GetElementAccessExpressionName(n.AsElementAccessExpression())})
		}
		if ast.IsFunctionLikeDeclaration(n) {
			props := []int{}
			for _, p := range r.GetPropertiesOfContainerFunction(n) {
				props = append(props, sid(p))
			}
			rows = append(rows, []any{3, nodeIDs[n], props, r.IsExpandoFunctionDeclaration(n)})
		}
	}
	return rows
}

func (c *Checker) csharpEmitQueries(nodes []*ast.Node, nodeIDs map[*ast.Node]int) []any {
	r := c.GetEmitResolver()
	rows := []any{}
	locations := []*ast.Node{nil}
	for _, n := range nodes {
		if strings.HasPrefix(ast.GetSourceFileOfNode(n).FileName(), "/project/main.") && (n.Kind == ast.KindSourceFile || n.Kind == ast.KindFunctionDeclaration || n.Kind == ast.KindClassDeclaration) {
			locations = append(locations, n)
		}
	}
	for _, n := range nodes {
		if !strings.HasPrefix(ast.GetSourceFileOfNode(n).FileName(), "/project/main.") {
			continue
		}
		if ast.IsDeclaration(n) || ast.IsBinaryExpression(n) && ast.IsInJSFile(n) {
			rows = append(rows, []any{0, nodeIDs[n], r.IsLateBound(n), r.IsLiteralConstDeclaration(n), r.IsReferencedAliasDeclaration(n), r.IsValueAliasDeclaration(n), r.IsTopLevelValueImportEqualsWithEntityName(n)})
		}
		if ast.IsFunctionLike(n) {
			rows = append(rows, []any{1, nodeIDs[n], r.IsImplementationOfOverload(n)})
		}
		if n.Kind == ast.KindParameter {
			rows = append(rows, []any{2, nodeIDs[n], r.IsOptionalParameter(n)})
		}
		if n.Kind == ast.KindPropertyAccessExpression {
			rows = append(rows, []any{3, nodeIDs[n], r.IsDefinitelyReferenceToGlobalSymbolObject(n)})
		}
		if (n.Kind == ast.KindImportDeclaration || n.Kind == ast.KindExportDeclaration || n.Kind == ast.KindImportEqualsDeclaration || n.Kind == ast.KindImportType || n.Kind == ast.KindModuleDeclaration) && ast.GetExternalModuleName(n) != nil {
			file := r.GetExternalModuleFileFromDeclaration(n)
			name := ""
			if file != nil {
				name = file.FileName()
			}
			required := false
			if n.Kind == ast.KindImportDeclaration {
				required = r.IsImportRequiredByAugmentation(n.AsImportDeclaration())
			}
			rows = append(rows, []any{4, nodeIDs[n], name, required})
		}
		if n.Kind == ast.KindParameter || n.Kind == ast.KindPropertyDeclaration || n.Kind == ast.KindPropertySignature {
			for _, location := range locations {
				rows = append(rows, []any{5, nodeIDs[n], nodeIDs[location], r.RequiresAddingImplicitUndefined(n, nil, location)})
			}
		}
		if n.Kind == ast.KindSourceFile || n.Kind == ast.KindFunctionDeclaration || n.Kind == ast.KindClassDeclaration {
			for _, name := range []string{"Symbol", "globalThis", "value", "T", "Missing"} {
				rows = append(rows, []any{6, nodeIDs[n], name, r.IsNameResolvable(n, name)})
			}
		}
	}
	return rows
}

func (c *Checker) csharpSignatureSyntax(nodes []*ast.Node, nodeIDs map[*ast.Node]int) []any {
	locations := []*ast.Node{nil}
	targets := []*ast.Node{}
	for _, node := range nodes {
		if !strings.HasPrefix(ast.GetSourceFileOfNode(node).FileName(), "/project/main.") {
			continue
		}
		if node.Kind == ast.KindSourceFile || node.Kind == ast.KindClassDeclaration || node.Kind == ast.KindFunctionDeclaration {
			locations = append(locations, node)
		}
		if ast.IsFunctionLike(node) && ((node.Name() != nil && strings.HasPrefix(node.Name().Text(), "serialize")) || (node.Parent != nil && node.Parent.Kind == ast.KindClassDeclaration && strings.HasPrefix(node.Parent.Name().Text(), "Serialize"))) {
			targets = append(targets, node)
		}
	}
	kinds := []ast.Kind{ast.KindCallSignature, ast.KindConstructSignature, ast.KindFunctionType, ast.KindConstructorType, ast.KindMethodSignature, ast.KindMethodDeclaration, ast.KindConstructor, ast.KindGetAccessor, ast.KindSetAccessor, ast.KindIndexSignature, ast.KindFunctionDeclaration, ast.KindFunctionExpression, ast.KindArrowFunction}
	flags := []nodebuilder.Flags{nodebuilder.FlagsNoTruncation, nodebuilder.FlagsNoTruncation | nodebuilder.FlagsOmitParameterModifiers, nodebuilder.FlagsNoTruncation | nodebuilder.FlagsSuppressAnyReturnType, nodebuilder.FlagsNoTruncation | nodebuilder.FlagsOmitThisParameter, nodebuilder.FlagsNoTruncation | nodebuilder.FlagsGenerateNamesForShadowedTypeParams}
	rows := []any{}
	for _, target := range targets {
		signature := c.getSignatureFromDeclaration(target)
		for _, location := range locations {
			for _, kind := range kinds {
				for _, flag := range flags {
					b, release := c.getNodeBuilder()
					node := b.SignatureToSignatureDeclaration(signature, kind, location, flag|nodebuilder.FlagsIgnoreErrors, nodebuilder.InternalFlagsNone, nil)
					value := ""
					if node != nil {
						writer, put := printer.GetSingleLineStringWriter()
						p := printer.NewPrinter(printer.PrinterOptions{RemoveComments: true, OmitTrailingSemicolon: true, NeverAsciiEscape: location != nil && location.Kind == ast.KindSourceFile}, printer.PrintHandlers{}, b.EmitContext())
						p.Write(node, ast.GetSourceFileOfNode(location), writer, nil)
						value = writer.String()
						put()
					}
					release()
					rows = append(rows, []any{nodeIDs[target], nodeIDs[location], int(kind), uint32(flag), value})
				}
			}
		}
	}
	return rows
}

func (c *Checker) csharpTypeSyntax(nodes []*ast.Node, nodeIDs map[*ast.Node]int, configuredFlags []nodebuilder.Flags) []any {
	locations := []*ast.Node{nil}
	targets := []*ast.Node{}
	for _, node := range nodes {
		file := ast.GetSourceFileOfNode(node)
		if !strings.HasPrefix(file.FileName(), "/project/main.") {
			continue
		}
		if ast.IsTypeAliasDeclaration(node) && strings.HasPrefix(node.Name().Text(), "Serialize") {
			targets = append(targets, node.Type())
		}
		if node.Kind == ast.KindSourceFile || node.Kind == ast.KindClassDeclaration || node.Kind == ast.KindFunctionDeclaration {
			locations = append(locations, node)
		}
	}
	rows := []any{}
	for _, target := range targets {
		t := c.getTypeFromTypeNode(target)
		for _, location := range locations {
			for _, expand := range []bool{false, true} {
				for _, outside := range []bool{false, true} {
					values := configuredFlags
					if values == nil {
						values = []nodebuilder.Flags{nodebuilder.FlagsNoTruncation}
					}
					for _, configured := range values {
						b, release := c.getNodeBuilder()
						flags := nodebuilder.FlagsIgnoreErrors | configured
						if expand {
							flags |= nodebuilder.FlagsInTypeAlias
						}
						if outside {
							flags |= nodebuilder.FlagsUseAliasDefinedOutsideCurrentScope
						}
						node := b.TypeToTypeNode(t, location, flags, nodebuilder.InternalFlagsNone, nil)
						value := ""
						if node != nil {
							writer, put := printer.GetSingleLineStringWriter()
							p := printer.NewPrinter(printer.PrinterOptions{RemoveComments: true, OmitTrailingSemicolon: true, NeverAsciiEscape: location != nil && location.Kind == ast.KindSourceFile}, printer.PrintHandlers{}, b.EmitContext())
							p.Write(node, ast.GetSourceFileOfNode(location), writer, nil)
							value = writer.String()
							put()
						}
						release()
						row := []any{nodeIDs[target], nodeIDs[location], expand, outside}
						if configuredFlags != nil {
							row = append(row, uint32(configured))
						}
						rows = append(rows, append(row, value))
					}
				}
			}
		}
	}
	return rows
}

func (c *Checker) csharpEmitSyntax(nodes []*ast.Node, nodeIDs map[*ast.Node]int) []any {
	targets := []*ast.Node{}
	locations := []*ast.Node{nil}
	for _, n := range nodes {
		if !strings.HasPrefix(ast.GetSourceFileOfNode(n).FileName(), "/project/main.") {
			continue
		}
		targets = append(targets, n)
		if n.Kind == ast.KindSourceFile || (n.Kind == ast.KindClassDeclaration || n.Kind == ast.KindFunctionDeclaration) && n.Name() != nil && n.Name().Text() == "Scope" {
			locations = append(locations, n)
		}
	}
	flags := []nodebuilder.Flags{nodebuilder.FlagsNoTruncation, nodebuilder.FlagsNoTruncation | nodebuilder.FlagsGenerateNamesForShadowedTypeParams, nodebuilder.FlagsNoTruncation | nodebuilder.FlagsSuppressAnyReturnType, nodebuilder.FlagsNoTruncation | nodebuilder.FlagsUseSingleQuotesForStringLiteralType}
	rows := []any{}
	r := c.GetEmitResolver()
	emit := func(op int, n *ast.Node, location *ast.Node, flag nodebuilder.Flags) {
		e := printer.NewEmitContext()
		print := func(node *ast.Node) string {
			if node == nil {
				return ""
			}
			writer, put := printer.GetSingleLineStringWriter()
			defer put()
			p := printer.NewPrinter(printer.PrinterOptions{RemoveComments: true, OmitTrailingSemicolon: true, NeverAsciiEscape: location != nil && location.Kind == ast.KindSourceFile}, printer.PrintHandlers{}, e)
			p.Write(node, ast.GetSourceFileOfNode(location), writer, nil)
			return writer.String()
		}
		f := flag | nodebuilder.FlagsIgnoreErrors
		var value any
		switch op {
		case 0:
			value = print(r.CreateTypeOfDeclaration(e, n, location, f, nodebuilder.InternalFlagsNone, nil))
		case 1:
			value = print(r.CreateTypeOfExpression(e, n, location, f, nodebuilder.InternalFlagsNone, nil))
		case 2:
			value = print(r.CreateReturnTypeOfSignatureDeclaration(e, n, location, f, nodebuilder.InternalFlagsNone, nil))
		case 3:
			values := []string{}
			for _, parameter := range r.CreateTypeParametersOfSignatureDeclaration(e, n, location, f, nodebuilder.InternalFlagsNone, nil) {
				values = append(values, print(parameter))
			}
			value = values
		case 4:
			if literal := r.CreateLiteralConstValue(e, n, nil); literal != nil {
				value = print(literal)
			}
		}
		rows = append(rows, []any{op, nodeIDs[n], nodeIDs[location], uint32(flag), value})
	}
	for _, n := range targets {
		if ast.IsVariableDeclaration(n) || ast.IsParameterDeclaration(n) || ast.IsPropertyDeclaration(n) || ast.IsPropertySignatureDeclaration(n) || ast.IsAccessor(n) {
			for _, location := range locations {
				for _, flag := range flags {
					emit(0, n, location, flag)
				}
			}
		}
		if ast.HasInitializer(n) && n.Initializer() != nil {
			for _, location := range locations {
				for _, flag := range flags {
					emit(1, n.Initializer(), location, flag)
				}
			}
		}
		if ast.IsFunctionLike(n) {
			for _, location := range locations {
				for _, flag := range flags {
					emit(2, n, location, flag)
					emit(3, n, location, flag)
				}
			}
		}
		if ast.IsVariableDeclaration(n) || ast.IsPropertyDeclaration(n) {
			emit(4, n, nil, 0)
		}
	}
	return rows
}
