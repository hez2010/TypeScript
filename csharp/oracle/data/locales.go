package main
import("encoding/json";"fmt";"os";"golang.org/x/text/language")
func main(){
 data:=map[string][]string{"Languages":{},"Scripts":{},"Regions":{}}
 for a:='a';a<='z';a++ {for b:='a';b<='z';b++ {
  s:=string([]rune{a,b});if _,e:=language.ParseBase(s);e==nil{data["Languages"]=append(data["Languages"],s)}
  if _,e:=language.ParseRegion(s);e==nil{data["Regions"]=append(data["Regions"],s)}
  for c:='a';c<='z';c++ {
   s:=string([]rune{a,b,c});if _,e:=language.ParseBase(s);e==nil{data["Languages"]=append(data["Languages"],s)}
   for d:='a';d<='z';d++ {s:=string([]rune{a,b,c,d});if _,e:=language.ParseScript(s);e==nil{data["Scripts"]=append(data["Scripts"],s)}}
  }
 }}
 for i:=0;i<1000;i++ {s:=fmt.Sprintf("%03d",i);if _,e:=language.ParseRegion(s);e==nil{data["Regions"]=append(data["Regions"],s)}}
 if e:=json.NewEncoder(os.Stdout).Encode(data);e!=nil{panic(e)}
}