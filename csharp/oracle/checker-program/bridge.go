// Development-only instrumentation, copied into the frozen reference checkout.
package checker

import (
	"encoding/base64"
	"fmt"
	"math"
	"slices"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/jsnum"
)

func (c *Checker) CSharpProgramScopeProbe(aliasQueries bool, typeNodes bool, memberQueries bool, valueQueries bool, propertyQueries bool) any {
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
	memberRoots, memberRows := []any{}, []any{}
	valueRows := []any{}
	if memberQueries {
		pending := []*Type{}
		seen := map[*Type]bool{}
		mtid := func(t *Type) int {
			id := tid(t)
			if t != nil && t.flags&TypeFlagsObject != 0 && !seen[t] {
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
		signatureRow := func(s *Signature) any {
			generic := []int{}
			for _, p := range s.typeParameters {
				generic = append(generic, mtid(p))
			}
			var receiver any
			if s.thisParameter != nil {
				receiver = []any{sid(s.thisParameter), mtid(c.getTypeOfSymbol(s.thisParameter))}
			}
			parameters := []any{}
			for _, p := range s.parameters {
				parameters = append(parameters, []any{sid(p), mtid(c.getTypeOfSymbol(p))})
			}
			result := mtid(c.getReturnTypeOfSignature(s))
			var predicate any
			if p := c.getTypePredicateOfSignature(s); p != nil {
				predicate = []any{p.kind, p.parameterIndex, p.parameterName, mtid(p.t)}
			}
			return []any{s.flags, nodeIDs[s.declaration], s.minArgumentCount, generic, receiver, parameters, result, predicate}
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
		targetID, argIDs, paramIDs := tid(target), tids(args), tids(params)
		row := []any{t.flags, t.objectFlags, symbol, targetID, argIDs, paramIDs, outer, tid(this), isThis, tid(constraint), intrinsic}
		if typeNodes {
			shape := map[string]any{}
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
			row = append(row, shape)
		}
		typeRows = append(typeRows, row)
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
	return result
}
