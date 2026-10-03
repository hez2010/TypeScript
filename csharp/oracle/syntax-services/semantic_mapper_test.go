package fourslash_test

import (
    "testing"
    "github.com/microsoft/TypeScript/tsc/internal/fourslash"
    "github.com/microsoft/TypeScript/tsc/internal/lsp/lsproto"
    "github.com/microsoft/TypeScript/tsc/internal/testutil/contentmappertest"
)

func TestCSharpWorkspaceSymbolMapper(t *testing.T) {
    for _, sample := range []struct{name, mapper string}{
        {"/app.astro",contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro",contentmappertest.DuplicateProjectionMapper},
        {"/folding-disabled.astro",contentmappertest.PrefixedSupplementalMapper},
    } {
        f,done:=newContentMapperFourslash(t,"// @Filename: "+sample.name+"\nconst 名 = 1;\nfunction 方法() { return 名; }",sample.mapper,".astro")
        func(){defer done();f.GoToFile(t,sample.name);f.VerifyWorkspaceSymbol(t,[]*fourslash.VerifyWorkspaceSymbolCase{{Pattern:"",Includes:new([]*lsproto.SymbolInformation{})}})}()
    }
}

func TestCSharpSemanticMapper(t *testing.T) {
    samples := []struct { name, mapper string; tokens []fourslash.SemanticToken }{
        {"/app.astro",contentmappertest.PrefixedSupplementalMapper,[]fourslash.SemanticToken{
            {Type:"variable.declaration.readonly",Text:"value"},{Type:"function.declaration",Text:"outer"},
            {Type:"variable.readonly",Text:"value"},{Type:"function",Text:"outer"},
        }},
        {"/app.astro",contentmappertest.DuplicateProjectionMapper,[]fourslash.SemanticToken{
            {Type:"variable.declaration.readonly",Text:"value"},{Type:"function.declaration",Text:"outer"},
            {Type:"variable.readonly",Text:"value"},{Type:"function",Text:"outer"},
        }},
        {"/folding-disabled.astro",contentmappertest.PrefixedSupplementalMapper,nil},
    }
    for _, sample := range samples {
        f, done := newContentMapperFourslash(t, "// @Filename: "+sample.name+"\nconst value = 1;\nfunction outer() { return value; }\nouter();",sample.mapper,".astro")
        func() { defer done(); f.GoToFile(t,sample.name); f.VerifySemanticTokens(t,sample.tokens) }()
    }
}
