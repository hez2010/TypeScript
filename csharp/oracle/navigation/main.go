package main

import (
    "bufio"
    "encoding/json"
    "fmt"
    "os"
    "github.com/microsoft/TypeScript/tsc/internal/ast"
    "github.com/microsoft/TypeScript/tsc/internal/astnav"
    "github.com/microsoft/TypeScript/tsc/internal/core"
    "github.com/microsoft/TypeScript/tsc/internal/parser"
    "github.com/microsoft/TypeScript/tsc/internal/tspath"
)
type input struct { File, Text string; Kind core.ScriptKind; Positions []int; TemplateSourceRanges bool }
func main() {
    lines := bufio.NewScanner(os.Stdin); lines.Buffer(make([]byte, 4096), 128*1024*1024)
    output := json.NewEncoder(os.Stdout)
    for lines.Scan() {
        var item input; if err := json.Unmarshal(lines.Bytes(), &item); err != nil { panic(err) }
        file := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName:item.File, Path:tspath.Path(item.File)}, item.Text, item.Kind)
        if item.TemplateSourceRanges {
            // Isolated control for an existing parser representation difference:
            // use the actual parameter span instead of Go's zero-range list.
            seen:=map[*ast.Node]bool{}
            var visit func(*ast.Node) bool
            visit=func(node *ast.Node) bool {
                if seen[node] { return false };seen[node]=true
                if node.Kind==ast.KindJSDocTemplateTag {
                    list:=node.AsJSDocTemplateTag().TypeParameters
                    if list!=nil&&len(list.Nodes)>0 { list.Loc=core.NewTextRange(list.Nodes[0].Pos(),list.Nodes[len(list.Nodes)-1].End()) }
                }
                for _,doc:=range node.JSDoc(file) { visit(doc) };node.ForEachChild(visit);return false
            }
            visit(file.AsNode())
        }
        if item.Positions == nil { item.Positions = make([]int,len(item.Text)+1); for i:=range item.Positions { item.Positions[i]=i } }
        result := make([][]any,len(item.Positions))
        for i,pos := range item.Positions {
            result[i]=make([]any,6)
            for method:=range 6 { result[i][method]=query(file,pos,method) }
        }
        if err:=output.Encode(result);err!=nil { panic(err) }
    }
    if err:=lines.Err();err!=nil { panic(err) }
}
func query(file *ast.SourceFile,pos,method int) (result any) {
    defer func() { if failure:=recover();failure!=nil { result=map[string]any{"error":fmt.Sprint(failure)} } }()
    get:=func() *ast.Node {
        switch method {
        case 0:return astnav.GetTokenAtPosition(file,pos)
        case 1:return astnav.GetTouchingPropertyName(file,pos)
        case 2:return astnav.GetTouchingToken(file,pos)
        case 3:return astnav.FindPrecedingToken(file,pos)
        case 4:return astnav.FindPrecedingTokenEx(file,pos,nil,true)
        default:return astnav.FindNextToken(astnav.GetTokenAtPosition(file,pos),file.AsNode(),file)
        }
    }
    node:=get();if node==nil { return nil }
    pk,pp,pe:=ast.KindUnknown,-1,-1;if node.Parent!=nil { pk,pp,pe=node.Parent.Kind,node.Parent.Pos(),node.Parent.End() }
    return []any{uint32(node.Kind),node.Pos(),node.End(),uint32(pk),pp,pe,astnav.GetStartOfNode(node,file,false),astnav.GetStartOfNode(node,file,true),node==get()}
}
