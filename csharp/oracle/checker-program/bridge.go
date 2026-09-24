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
)

func (c *Checker) CSharpProgramScopeProbe(aliasQueries bool, typeNodes bool, memberQueries bool, valueQueries bool, propertyQueries bool, signatureQueries bool, identityQueries bool, assignabilityQueries bool, indexingQueries bool, constantQueries bool, expressionQueries bool, awaitedQueries bool, referenceQueries bool, flowQueries bool, identifierQueries bool, accessQueries bool, callQueries bool) any {
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
