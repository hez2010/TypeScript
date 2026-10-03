package main

import (
    "context"
    "github.com/microsoft/TypeScript/tsc/internal/ast"
    "github.com/microsoft/TypeScript/tsc/internal/core"
    "github.com/microsoft/TypeScript/tsc/internal/format"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsutil"
    "github.com/microsoft/TypeScript/tsc/internal/parser"
    "github.com/microsoft/TypeScript/tsc/internal/printer"
)

func insertion(item input, file *ast.SourceFile, opts lsutil.FormatCodeSettings) any {
    nodes:=[]*ast.Node{file.AsNode()}; nodes=append(nodes,file.Statements.Nodes...)
    factory:=ast.NewNodeFactory(ast.NodeFactoryHooks{})
    target:=parser.ParseSourceFile(ast.SourceFileParseOptions{FileName:"/target.ts"},item.Target,core.ScriptKindTS)
    output:=[]any{}
    for _,node:=range nodes {
        var detach func(*ast.Node) bool
        detach=func(n *ast.Node) bool { n.Parent=nil;n.ForEachChild(detach);return false };detach(node)
        text,positioned:=printer.PrintAndPositionNode(factory,node,nil,opts.NewLineCharacter,opts.IndentSize,nil)
        ranges:=[]any{}
        var collect func(*ast.Node) bool
        collect=func(n *ast.Node) bool {
            lists:=[]any{}
            visitor:=ast.NewNodeVisitor(func(n *ast.Node) *ast.Node {return n},factory,ast.NodeVisitorHooks{
                VisitNodes:func(list *ast.NodeList,v *ast.NodeVisitor) *ast.NodeList { if list!=nil {lists=append(lists,[]int{list.Pos(),list.End(),len(list.Nodes),core.IfElse(list.HasTrailingComma(),1,0)})};return list },
                VisitModifiers:func(list *ast.ModifierList,v *ast.NodeVisitor) *ast.ModifierList { if list!=nil {lists=append(lists,[]int{list.Pos(),list.End(),len(list.Nodes),core.IfElse(list.HasTrailingComma(),1,0)})};return list },
            });n.VisitEachChild(visitor)
            ranges=append(ranges,[]any{int(n.Kind),n.Pos(),n.End(),int(n.Flags),lists});n.ForEachChild(collect);return false
        };collect(positioned)
        formatted:=[]any{}
        if node.Kind!=ast.KindSourceFile {
            for _,pos:=range item.Positions {
                formatted=append(formatted,query(func() any {
                    synthetic:=printer.CreateSyntheticSourceFile(factory,positioned,text,file.ParseOptions())
                    initial:=format.GetIndentation(pos,target,opts,format.GetLineStartPositionForPosition(pos,target)==pos)
                    delta:=0;if opts.IndentSize!=0 && format.ShouldIndentChildNode(opts,node,nil,nil) {delta=opts.IndentSize}
                    ctx:=format.WithFormatCodeSettings(context.Background(),opts,opts.NewLineCharacter)
                    edits:=format.FormatNodeGivenIndentation(ctx,positioned,synthetic,file.LanguageVariant,initial,delta)
                    return core.ApplyBulkEdits(text,edits)
                }))
            }
        }
        output=append(output,map[string]any{"text":text,"ranges":ranges,"formatted":formatted})
    }
    return output
}
