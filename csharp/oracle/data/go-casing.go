package main
import("encoding/json";"os";"unicode")
func main(){
 lower,fold:=[][2]int{},[][2]int{}
 for r:=rune(0);r<=unicode.MaxRune;r++ {
  if x:=unicode.ToLower(r);x!=r {lower=append(lower,[2]int{int(r),int(x)})}
  if x:=unicode.SimpleFold(r);x!=r {fold=append(fold,[2]int{int(r),int(x)})}
 }
 if err:=json.NewEncoder(os.Stdout).Encode(map[string]any{"version":unicode.Version,"lower":lower,"fold":fold});err!=nil {panic(err)}
}
