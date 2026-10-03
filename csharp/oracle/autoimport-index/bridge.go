// Development differential access to the original index; not shipped.
package autoimport

func CSharpIndexInsert[T Named](index *Index[T], entry T) { index.insertAsWords(entry) }
func CSharpWordIndices(name string) []int                 { return wordIndices(name) }
