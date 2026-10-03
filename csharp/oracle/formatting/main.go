package main

import (
    "bufio"
    "encoding/json"
    "fmt"
    "os"
    "github.com/microsoft/TypeScript/tsc/internal/ast"
    "github.com/microsoft/TypeScript/tsc/internal/core"
    "github.com/microsoft/TypeScript/tsc/internal/format"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsutil"
    "github.com/microsoft/TypeScript/tsc/internal/parser"
    "github.com/microsoft/TypeScript/tsc/internal/tspath"
)

type input struct { File,Text,Target string; Insertion bool; Kind core.ScriptKind; Positions []int; Options json.RawMessage }
func main() {
    lines:=bufio.NewScanner(os.Stdin);lines.Buffer(make([]byte,4096),128*1024*1024);output:=json.NewEncoder(os.Stdout)
    for lines.Scan() {
        var item input;if err:=json.Unmarshal(lines.Bytes(),&item);err!=nil { panic(err) }
        opts:=lsutil.GetDefaultFormatCodeSettings();if len(item.Options)>0 { if err:=json.Unmarshal(item.Options,&opts);err!=nil { panic(err) } }
        file:=parser.ParseSourceFile(ast.SourceFileParseOptions{FileName:item.File,Path:tspath.Path(item.File)},item.Text,item.Kind)
        if item.Insertion { if err:=output.Encode(insertion(item,file,opts));err!=nil {panic(err)};continue }
        if item.Positions==nil { for pos:=0;pos<=len(item.Text)+1;pos++ { item.Positions=append(item.Positions,pos) } }
        positions:=make([]any,0,len(item.Positions));nodes:=[]any{}
        for _,pos:=range item.Positions {
            positions=append(positions,[]any{query(func() any { return format.GetIndentation(pos,file,opts,false) }),query(func() any { return format.GetIndentation(pos,file,opts,true) })})
        }
        var visit func(*ast.Node) bool
        visit=func(node *ast.Node) bool {
            if node.Flags&ast.NodeFlagsReparsed!=0 { return false }
            ignore:=core.NewTextRange(0,len(item.Text))
            nodes=append(nodes,[]any{uint32(node.Kind),node.Pos(),node.End(),query(func() any { return lsutil.IsCompletedNode(node,file) }),
                query(func() any { return format.GetIndentationForNode(node,nil,file,opts) }),query(func() any { return format.GetIndentationForNode(node,&ignore,file,opts) }),
                query(func() any { list:=format.GetContainingList(node,file);if list==nil { return nil };return []int{list.Loc.Pos(),list.Loc.End(),len(list.Nodes)} })})
            node.ForEachChild(visit);return false
        };visit(file.AsNode())
        if err:=output.Encode(map[string]any{"positions":positions,"nodes":nodes});err!=nil { panic(err) }
    }
    if err:=lines.Err();err!=nil { panic(err) }
}
func query(f func() any) (result any) { defer func(){if err:=recover();err!=nil { result=map[string]any{"error":fmt.Sprint(err)} }}();return f() }
