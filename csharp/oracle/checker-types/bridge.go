// Development-only instrumentation, copied into the frozen reference checkout.
package checker

import (
	"encoding/base64"
	"fmt"
	"math"
	"strconv"
	"strings"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/jsnum"
)

type CSharpTypeStep struct {
	Op, Text, Symbol, Member string
	Args, AliasArgs          []int
	Flags                    uint32
	Origin                   int
	This                     bool
	Texts                    []string
	Properties               []string
}

type CSharpMapperStep struct {
	Op                      string
	Sources, Targets, Parts []int
}
type CSharpMapperQuery struct {
	Mapper, Type int
	Normalize    bool
}

type CSharpResolutionOperation struct {
	Op                      string
	Target, Property, Value int
}

func (c *Checker) CSharpResolutionProbe(operations []CSharpResolutionOperation) any {
	entities := map[string]any{}
	factory := ast.NewNodeFactory(ast.NodeFactoryHooks{})
	entity := func(id int, property TypeSystemPropertyName) any {
		group := "type"
		switch property {
		case TypeSystemPropertyNameType, TypeSystemPropertyNameDeclaredType, TypeSystemPropertyNameWriteType, TypeSystemPropertyNameAliasTarget:
			group = "symbol"
		case TypeSystemPropertyNameResolvedReturnType:
			group = "signature"
		case TypeSystemPropertyNameInitializerIsUndefined:
			group = "node"
		}
		key := fmt.Sprint(group, id)
		if target := entities[key]; target != nil {
			return target
		}
		var target any
		switch group {
		case "symbol":
			target = c.newSymbol(ast.SymbolFlagsProperty, key)
		case "node":
			target = factory.NewIdentifier(key)
		case "signature":
			target = c.newSignature(0, nil, nil, nil, nil, nil, nil, 0)
		default:
			target = c.newObjectType(ObjectFlagsInterface|ObjectFlagsReference, nil)
		}
		entities[key] = target
		return target
	}
	rows := []any{}
	for _, op := range operations {
		property := TypeSystemPropertyName(op.Property)
		target := entity(op.Target, property)
		value := 0
		switch op.Op {
		case "push":
			if c.pushTypeResolution(target, property) {
				value = 1
			}
		case "pop":
			if c.popTypeResolution() {
				value = 1
			}
		case "find":
			value = c.findResolutionCycleStartIndex(target, property)
		case "start":
			c.resolutionStart = op.Value
			value = c.resolutionStart
		case "set":
			var t *Type
			if op.Value != 0 {
				t = c.stringType
			}
			switch property {
			case TypeSystemPropertyNameType:
				c.valueSymbolLinks.Get(target.(*ast.Symbol)).resolvedType = t
			case TypeSystemPropertyNameDeclaredType:
				c.typeAliasLinks.Get(target.(*ast.Symbol)).declaredType = t
			case TypeSystemPropertyNameWriteType:
				c.valueSymbolLinks.Get(target.(*ast.Symbol)).writeType = t
			case TypeSystemPropertyNameAliasTarget:
				var symbol *ast.Symbol
				if t != nil {
					symbol = c.unknownSymbol
				}
				c.aliasSymbolLinks.Get(target.(*ast.Symbol)).aliasTarget = symbol
			case TypeSystemPropertyNameResolvedTypeArguments:
				var args []*Type
				if t != nil {
					args = []*Type{}
				}
				target.(*Type).AsTypeReference().resolvedTypeArguments = args
			case TypeSystemPropertyNameResolvedBaseTypes:
				target.(*Type).AsInterfaceType().baseTypesResolved = t != nil
			case TypeSystemPropertyNameResolvedBaseConstructorType:
				target.(*Type).AsInterfaceType().resolvedBaseConstructorType = t
			case TypeSystemPropertyNameResolvedReturnType:
				target.(*Signature).resolvedReturnType = t
			case TypeSystemPropertyNameResolvedBaseConstraint:
				target.(*Type).AsConstrainedType().resolvedBaseConstraint = t
			case TypeSystemPropertyNameInitializerIsUndefined:
				flags := NodeCheckFlagsNone
				if t != nil {
					flags = NodeCheckFlagsInitializerIsUndefinedComputed
				}
				c.nodeLinks.Get(target.(*ast.Node)).flags = flags
			}
			if c.typeResolutionHasProperty(&TypeResolution{target: target, propertyName: property}) {
				value = 1
			}
		default:
			panic(op.Op)
		}
		rows = append(rows, []int{value, len(c.typeResolutions), c.resolutionStart})
	}
	return rows
}

func (c *Checker) CSharpTypeProbe(steps []CSharpTypeStep, mapperSteps []CSharpMapperStep, queries []CSharpMapperQuery, comparisons [][]int) any {
	algebra := false
	for _, step := range steps {
		if step.Op == "unionReduced" || step.Op == "intersection" || step.Op == "templateNormalized" || step.Op == "caseMap" || step.Op == "regularAll" {
			algebra = true
		}
	}
	if algebra {
		c.diagnostics = ast.DiagnosticsCollection{}
		c.currentNode = c.files[0].AsNode()
	}
	builtins := map[string]*Type{
		"any": c.anyType, "auto": c.autoType, "wildcard": c.wildcardType, "blockedString": c.blockedStringType,
		"error": c.errorType, "unresolved": c.unresolvedType, "nonInferrableAny": c.nonInferrableAnyType, "intrinsic": c.intrinsicMarkerType,
		"unknown": c.unknownType, "undefined": c.undefinedType, "undefinedWidening": c.undefinedWideningType,
		"missing": c.missingType, "undefinedOrMissing": c.undefinedOrMissingType, "optional": c.optionalType,
		"null": c.nullType, "nullWidening": c.nullWideningType, "string": c.stringType, "number": c.numberType,
		"bigint": c.bigintType, "false": c.falseType, "true": c.trueType, "regularFalse": c.regularFalseType,
		"regularTrue": c.regularTrueType, "boolean": c.booleanType, "symbol": c.esSymbolType, "void": c.voidType,
		"never": c.neverType, "silentNever": c.silentNeverType, "implicitNever": c.implicitNeverType,
		"unreachableNever": c.unreachableNeverType, "object": c.nonPrimitiveType, "uniqueLiteral": c.uniqueLiteralType,
		"empty": c.emptyObjectType, "unknownEmpty": c.unknownEmptyObjectType, "anyFunction": c.anyFunctionType,
		"unknownUnion": c.unknownUnionType, "numericString": c.numericStringType, "templateConstraint": c.templateConstraintType,
	}
	syms := map[string]*ast.Symbol{}
	names := map[*ast.Symbol]string{}
	symbol := func(name string) *ast.Symbol {
		if name == "" {
			return nil
		}
		if s := syms[name]; s != nil {
			return s
		}
		stored := name
		if rest, internal := strings.CutPrefix(name, "@internal:"); internal {
			stored = ast.InternalSymbolNamePrefix + rest
		}
		s := c.newSymbol(ast.SymbolFlagsTypeAlias, stored)
		syms[name] = s
		names[s] = name
		return s
	}
	values := []*Type{nil}
	selectTypes := func(indices []int) []*Type {
		if indices == nil {
			return nil
		}
		r := make([]*Type, len(indices))
		for i, index := range indices {
			r[i] = values[index]
		}
		return r
	}
	for _, step := range steps {
		args := selectTypes(step.Args)
		var t *Type
		switch step.Op {
		case "builtin":
			t = builtins[step.Text]
		case "string":
			b, e := base64.StdEncoding.DecodeString(step.Text)
			if e != nil {
				panic(e)
			}
			t = c.getStringLiteralType(string(b))
		case "number", "enumNumber":
			bits, e := strconv.ParseUint(step.Text, 16, 64)
			if e != nil {
				panic(e)
			}
			v := jsnum.Number(math.Float64frombits(bits))
			if step.Op == "number" {
				t = c.getNumberLiteralType(v)
			} else {
				t = c.getEnumLiteralType(v, symbol(step.Symbol), symbol(step.Member))
			}
		case "bigint":
			t = c.parseBigIntLiteralType(step.Text)
		case "enumString":
			t = c.getEnumLiteralType(step.Text, symbol(step.Symbol), symbol(step.Member))
		case "computedEnum":
			t = c.newLiteralType(TypeFlagsEnum, nil, nil)
			t.symbol = symbol(step.Symbol)
		case "errorAlias":
			t = c.newIntrinsicType(TypeFlagsAny, "error")
			t.alias = &TypeAlias{symbol: symbol(step.Symbol), typeArguments: selectTypes(step.AliasArgs)}
		case "fresh":
			t = c.getFreshTypeOfLiteralType(args[0])
		case "regular":
			t = args[0].AsLiteralType().regularType
		case "parameter":
			t = c.newTypeParameter(symbol(step.Symbol))
			t.AsTypeParameter().isThisType = step.This
			if len(args) != 0 {
				t.AsTypeParameter().constraint = args[0]
			}
		case "distributed":
			t = c.newTypeParameter(nil)
			t.AsTypeParameter().isDistributed = true
			t.AsTypeParameter().constraint = args[0]
		case "object":
			t = c.newObjectType(ObjectFlags(step.Flags), symbol(step.Symbol))
		case "shape":
			members := ast.SymbolTable{}
			for i, name := range step.Properties {
				members[name] = c.newProperty(name, args[i])
			}
			t = c.newAnonymousType(symbol(step.Symbol), members, nil, nil, nil)
		case "reference":
			target := args[0]
			if target.AsInterfaceType().instantiations == nil {
				target.AsInterfaceType().instantiations = make(map[CacheHashKey]*Type)
			}
			t = c.createTypeReferenceEx(target, args[1:], ObjectFlags(step.Flags))
		case "clone":
			t = c.cloneTypeReference(args[0])
		case "union", "unionReduced", "intersection":
			var alias *TypeAlias
			if step.Symbol != "" {
				alias = &TypeAlias{symbol: symbol(step.Symbol), typeArguments: selectTypes(step.AliasArgs)}
			}
			if step.Op == "union" {
				t = c.getUnionTypeFromSortedList(args, ObjectFlags(step.Flags), alias, values[step.Origin])
			} else if step.Op == "unionReduced" {
				t = c.getUnionTypeEx(args, UnionReduction(step.Flags), alias, values[step.Origin])
			} else {
				t = c.getIntersectionTypeEx(args, IntersectionFlags(step.Flags), alias)
			}
		case "rawUnion":
			t = c.newUnionType(ObjectFlags(step.Flags), args)
		case "rawIntersection":
			t = c.newIntersectionType(ObjectFlags(step.Flags), args)
		case "index":
			t = c.getIndexTypeForGenericType(args[0], IndexFlags(step.Flags))
		case "indexed":
			t = c.newIndexedAccessType(args[0], args[1], AccessFlags(step.Flags))
		case "substitution":
			t = c.getSubstitutionType(args[0], args[1])
		case "template":
			texts := make([]string, len(args)+1)
			for i := range texts {
				texts[i] = step.Text
			}
			t = c.newTemplateLiteralType(texts, args)
		case "stringMapping":
			t = c.newStringMappingType(symbol(step.Symbol), args[0])
		case "templateNormalized":
			texts := make([]string, len(step.Texts))
			for i, s := range step.Texts {
				b, err := base64.StdEncoding.DecodeString(s)
				if err != nil {
					panic(err)
				}
				texts[i] = string(b)
			}
			t = c.getTemplateLiteralType(texts, args)
		case "caseMap":
			t = c.getStringMappingType(symbol(step.Symbol), args[0])
		case "regularAll":
			t = c.getRegularTypeOfLiteralType(args[0])
		case "filter":
			t = c.filterType(args[0], func(t *Type) bool { return t.flags&TypeFlags(step.Flags) == 0 })
		default:
			panic(step.Op)
		}
		if t == nil {
			panic("nil type")
		}
		values = append(values, t)
	}
	mappers := []*TypeMapper{nil}
	mapperRows := []any{}
	calls := 0
	for _, step := range mapperSteps {
		sources, targets := selectTypes(step.Sources), selectTypes(step.Targets)
		var mapper *TypeMapper
		switch step.Op {
		case "direct":
			mapper = newTypeMapper(sources, targets)
		case "single":
			mapper = newArrayToSingleTypeMapper(sources, targets[0])
		case "deferred":
			callbacks := make([]func() *Type, len(targets))
			for i := range targets {
				callbacks[i] = func() *Type { calls++; return targets[i] }
			}
			mapper = newDeferredTypeMapper(sources, callbacks)
		case "function":
			mapper = newFunctionTypeMapper(func(t *Type) *Type {
				calls++
				for i, s := range sources {
					if t == s {
						return targets[i]
					}
				}
				return t
			})
		case "merged":
			mapper = mergeTypeMappers(mappers[step.Parts[0]], mappers[step.Parts[1]])
		case "composite":
			mapper = c.combineTypeMappers(mappers[step.Parts[0]], mappers[step.Parts[1]])
		case "prepend":
			mapper = prependTypeMapping(sources[0], targets[0], mappers[step.Parts[0]])
		case "append":
			mapper = appendTypeMapping(mappers[step.Parts[0]], sources[0], targets[0])
		default:
			panic(step.Op)
		}
		mappers = append(mappers, mapper)
		mapperRows = append(mapperRows, []any{mapper.Kind(), mapper.MapsThisOnly()})
	}
	queryTypes := []*Type{}
	for _, query := range queries {
		if query.Normalize {
			queryTypes = append(queryTypes, getMappedType(values[query.Type], mappers[query.Mapper]))
		} else {
			queryTypes = append(queryTypes, mappers[query.Mapper].Map(values[query.Type]))
		}
	}
	ids := map[*Type]int{nil: 0}
	queue := []*Type{}
	var ref func(*Type) int
	ref = func(t *Type) int {
		if id, ok := ids[t]; ok {
			return id
		}
		id := len(queue) + 1
		ids[t] = id
		queue = append(queue, t)
		return id
	}
	refs := func(types []*Type) []int {
		if types == nil {
			return nil
		}
		result := make([]int, len(types))
		for i, t := range types {
			result[i] = ref(t)
		}
		return result
	}
	results := refs(values[1:])
	rows := []any{}
	for i := 0; i < len(queue); i++ {
		t := queue[i]
		row := map[string]any{"flags": t.flags, "objectFlags": t.objectFlags, "symbol": names[t.symbol], "literal": isLiteralType(t), "unit": isUnitType(t)}
		if t.alias != nil {
			arguments := refs(t.alias.typeArguments)
			// Alias arguments are not a lazy-resolution slot; nil and an empty slice both mean arity zero.
			if arguments == nil {
				arguments = []int{}
			}
			row["alias"] = []any{names[t.alias.symbol], arguments}
		}
		switch {
		case t.flags&TypeFlagsIntrinsic != 0:
			row["name"] = t.AsIntrinsicType().intrinsicName
		case t.flags&TypeFlagsFreshable != 0:
			d := t.AsLiteralType()
			row["fresh"] = ref(d.freshType)
			row["regular"] = ref(d.regularType)
			row["isFresh"] = isFreshLiteralType(t)
			switch v := d.value.(type) {
			case string:
				row["value"] = base64.StdEncoding.EncodeToString([]byte(v))
			case jsnum.Number:
				row["value"] = fmt.Sprintf("%016x", math.Float64bits(float64(v)))
			case jsnum.PseudoBigInt:
				row["value"] = v.String()
			default:
				row["value"] = v
			}
		case t.flags&TypeFlagsUnionOrIntersection != 0:
			row["types"] = refs(t.Types())
			if t.flags&TypeFlagsUnion != 0 {
				row["origin"] = ref(t.AsUnionType().origin)
			}
		case t.flags&TypeFlagsTypeParameter != 0:
			row["this"] = t.AsTypeParameter().isThisType
		case t.objectFlags&ObjectFlagsReference != 0:
			row["target"] = ref(t.AsTypeReference().target)
			row["arguments"] = refs(t.AsTypeReference().resolvedTypeArguments)
		case t.flags&TypeFlagsIndex != 0:
			row["target"] = ref(t.AsIndexType().target)
			row["indexFlags"] = t.AsIndexType().indexFlags
		case t.flags&TypeFlagsIndexedAccess != 0:
			row["object"] = ref(t.AsIndexedAccessType().objectType)
			row["index"] = ref(t.AsIndexedAccessType().indexType)
			row["accessFlags"] = t.AsIndexedAccessType().accessFlags
		case t.flags&TypeFlagsSubstitution != 0:
			row["base"] = ref(t.AsSubstitutionType().baseType)
			row["constraint"] = ref(t.AsSubstitutionType().constraint)
		case t.flags&TypeFlagsTemplateLiteral != 0:
			row["texts"] = t.AsTemplateLiteralType().texts
			row["types"] = refs(t.AsTemplateLiteralType().types)
		case t.flags&TypeFlagsStringMapping != 0:
			row["target"] = ref(t.AsStringMappingType().target)
		}
		rows = append(rows, row)
	}
	// Query results must be among the input atoms for this mapper-isolation probe.
	queryRefs := refs(queryTypes)
	if len(queue) != len(rows) {
		panic("mapper fixture requires structural instantiation")
	}
	orders := []int{}
	for _, pair := range comparisons {
		v := CompareTypes(values[pair[0]], values[pair[1]])
		if v < 0 {
			v = -1
		} else if v > 0 {
			v = 1
		}
		orders = append(orders, v)
	}
	result := map[string]any{"results": results, "types": rows, "mappers": mapperRows, "queries": queryRefs, "calls": calls, "comparisons": orders}
	if algebra {
		codes := []int32{}
		for _, d := range c.diagnostics.GetDiagnosticsForFile(c.files[0]) {
			codes = append(codes, d.Code())
		}
		result["diagnostics"] = codes
	}
	return result
}
