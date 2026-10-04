package main
import("encoding/json";"os";"unicode";"reflect";"golang.org/x/text/unicode/norm")
func main(){
 decomposition:=[][2]any{}; classes,upper,streamSafe:=[][2]int{},[][2]int{},[][2]int{}; marks,upperCase,lowerCase:=[]int{},[]int{},[]int{}
 for r:=rune(0);r<=unicode.MaxRune;r++ {
  if r>=0xD800&&r<=0xDFFF {continue}
  s:=string(r)
  if d:=norm.NFD.String(s);d!=s && !(r>=0xAC00&&r<=0xD7A3) {rr:=[]int{};for _,v:=range d {rr=append(rr,int(v))};decomposition=append(decomposition,[2]any{int(r),rr})}
  p:=norm.NFD.PropertiesString(s)
  if c:=p.CCC();c!=0 {classes=append(classes,[2]int{int(r),int(c)})}
  v:=reflect.ValueOf(p); nLead,nTrail:=v.FieldByName("nLead").Uint(),v.FieldByName("flags").Uint()&3
  if n:=int(nLead|nTrail<<2);n!=0 {streamSafe=append(streamSafe,[2]int{int(r),n})}
  if unicode.Is(unicode.Mn,r) {marks=append(marks,int(r))}
  if unicode.IsUpper(r) {upperCase=append(upperCase,int(r))}
  if unicode.IsLower(r) {lowerCase=append(lowerCase,int(r))}
  if v:=unicode.ToUpper(r);v!=r {upper=append(upper,[2]int{int(r),int(v)})}
 }
 if err:=json.NewEncoder(os.Stdout).Encode(map[string]any{"version":unicode.Version,"normalizationVersion":norm.Version,"decomposition":decomposition,"classes":classes,"marks":marks,"upperCase":upperCase,"lowerCase":lowerCase,"upper":upper,"streamSafe":streamSafe});err!=nil {panic(err)}
}
