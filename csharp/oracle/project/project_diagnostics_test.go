package project_test

import (
    "context"
    "testing"
    "github.com/microsoft/TypeScript/tsc/internal/bundled"
    "github.com/microsoft/TypeScript/tsc/internal/core"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsutil"
    "github.com/microsoft/TypeScript/tsc/internal/lsp/lsproto"
    "github.com/microsoft/TypeScript/tsc/internal/project"
    "github.com/microsoft/TypeScript/tsc/internal/testutil/projecttestutil"
)

func TestCSharpProjectDiagnostics(t *testing.T) {
    for _, encoding := range []lsproto.PositionEncodingKind{lsproto.PositionEncodingKindUTF8, lsproto.PositionEncodingKindUTF16} {
        t.Run(string(encoding), func(t *testing.T) {
            files := map[string]any{
                "/src/base.json": `{ "description":"😀", "compilerOptions":{"target":"nöpē"}}`,
                "/src/tsconfig.json": `{"extends":"./base.json","compilerOptions":{"noLib":true,"types":["missing"]}}`,
                "/src/custom.json": `{"compilerOptions":{"noLib":true,"baseUrl":"."}}`,
                "/src/a.ts": "export const 名 = 1;",
                "/other/tsconfig.json": `{"compilerOptions":{"noLib":true,"target":"nope"}}`,
                "/other/b.ts": "export const b = 2;",
            }
            session, utils := projecttestutil.CSharpSetupWithOptions(t, files, &project.SessionOptions{
                CurrentDirectory:"/", DefaultLibraryPath:bundled.LibPath(), PositionEncoding:encoding, WatchEnabled:true, PushDiagnosticsEnabled:true,
            })
            request := func(uri lsproto.DocumentUri) {
                if _, err := session.GetLanguageService(context.Background(), uri); err != nil { t.Fatal(err) }
                session.WaitForBackgroundTasks()
            }
            first, second := lsproto.DocumentUri("file:///src/a.ts"), lsproto.DocumentUri("file:///other/b.ts")
            session.DidOpenFile(context.Background(), first, 1, files["/src/a.ts"].(string), lsproto.LanguageKindTypeScript)
            request(first)
            if err := utils.FS().WriteFile("/src/base.json", `{"compilerOptions":{"target":"es2020"}}`); err != nil { t.Fatal(err) }
            session.DidChangeWatchedFiles(context.Background(), []*lsproto.FileEvent{{Uri:"file:///src/base.json",Type:lsproto.FileChangeTypeChanged}})
            session.WaitForBackgroundTasks()
            prefs := lsutil.NewDefaultUserPreferences(); prefs.EnableValidation = core.TSFalse
            session.Configure(prefs); request(first)
            prefs.EnableValidation = core.TSTrue; prefs.CustomConfigFileName = "custom.json"
            session.Configure(prefs); request(first)
            session.DidOpenFile(context.Background(), second, 1, files["/other/b.ts"].(string), lsproto.LanguageKindTypeScript)
            request(second)
            session.DidCloseFile(context.Background(), first); session.WaitForBackgroundTasks()
            session.DidOpenFile(context.Background(), first, 2, files["/src/a.ts"].(string), lsproto.LanguageKindTypeScript)
            request(first)
        })
    }
}
