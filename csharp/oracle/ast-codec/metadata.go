// Development-only candidate-syntax control for separating parser and encoder differences.
package ast

type CSharpEncodingMetadata struct {
    Imports []int
    Augmentations []int
    Ambient []string
    Documentation map[int][]int
}

func CSharpSetEncodingMetadata(source *SourceFile, nodes []*Node, metadata CSharpEncodingMetadata) {
    source.imports = nil
    for _, id := range metadata.Imports { source.imports = append(source.imports, nodes[id]) }
    source.ModuleAugmentations = nil
    for _, id := range metadata.Augmentations { source.ModuleAugmentations = append(source.ModuleAugmentations, nodes[id]) }
    source.AmbientModuleNames = metadata.Ambient
    cache := map[*Node][]*Node{}
    for id, entries := range metadata.Documentation {
        documents := []*Node{}; for _, document := range entries { documents = append(documents, nodes[document]) }
        cache[nodes[id]] = documents
    }
    source.SetJSDocCache(cache)
    source.SetHasLazyJSDoc(false)
}
