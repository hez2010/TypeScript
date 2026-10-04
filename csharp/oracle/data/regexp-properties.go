package main
import("encoding/json";"os";"unicode";"regexp/syntax")
func main(){
 names:=map[string]bool{"Any":true,"Assigned":true,"ASCII":true}
 for name:=range unicode.Categories { names[name]=true }; for name:=range unicode.CategoryAliases { names[name]=true }; for name:=range unicode.Scripts { names[name]=true }
 tables:=map[string][][]rune{}
 for name:=range names {
  parts:=[][]rune{}
  for _,flag:=range []syntax.Flags{syntax.Perl,syntax.Perl|syntax.FoldCase} {
   expr,err:=syntax.Parse("\\p{"+name+"}",flag);if err!=nil {panic(err)}
   values:=expr.Rune
   if expr.Op==syntax.OpAnyChar {values=[]rune{0,unicode.MaxRune}} else if expr.Op==syntax.OpLiteral {values=[]rune{values[0],values[0]}}
   parts=append(parts,values)
  };tables[name]=parts
 }
 json.NewEncoder(os.Stdout).Encode(map[string]any{"version":unicode.Version,"tables":tables})
}
