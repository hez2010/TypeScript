package autoimport_test

import (
    "context"
    "testing"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsconv"
    "github.com/microsoft/TypeScript/tsc/internal/project"
    "github.com/microsoft/TypeScript/tsc/internal/testutil/projecttestutil"
    "github.com/microsoft/TypeScript/tsc/internal/vfs/vfstest"
)

// Exercise the layouts from util_test.go through package extraction and the project
// session, including both symlink spellings of a shared dependency's export.
func TestCSharpAutoImportRealpaths(t *testing.T) {
    cases := []struct{name, main string; files map[string]any}{
        {"follows-node-modules-symlinks", "/symlink-bin/pkg/main.ts", map[string]any{
            "/symlink-bin/pkg": vfstest.Symlink("/real/bin/pkg"),
            "/real/bin/pkg/tsconfig.json": `{"compilerOptions":{"noLib":true},"files":["main.ts","index.d.ts"]}`,
            "/real/bin/pkg/package.json": `{"dependencies":{"dep":"*"}}`,
            "/real/bin/pkg/main.ts": "export {};",
            "/real/bin/pkg/index.d.ts": "export declare const a: number; export * from 'dep';",
            "/real/bin/pkg/node_modules/dep": vfstest.Symlink("/real/dep"),
            "/real/dep/package.json": `{"name":"dep","version":"1.0.0","types":"index.d.ts"}`,
            "/real/dep/index.d.ts": "export declare const b: number; export * from './src/utils/helper';",
            "/real/dep/src/utils/helper.d.ts": "export declare const c: number;",
        }},
        {"shared-dependency-realpath", "/workspace/packages/app-a/main.ts", map[string]any{
            "/workspace/packages/app-a": vfstest.Symlink("/store/app-a"),
            "/workspace/packages/app-b": vfstest.Symlink("/store/app-b"),
            "/store/app-a/tsconfig.json": `{"compilerOptions":{"noLib":true},"files":["main.ts","index.d.ts","/workspace/packages/app-b/index.d.ts"]}`,
            "/store/app-a/package.json": `{"dependencies":{"shared-lib":"*"}}`,
            "/store/app-a/main.ts": "export {};",
            "/store/app-a/index.d.ts": "export declare const a: number; export * from 'shared-lib';",
            "/store/app-b/index.d.ts": "export declare const b: number; export * from 'shared-lib';",
            "/store/app-a/node_modules/shared-lib": vfstest.Symlink("/store/shared-lib"),
            "/store/app-b/node_modules/shared-lib": vfstest.Symlink("/store/shared-lib"),
            "/store/shared-lib/package.json": `{"name":"shared-lib","version":"1.0.0","types":"index.d.ts"}`,
            "/store/shared-lib/index.d.ts": "export declare const shared: string;",
        }},
        {"non-symlinked-package-with-symlinked-dependencies", "/real/my-pkg/main.ts", map[string]any{
            "/real/my-pkg/tsconfig.json": `{"compilerOptions":{"noLib":true},"files":["main.ts","index.d.ts"]}`,
            "/real/my-pkg/package.json": `{"dependencies":{"dep":"*"}}`,
            "/real/my-pkg/main.ts": "export {};",
            "/real/my-pkg/index.d.ts": "export declare const a: number; export * from 'dep';",
            "/real/my-pkg/node_modules/dep": vfstest.Symlink("/real/dep"),
            "/real/dep/package.json": `{"name":"dep","version":"1.0.0","types":"index.d.ts"}`,
            "/real/dep/index.d.ts": "export declare const b: number;",
        }},
        {"alias-checker-diagnostics", "/app/main.ts", map[string]any{
            "/app/tsconfig.json": `{"compilerOptions":{"noLib":true},"files":["main.ts"]}`,
            "/app/package.json": `{"dependencies":{"pkg":"*"}}`,
            "/app/main.ts": "export {};",
            "/app/node_modules/pkg/package.json": `{"name":"pkg","version":"1.0.0","types":"index.ts"}`,
            "/app/node_modules/pkg/index.ts": "declare function f(arg: { a: string }): () => void;\nexport const x = f({ a: 1 });\n",
        }},
    }
    for _, test := range cases { t.Run(test.name, func(t *testing.T) {
        session, _ := projecttestutil.CSharpSetupWithOptions(t, test.files, &project.SessionOptions{CurrentDirectory: "/"})
        defer session.Close()
        uri := lsconv.FileNameToDocumentURI(test.main)
        session.DidOpenFile(context.Background(), uri, 1, "export {};", "typescript")
        if _, err := session.GetCurrentLanguageServiceWithAutoImports(context.Background(), uri); err != nil { t.Fatal(err) }
        session.WaitForBackgroundTasks()
    }) }
}
