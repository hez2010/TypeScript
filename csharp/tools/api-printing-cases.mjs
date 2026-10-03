export function printingCases() {
    const ref = name => ({ $ref: name });
    const request = (method, params = {}, save) => ({ method, params, ...(save ? { save } : {}) });
    const cases = [
        ["lines", "const a=1; const b=2;\n\n\nconst c=3;\n"],
        ["comments", "// leading\n\nconst a=1; // trailing\n\n// next\nconst b=2;\n/*end*/"],
        ["blocks", "function f() { let x=1;\n\nreturn x; }\nif(x) { x++; y(); } else {\ny();\n}"],
        ["class", "export\nclass C { a = 1; b = 2;\n\nmethod() {\nreturn 1;\n}\n}\n"],
        ["object", "const o={\nx:1,\n\ny:2, m() { return 1; }\n}; const p={a:1,b:2};\n"],
        ["array", "const a=[1,\n\n2,\n3,]; f(1,\n2,\n\n3);\n"],
        ["types", "type X = { a: number; b: string;\n\nc: boolean; };\ninterface I { a: number;\nb: string; }\n"],
        ["expressions", "const a =\n\nfoo\n .bar\n [x];\nconst b = a\n ? 1\n : 2; const c = a\n + b;\n"],
        ["control", "if (a)\n\nf();\nelse\n\ng();\nfor (let i=0;i<3;i++)\n\nf(); do\nf();\nwhile (a);"],
        ["switch", "switch (x) { case 1: f(); g(); break;\n\ndefault:\n\nh(); }"],
        ["unicode", 'const 日本語 = "😀é"; const s = `文字`; const t = `😀${日本語}é`;'],
        ["unterminated-double", 'const x = "😀text'], ["unterminated-single", "const x = 'text\\"],
        ["unterminated-template", "const x = `text"], ["unterminated-tail", "const x = `text${1}tail"],
        ["unterminated-regex", "const x = /text"], ["unterminated-regex-slash", "const x = /text\\"],
        ["templates", 'const x = `a\\${b}`; const y = `a${b}c${d}e`;'],
        ["empty", "\n\n\n"], ["empty-blocks", "function f() {\n\n}\nclass C {\n\n}\nif (a) {\n\n}\n"],
        ["jsx", "const x = <div\n\nid='a'>hello\n<span>{a}</span></div>;", 4],
        ["parentheses", "const a=(\n\nx\n\n); f(\n\nx\n\n); const b=[\n\n1,\n\n2\n\n];"],
        ["type-lists", "type X<T\n\n> = [a: T, b: number\n\n]; function f(x: X<\nstring\n>) {}"],
        ["imports", 'import { a,\n b\n} from "pkg"; export {\n a, b\n}; import "pkg"\nwith { type: "json"\n};'],
        ["modifiers", "export\ndefault\nclass C { public\nstatic\nx = 1;\n}\n"],
        ["decorators", "@dec\n@other\nexport class C { @dec\nmethod(@dec x: number) {} }"],
        ["try", "try { f(); } catch (e) { g(); } finally { h(); }"],
        ["try-lines", "try {\nf();\n}\n\ncatch (e) {\ng();\n}\n\nfinally {\nh();\n}"],
        ["enum", "enum E { A, B,\nC }\nnamespace N { export const a=1; export const b=2; }"],
        ["binding", "const { a,\nb\n} = x; const [c,\nd\n]=y;"],
        ["mapped", "type M<T> = {\nreadonly [K in keyof T]?: T[K]\n};"],
        ["json", '{\n"a": 1,\n\n"b": [1,2]\n}', 6],
        ["shebang", "#!/usr/bin/node\n\n'use strict';\n\n// before\nlet a=1;\n"],
        ["jsx-newlines", "const x = <div\n foo={1}\n >\ntext\n</div>; const y=<div\n\n/>;", 4],
        ["empty-lists", "const x={\n\n}; const y=[\n\n]; f(\n\n);"],
        ["comment-lists", "const a=[\n// first\n1,\n// second\n2\n// last\n]; f(\n/*a*/ 1, /*b*/2\n);"],
    ];
    return cases.map(([name, sourceText, scriptKind = 3]) => ({ name: `printing-${name}`, files: {}, requests: [
        request("createSourceFile", { fileName: "/p/main.ts", sourceText, options: { scriptKind } }, "ast"),
        ...[false, true].flatMap(preserveSourceNewlines => [false, true].flatMap(neverAsciiEscape => [false, true].map(terminateUnterminatedLiterals =>
            request("printNode", { data: ref("ast.data"), preserveSourceNewlines, neverAsciiEscape, terminateUnterminatedLiterals })))),
    ] })).concat([{ name: "printing-invalid-packets", files: {}, requests: [
        "", "!", "A", "AA", "AAA", "AAAA", "A=", "AA=", "AA=A", "AA==!", "AA==\r\n!", "AA==\r\n", " A===", "AAAA AAAA", "A\r\nA", "😀", "/x==", "///=", "A/==",
        Buffer.alloc(44).toString("base64"),
    ].map(data => request("printNode", { data })) }]);
}
