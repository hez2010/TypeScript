export function configurationCases() {
    const request = (method, params = {}) => ({ method, params });
    const files = {
        "/p/a.ts": "export const a = 1;", "/p/b.ts": "export const b = 2;", "/p/sub/c.ts": "export const c = 3;",
        "/p/base.json": '{"compilerOptions":{"noLib":true,"strict":false,"outDir":"./out","lib":["es2020"]},"include":["**/*.ts"],"compileOnSave":true}',
        "/p/tsconfig.json": '{"extends":"./base.json","compilerOptions":{"target":"es2022","strict":null,"module":"esnext"},"exclude":["sub"],"references":[{"path":"./sub"}]}',
        "/p/sub/tsconfig.json": '{"compilerOptions":{"composite":true,"noLib":true},"files":["c.ts"]}',
        "/p/jsconfig.json": '{"compilerOptions":{"checkJs":true},"files":["a.ts"]}',
        "/p/invalid.json": '{"compilerOptions":{"unknown":true,"target":"bad","strict":42},"files":[]}',
        "/p/syntax.json": '{"compilerOptions":{"target":"es2022",},"files":["a.ts"] // tail\n',
        "/p/primitive.json": '42', "/p/quoted.json": "{ unquoted: 'value', yes: true, nope: undefined }",
        "/p/args.txt": '--target es2022 --strict a.ts "sub/c.ts"',
    };
    const configs = ["tsconfig.json", "base.json", "jsconfig.json", "invalid.json", "syntax.json", "primitive.json", "quoted.json", "missing.json"];
    return [{ name: "configuration-files", cwd: "/p", files, requests: [
        ...configs.flatMap(file => ["readConfigFile", "parseConfigFile"].map(method => request(method, { file }))),
        ...["readConfigFile", "parseConfigFile"].map(method => request(method, { file: { uri: "file:///p/tsconfig.json" } })),
    ] }, { name: "configuration-command-line", cwd: "/p", files, requests: [
        [], ["--strict", "a.ts", "sub/c.ts"], ["--target", "es2022", "--module", "esnext", "--lib", "es2020,dom", "--types", "node", "--outDir", "./out", "a.ts"],
        ["@args.txt"], ["@missing.txt"], ["--unknown"], ["--target", "bad"], ["--strict", "false", "--checkJs", "--noEmit"], ["--target"], ["--paths", "x"],
    ].map(commandLine => request("parseCommandLine", { commandLine })) },
    { name: "configuration-json", cwd: "/p", files, requests: [
        ...[
            null, 42, [], "text",
            { files: [null], include: [null], exclude: [null] },
            { compilerOptions: { lib: [null], types: [null], rootDirs: [null] } },
            { compilerOptions: { noLib: true, target: "es2022" }, files: ["a.ts"] },
            { extends: "./base.json", compilerOptions: { module: "esnext" }, exclude: ["sub"] },
            { compilerOptions: { unknown: true, target: "bad", strict: 42 }, files: [] },
            { references: [{ path: "./sub", circular: true }] }, { include: ["missing/**/*.ts"] },
        ].flatMap(json => [request("parseJsonConfigFileContent", { json, configDirectory: "/p" }),
            request("parseJsonConfigFileContent", { json, configFileName: { uri: "file:///p/virtual.json" } })]),
        request("parseJsonConfigFileContent", { json: {} }),
        request("parseJsonConfigFileContent", { json: {}, configDirectory: "/p", configFileName: "/p/virtual.json" }),
    ] }];
}
