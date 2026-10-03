package fourslash

import (
    "testing"
    "strings"
    "fmt"
    "github.com/microsoft/TypeScript/tsc/internal/testutil/contentmappertest"
    "github.com/microsoft/TypeScript/tsc/internal/core"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsconv"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsutil"
    "github.com/microsoft/TypeScript/tsc/internal/lsp/lsproto"
)

func TestCSharpAutoImportCapabilities(t *testing.T) {
    samples := []struct{name,text,mapper string}{
        {"/a.ts", "const 字='😀';\r\n$V~", ""},
        {"/a.js", "const 字='😀';\n$V~", ""},
        {"/a.ts", "const one=1;\nconst two=2;\nimport $V~", ""},
        {"/a.ts", "const one=1\nconst two=2\nimport $V~", ""},
        {"/a.ts", "import type { $ValueType } from './dep';\n$V~", ""},
        {"/a.ts", "import type $V~", ""},
        {"/a.ts", "let value: $V~", ""},
        {"/app.vue", "<template>日本語 😀</template>\n<script>\n$V~\n</script>", contentmappertest.ComponentMapper},
        {"/app.astro", "$V~", contentmappertest.PrefixedSupplementalMapper},
    }
    for index,sample:=range samples {t.Run(fmt.Sprint(index),func(t *testing.T){
        position:=strings.Index(sample.text,"~");source:=strings.Replace(sample.text,"~","",1)
        for mask:=0;mask<32;mask++ {
            caps:=GetDefaultCapabilities()
            caps.TextDocument.Completion.CompletionItem.SnippetSupport=new(mask&1!=0)
            caps.TextDocument.Completion.CompletionItem.CommitCharactersSupport=new(mask&2!=0)
            caps.TextDocument.Completion.CompletionItem.InsertReplaceSupport=new(mask&4!=0)
            caps.TextDocument.Completion.CompletionItem.LabelDetailsSupport=new(mask&8!=0)
            if mask&16==0 {caps.TextDocument.Completion.CompletionList.ItemDefaults=nil}
            options:=&FourslashOptions{Capabilities:caps}
            content:="// @noLib: true\n// @allowJs: true\n// @Filename: "+sample.name+"\n"+source
            if sample.mapper!="" {
                content="// @Filename: /tsconfig.json\n{\"compilerOptions\":{\"noLib\":true},\"contentMappers\":[{\"package\":\"mapper\",\"extensions\":[\".astro\",\".vue\"]}]}\n// @Filename: /node_modules/mapper/package.json\n"+contentmappertest.PackageJSON(sample.mapper)+"\n// @Filename: "+sample.name+"\n"+source
                options.ContentMapperSpawner=contentmappertest.NewSpawner();options.RunExternalCode=true
            }
            content+="\n// @Filename: /dep.ts\nexport interface $ValueType { text:string }; export const $Value=1; export default class $ValueDefault {}; /** @deprecated */ export function $ValueFunction() {}"
            content+="\n// @Filename: /alias.ts\nexport { $Value as $ValueAlias } from './dep'; export type { $ValueType } from './dep';"
            content+="\n// @Filename: /bad.ts\ndeclare function f(arg: { a: string }): () => void; export const $ValueInvalid = f({ a: 1 });"
            f,done:=NewFourslashWithOptions(t,content,options)
            func(){defer done();f.GoToFile(t,sample.name)
                prefs:=lsutil.NewDefaultUserPreferences()
                if mask&1!=0 {prefs.PreferTypeOnlyAutoImports=core.TSTrue}else{prefs.PreferTypeOnlyAutoImports=core.TSFalse}
                if mask&2==0 {prefs.FormatCodeSettings.Semicolons=lsutil.SemicolonPreferenceInsert}else{prefs.FormatCodeSettings.Semicolons=lsutil.SemicolonPreferenceRemove}
                if mask&4!=0 {prefs.QuotePreference=lsutil.QuotePreferenceSingle}
                f.Configure(t,prefs)
                script:=f.getScriptInfo(sample.name)
                response:=f.sendRequest(t,lsproto.TextDocumentCompletionInfo,&lsproto.CompletionParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(sample.name)},Position:f.converters.PositionToLineAndCharacter(script,core.TextPos(position))})
                if response.List!=nil {for _,item:=range response.List.Items {if item.Data!=nil && item.Data.AutoImport!=nil {f.sendRequest(t,lsproto.CompletionItemResolveInfo,item)}}}
            }()
        }
    })}
}

func TestCSharpCompletionSnippets(t *testing.T) {
    samples := []struct{name,text,mapper string}{
        {"/a.ts", "const 字='😀';\r\ninterface Shape { $方法?($引数:string,...$残り:number[]):void; other:number; }\r\nconst value: Shape = { ~ };", ""},
        {"/a.ts", "interface Shape { '$quoted':($value:string)=>void; }\nconst value: Shape = { ~ };", ""},
        {"/a.ts", "declare const v: '$value' | '字' | -2 | -3n;\nswitch(v) { ~ }", ""},
        {"/a.ts", "enum E { '$A'=1, '字'=2 }\ndeclare const v:E;\nswitch(v) { ~ }", ""},
        {"/app.astro", "interface Shape { 方法($値:string):void; }\nconst value: Shape = { ~ };", contentmappertest.PrefixedSupplementalMapper},
        {"/app.vue", "<template>日本語 😀</template>\n<script>\ninterface Shape { 方法($値:string):void; }\nconst value: Shape = { ~ };\n</script>", contentmappertest.ComponentMapper},
        {"/a.ts", "const 字='😀';\r\ninterface Shape { $方法($引数:{字?:string}):void; optional?:number; }\r\nclass C implements Shape {\r\n  public ~\r\n}", ""},
        {"/a.ts", "interface Shape { f(a:string):string; f(a:number,b?:boolean):number; }\nclass C implements Shape { ~ }", ""},
        {"/a.ts", "class Base { get $値():string {return '';} set $値($value:string) {} }\nclass C extends Base { ~ }", ""},
        {"/a.ts", "const a=1\nconst b=2\ninterface Shape { 値:number }\nclass C implements Shape { ~ }", ""},
        {"/app.astro", "interface Shape { $方法($値:string):void; }\nclass C implements Shape {\n public ~\n}", contentmappertest.PrefixedSupplementalMapper},
        {"/app.vue", "<template>日本語 😀</template>\n<script>\ninterface Shape { $方法($値:string):void; }\nclass C implements Shape {\n public ~\n}\n</script>", contentmappertest.ComponentMapper},
        {"/a.ts", "// header 😀\r\n\r\nimport { Base } from './dep';\r\nclass C extends Base {\r\n public ~\r\n}", ""},
        {"/a.ts", "import { Base } from './dep'\nconst one=1\nconst two=2\nclass C extends Base {\n ~\n}", ""},
        {"/a.ts", "import { Base } from './dep';\nclass C extends Base {\n readonly ~\n}", ""},
        {"/a.ts", "import {\n Base, // base\n} from './dep';\nclass C extends Base {\n ~\n}", ""},
        {"/app.astro", "import { Base } from './dep';\nclass C extends Base {\n public ~\n}", contentmappertest.PrefixedSupplementalMapper},
        {"/app.vue", "<template>日本語 😀</template>\n<script>\nimport { Base } from './dep';\nclass C extends Base {\n public ~\n}\n</script>", contentmappertest.ComponentMapper},
        {"/a.ts", "import { choose } from './dep';\nswitch(choose()) { ~ }", ""},
    }
    for index,sample:=range samples { t.Run(fmt.Sprint(index),func(t *testing.T) {
        position:=strings.Index(sample.text,"~"); source:=strings.Replace(sample.text,"~","",1)
        for mask:=0;mask<8;mask++ {
            caps:=GetDefaultCapabilities()
            caps.TextDocument.Completion.CompletionItem.SnippetSupport=new(mask&1!=0)
            caps.TextDocument.Completion.CompletionItem.LabelDetailsSupport=new(mask&2!=0)
            if mask&4==0 {caps.TextDocument.Completion.CompletionList.ItemDefaults=nil}
            content:="// @noLib: true\n// @Filename: "+sample.name+"\n"+source
            options:=&FourslashOptions{Capabilities:caps}
            if sample.mapper!="" {
                content="// @Filename: /tsconfig.json\n{\"compilerOptions\":{\"noLib\":true},\"contentMappers\":[{\"package\":\"mapper\",\"extensions\":[\".astro\",\".vue\"]}]}\n// @Filename: /node_modules/mapper/package.json\n"+contentmappertest.PackageJSON(sample.mapper)+"\n// @Filename: "+sample.name+"\n"+source
                options.ContentMapperSpawner=contentmappertest.NewSpawner(); options.RunExternalCode=true
            }
            if index>=12 {content+="\n// @Filename: /dep.ts\nexport interface $引数 { name:string }; export interface $結果 { value:string }; export default class $Default {}; export class Base { property:$Default; method(arg:$引数):$結果 { throw 0; } }; export enum $Enum { First, Second }; export function choose():$Enum { return $Enum.First; }"}
            f,done:=NewFourslashWithOptions(t,content,options)
            func(){defer done(); f.GoToFile(t,sample.name)
                prefs:=lsutil.NewDefaultUserPreferences(); prefs.IncludeCompletionsWithObjectLiteralMethodSnippets=core.TSTrue
                prefs.IncludeCompletionsWithClassMemberSnippets=core.TSTrue
                prefs.IncludeCompletionsForModuleExports=core.TSFalse
                if index==14 || index==15 {prefs.PreferTypeOnlyAutoImports=core.TSTrue}
                if index%2==0 {prefs.FormatCodeSettings.NewLineCharacter="\r\n";prefs.FormatCodeSettings.IndentSize=2}
                f.Configure(t,prefs)
                script:=f.getScriptInfo(sample.name)
                response:=f.sendRequest(t,lsproto.TextDocumentCompletionInfo,&lsproto.CompletionParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(sample.name)},Position:f.converters.PositionToLineAndCharacter(script,core.TextPos(position))})
                if response.List!=nil {for _,item:=range response.List.Items {if item.Data!=nil && (item.Data.Source!="" || index>=6 && (item.InsertText!=nil || item.TextEdit!=nil)) {f.sendRequest(t,lsproto.CompletionItemResolveInfo,item)}}}
            }()
        }
    }) }
}

func TestCSharpJSDocTags(t *testing.T) {
    samples := []struct{name,text,mapper string}{
        {"/a.ts", "const 字='😀';\r\n/**\r\n * @~\r\n */\r\nfunction 方法(名前:string, 数=3) {}", ""},
        {"/a.js", "/**\n * ~\n */\nfunction f(a='字', b=3, c=true, d=undefined, ...rest) {}", ""},
        {"/a.js", "/**\n * @~\n */\nfunction f({a,b:{c=3,d},e:[first],...rest}={a:1,b:{c:2,d:4},e:[]}) {}", ""},
        {"/a.js", "/**\n * @~\n */\nfunction f([a,b]=[1,2], c={field:'value'}) {}", ""},
        {"/a.js", "/**\n * @~\n */\nfunction f({[key]:value, known}) {}", ""},
        {"/a.ts", "/**\n * @param first\n * @param 名~\n */\nfunction f(first:number,名前:string,名札:string,other:boolean) {}", ""},
        {"/a.ts", "/**\n * @param {string} ~\n */\nfunction f(名前:string,second:string) {}", ""},
        {"/a.ts", "/**\n * @par~am {string} 名前\n */\nfunction f(名前:string) {}", ""},
        {"/a.js", "/**\n * @param known\n * ~\n */\nfunction f(known,{a:{nested},b},last) {}", ""},
        {"/a.js", "/**\n * @~\n */\nfunction f(a={\n value:1\n}, b='"+strings.Repeat("字",30)+"') {}", ""},
        {"/a.ts", "/**\n * plain text ~\n */\nfunction f(name:string) {}", ""},
        {"/a.ts", "/**\n\u200b\u3000* (|) ~\n */\nfunction f(name:string) {}", ""},
        {"/app.astro", "/**\n * @~\n */\nexport function 方法(名前:string) {}", contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro", "// folding-duplicate\n/**\n * @~\n */\nexport function 方法(名前:string) {}", contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro", "/**\n * @~\n */\nexport function 方法(名前:string) {}", contentmappertest.DuplicateProjectionMapper},
        {"/app.vue", "<template>日本語 😀</template>\n<script>\n/**\n * @~\n */\nexport function 方法(名前:string) {}\n</script>", contentmappertest.ComponentMapper},
    }
    for index,sample:=range samples { t.Run(fmt.Sprint(index),func(t *testing.T) {
        position:=strings.Index(sample.text,"~"); source:=strings.Replace(sample.text,"~","",1)
        for mask:=0;mask<8;mask++ {
            caps:=GetDefaultCapabilities()
            caps.TextDocument.Completion.CompletionItem.SnippetSupport=new(mask&1!=0)
            caps.TextDocument.Completion.CompletionItem.CommitCharactersSupport=new(mask&2!=0)
            if mask&4==0 {caps.TextDocument.Completion.CompletionList.ItemDefaults=nil}
            content:="// @noLib: true\n// @allowJs: true\n// @newLine: crlf\n// @Filename: "+sample.name+"\n"+source
            options:=&FourslashOptions{Capabilities:caps}
            if sample.mapper!="" {
                content="// @Filename: /tsconfig.json\n{\"compilerOptions\":{\"noLib\":true},\"contentMappers\":[{\"package\":\"mapper\",\"extensions\":[\".astro\",\".vue\"]}]}\n// @Filename: /node_modules/mapper/package.json\n"+contentmappertest.PackageJSON(sample.mapper)+"\n// @Filename: "+sample.name+"\n"+source
                options.ContentMapperSpawner=contentmappertest.NewSpawner(); options.RunExternalCode=true
            }
            f,done:=NewFourslashWithOptions(t,content,options)
            func(){defer done(); f.GoToFile(t,sample.name)
                script:=f.getScriptInfo(sample.name)
                response:=f.sendRequest(t,lsproto.TextDocumentCompletionInfo,&lsproto.CompletionParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(sample.name)},Position:f.converters.PositionToLineAndCharacter(script,core.TextPos(position))})
                list:=response.List
                if list!=nil && len(list.Items)>0 {
                    f.sendRequest(t,lsproto.CompletionItemResolveInfo,list.Items[0])
                    if len(list.Items)>1 { f.sendRequest(t,lsproto.CompletionItemResolveInfo,list.Items[len(list.Items)-1]) }
                }
            }()
        }
    }) }
}

func TestCSharpJSDocCompletion(t *testing.T) {
    samples := []struct{name,text,mapper string}{
        {"/unicode.ts", "const 字='😀';\r\n\t/**~ */\r\nfunction 方法(名前:string, $数=1) { return 名前; }", ""},
        {"/parameters.js", "/**~ */\nfunction f({字}, [値], ...$args) { return 値; }", ""},
        {"/returns.ts", "/**~ */\nfunction f(p:number) { const nested=()=>{return 1}; class C { f(){return 2;} }; }", ""},
        {"/nested.ts", "/**~ */\nfunction f(p:number) { try { if(p) {return;} } finally {} }", ""},
        {"/property.ts", "class C {\n\t/**~ */\n\tf=(名前:string)=>名前;\n}", ""},
        {"/constructor.ts", "/**~ */\nconst C=(class {constructor(名前:string) {}});", ""},
        {"/whitespace.ts", "\u200b\u00a0/**~\u3000**/\nfunction 方法(名前:string) {}", ""},
        {"/asterisks.ts", "\t/****~**/\nfunction 方法(名前:string) {}", ""},
        {"/unclosed.ts", "/**~\nfunction 方法(名前:string) {return 名前;}", ""},
        {"/plain.ts", "/** text ~ */\nfunction f(p:number) {}", ""},
        {"/app.astro", "/**~ */\nexport function 方法(名前:string) { return 名前; }", contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro", "// folding-duplicate\n/**~ */\nexport function 方法(名前:string) { return 名前; }", contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro", "/**~ */\nexport function 方法(名前:string) { return 名前; }", contentmappertest.DuplicateProjectionMapper},
        {"/app.vue", "<template>日本語 😀</template>\n<script>\n/**~ */\nexport function 方法(名前:string) {return 名前;}\n</script>", contentmappertest.ComponentMapper},
    }
    for index,sample:=range samples { t.Run(fmt.Sprint(index),func(t *testing.T) {
        position:=strings.Index(sample.text,"~"); source:=strings.Replace(sample.text,"~","",1)
        for mask:=0;mask<8;mask++ {
            caps:=GetDefaultCapabilities()
            caps.TextDocument.Completion.CompletionItem.SnippetSupport=new(mask&1!=0)
            caps.TextDocument.Completion.CompletionItem.InsertReplaceSupport=new(mask&2!=0)
            caps.TextDocument.Completion.CompletionItem.CommitCharactersSupport=new(mask&4!=0)
            content:="// @noLib: true\n// @allowJs: true\n// @Filename: "+sample.name+"\n"+source
            options:=&FourslashOptions{Capabilities:caps}
            if sample.mapper!="" {
                content="// @Filename: /tsconfig.json\n{\"compilerOptions\":{\"noLib\":true},\"contentMappers\":[{\"package\":\"mapper\",\"extensions\":[\".astro\",\".vue\"]}]}\n// @Filename: /node_modules/mapper/package.json\n"+contentmappertest.PackageJSON(sample.mapper)+"\n// @Filename: "+sample.name+"\n"+source
                options.ContentMapperSpawner=contentmappertest.NewSpawner(); options.RunExternalCode=true
            }
            f,done:=NewFourslashWithOptions(t,content,options)
            f.GoToFile(t,sample.name)
            script:=f.getScriptInfo(sample.name)
            for preference:=0;preference<3;preference++ {
                prefs:=lsutil.NewDefaultUserPreferences()
                if preference==1 {prefs.GenerateReturnInDocTemplate=core.TSFalse}
                if preference==2 {prefs.EnableJSDocCompletions=core.TSFalse}
                if index%2==0 {prefs.FormatCodeSettings.NewLineCharacter="\r\n"}
                reset:=f.ConfigureWithReset(t,prefs)
                for _,trigger:=range []*string{nil,new("*")} {
                    context:=&lsproto.CompletionContext{TriggerKind:lsproto.CompletionTriggerKindInvoked}
                    if trigger!=nil {context.TriggerKind=lsproto.CompletionTriggerKindTriggerCharacter;context.TriggerCharacter=trigger}
                    f.sendRequest(t,lsproto.TextDocumentCompletionInfo,&lsproto.CompletionParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(sample.name)},Position:f.converters.PositionToLineAndCharacter(script,core.TextPos(position)),Context:context})
                }
                reset()
            }
            done()
        }
    }) }
}

func TestCSharpDiagnostics(t *testing.T) {
    samples := []struct{name,text,mapper string}{
        {"/unicode.ts", "export {}; const 字 = '😀';\r\nconst 名: number = 字; function 方法(未使用: number) { return; const 値 = 1; }\r\nmissing;", ""},
        {"/deprecated.ts", "/** @deprecated */ interface 表 { /** @deprecated */ 値: number }; let 引用: 表; 引用!.値; function f(unused:number) {}", ""},
        {"/chain.ts", "interface A { field: { inner: number } }; let a: A = { field: { inner: '字' } };", ""},
        {"/style.ts", "// @noUnusedLocals: true\n// @noUnusedParameters: true\n// @noImplicitReturns: true\n// @noFallthroughCasesInSwitch: true\n// @allowUnreachableCode: false\n// @allowUnusedLabels: false\nexport {}; let unused=1; function f(p:number) { if(p) return 1; } switch(1 as number) { case 1: f(1); case 2: break; } label: { f(1); } function g() { return; f(2); }", ""},
        {"/declaration.ts", "// @declaration: true\nexport const outer = () => new class { private hidden = 1; }();", ""},
        {"/bad.js", "// @allowJs: true\nfunction f(public p) {} var ===; var var = int;", ""},
        {"/import.ts", "import\nimport { bar } from './foo'\n// @Filename: /foo.ts\nexport function bar() {return 1;}", ""},
        {"/bad.tsx", "<T extends /=>", ""},
        {"/var.ts", "var export; var foo; var class; var bar;", ""},
        {"/app.astro", "export const 名: number = '😀';\nmissing;", contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro", "// folding-disabled\nexport const 名: number = '😀';", contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro", "export const 名: number = '😀';", contentmappertest.DuplicateProjectionMapper},
        {"/app.vue", "<template><h1>日本語 😀</h1></template>", contentmappertest.SynthesizingMapper},
        {"/app.vue", "export const 名: number = '😀';", contentmappertest.SupplementalDiagnosticsMapper},
        {"/app.vue", "<script>export const 名: number = '😀';</script><p>{{ missing }}</p>", contentmappertest.ComponentMapper},
        {"/app.vue", "<template>日本語 😀</template>", contentmappertest.FailingMapper},
        {"/app.vue", "export function foo() {}", contentmappertest.DiagnosticCodeCollisionMapper},
        {"/app.vue", "const 字 = '😀'; const x = #{broken", contentmappertest.TransformingMapper},
    }
    for index,sample := range samples { t.Run(fmt.Sprint(index),func(t *testing.T) {
        for capability:=0; capability<4; capability++ {
            tags:=[]lsproto.DiagnosticTag{lsproto.DiagnosticTagUnnecessary,lsproto.DiagnosticTagDeprecated}
            if capability==1 {tags=[]lsproto.DiagnosticTag{lsproto.DiagnosticTagUnnecessary}}
            if capability==2 {tags=[]lsproto.DiagnosticTag{lsproto.DiagnosticTagDeprecated}}
            if capability==3 {tags=[]lsproto.DiagnosticTag{}}
            caps:=&lsproto.ClientCapabilities{VSSupportsVisualStudioExtensions:new(capability==2),TextDocument:&lsproto.TextDocumentClientCapabilities{
                Diagnostic:&lsproto.DiagnosticClientCapabilities{RelatedInformation:new(capability%2==0),TagSupport:&lsproto.ClientDiagnosticsTagOptions{ValueSet:tags}},
            }}
            content:="// @noLib: true\n// @strict: true\n// @Filename: "+sample.name+"\n"+sample.text
            options:=&FourslashOptions{Capabilities:caps}
            if sample.mapper!="" {
                content="// @Filename: /tsconfig.json\n{\"compilerOptions\":{\"target\":\"esnext\",\"strict\":true,\"noLib\":true},\"contentMappers\":[{\"package\":\"mapper\",\"extensions\":[\".astro\",\".vue\"]}]}\n// @Filename: /node_modules/mapper/package.json\n"+contentmappertest.PackageJSON(sample.mapper)+"\n// @Filename: "+sample.name+"\n"+sample.text
                options.ContentMapperSpawner=contentmappertest.NewSpawner();options.RunExternalCode=true
            }
            f,done:=NewFourslashWithOptions(t,content,options)
            func() { defer done();f.GoToFile(t,sample.name)
                for pref:=0;pref<3;pref++ {
                    prefs:=lsutil.NewDefaultUserPreferences();if pref==1 {prefs.ReportStyleChecksAsWarnings=core.TSFalse};if pref==2 {prefs.EnableValidation=core.TSFalse}
                    reset:=f.ConfigureWithReset(t,prefs)
                    f.sendRequest(t,lsproto.TextDocumentDiagnosticInfo,&lsproto.DocumentDiagnosticParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(sample.name)}})
                    reset()
                }
            }()
        }
    }) }
}

func TestCSharpAutoInsert(t *testing.T) {
    samples := []string{
        "const 字='😀';\r\nconst x=<組.件>", "const x=<ns:字>", "const x=<$Tag.$Child>",
        "const x=<A><A></A>", "const x=<><></>", "const x=<A>text", "const x=<A/>;",
        "const x=<A></A>", "const x=<A><B></A>", "const x=<A>日本語 😀</B>",
        "const x=<A< string > title='値'>", "const x=<\n/*name*/ 組 /*dot*/ .\n/*child*/ 件\n>",
        "const x=<this.件>", "const x=<ns\n// colon\n:\n字>", "const x=<A></A></A>",
    }
    for index, sample := range samples { t.Run(fmt.Sprint(index),func(t *testing.T) {
        f,done:=NewFourslash(t,nil,"// @noLib: true\n// @Filename: /auto.tsx\n"+sample);defer done()
        script:=f.getScriptInfo("/auto.tsx")
        positions:=[]int{0,len(sample)}
        for offset,ch:=range sample { if ch=='>' { positions=append(positions,offset+1) } }
        for _, enabled:=range []core.Tristate{core.TSUnknown,core.TSTrue,core.TSFalse} {
            prefs:=lsutil.NewDefaultUserPreferences();prefs.EnableAutoClosingTags=enabled;reset:=f.ConfigureWithReset(t,prefs)
            for _,position:=range positions { for _,character:=range []string{">","/"} {
                f.sendRequest(t,lsproto.TextDocumentVSOnAutoInsertInfo,&lsproto.VSOnAutoInsertParams{
                    VSTextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI("/auto.tsx")},
                    VSPosition:f.converters.PositionToLineAndCharacter(script,core.TextPos(position)),VSCh:character})
            } }
            reset()
        }
    }) }
}

func TestCSharpInlayHints(t *testing.T) {
    samples := []struct{name, text, mapper string}{
        {"/unicode.ts", "const 字 = '😀';\r\nfunction 方法(名前:string, 数 = 1) { return 名前; }\r\nlet 値 = 方法('日本語', 2); 方法(/*名前*/ 'a', 3); 方法(値, 4);\r\nenum 色 { 赤, 青 = 5, 緑 };\r\nconst 矢 = (字) => 字;\r\n", ""},
        {"/functions.ts", "interface A { x: number }; const contextual: (value:A, flag?:boolean) => A = (value, flag) => value; const unparenthesized = value => value; function predicate(value: unknown) { return typeof value === 'string'; } class B { field = contextual({x:1}); inferred; constructor() { this.inferred = 1; } get value() { return this.field; } } let b = new B();", ""},
        {"/tuples.ts", "declare function rest(a:number,...values:[second:string,third?:boolean,...remaining:number[]]):void; declare function array(...values:number[]):void; const tuple:[number,string]=[1,'a']; const empty:[]=[]; rest(...tuple, true, 2, 3); rest(...empty, 1, 'b'); array(1,2,3); const values=2; array(values); let x=1; rest(x,'a'); rest((1), ('a'));", ""},
        {"/literals.ts", "declare function f(value:any):void; f(-1); f(+Infinity); f(NaN); f(undefined); f(null); f(true); f(/x/); f(2n); f(`a${1}`); f((null)); const value='a'; f(value); f({value}.value); f(/*value*/ 1); f(/* value * / */ 2); f(\n// value\n3);", ""},
        {"/types.ts", "declare function make(): { readonly prop?: string; method<T>(x:T): T; new(x:number): {a:1}; (x:string): number; [key:string]: unknown }; let value=make(); declare function tuple(): [label: string, optional?:number]; let values=tuple(); declare function union(): 'é' | 'a\\\"b' | -1; let text=union(); declare function template<T>(): `prefix${number}${string}`; let templated=template();", ""},
        {"/modules.ts", "// @Filename: /dep.ts\nexport class 名 {}; export function make() { return new 名(); }\n// @Filename: /modules.ts\nimport { make } from './dep'; import * as namespace from './dep'; let variable = make(); let alias=namespace;", ""},
        {"/casefold.ts", "class K {} class S {} class I {} class Σ {} function makeK() { return new K(); } function makeS() { return new S(); } function makeI() { return new I(); } function makeΣ() { return new Σ(); } let K=makeK(); let ſ=makeS(); let ı=makeI(); let ς=makeΣ();", ""},
        {"/app.astro", "function 方法(値:number) { return 値; }\r\nlet 数 = 方法(1);\r\nenum 色 { 赤, 青 }", contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro", "// folding-duplicate\nfunction 方法(値:number) { return 値; }\r\nlet 数 = 方法(1);", contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro", "// folding-disabled\nfunction 方法(値:number) { return 値; }\r\nlet 数 = 方法(1);", contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro", "function 方法(値:number) { return 値; }\r\nlet 数 = 方法(1);", contentmappertest.DuplicateProjectionMapper},
        {"/app.vue", "<script>\nfunction 方法(値:number) { return 値; }\nlet 数 = 方法(1);\n</script>\n<main>{{ 方法(2) }}</main>", contentmappertest.ComponentMapper},
    }
    all := lsutil.InlayHintsPreferences{IncludeInlayParameterNameHints:lsutil.IncludeInlayParameterNameHintsAll,
        IncludeInlayFunctionParameterTypeHints:core.TSTrue,IncludeInlayVariableTypeHints:core.TSTrue,
        IncludeInlayPropertyDeclarationTypeHints:core.TSTrue,IncludeInlayFunctionLikeReturnTypeHints:core.TSTrue,IncludeInlayEnumMemberValueHints:core.TSTrue}
    literals := all; literals.IncludeInlayParameterNameHints=lsutil.IncludeInlayParameterNameHintsLiterals
    matching := all; matching.IncludeInlayParameterNameHintsWhenArgumentMatchesName=core.TSTrue; matching.IncludeInlayVariableTypeHintsWhenTypeMatchesName=core.TSTrue
    for index, sample := range samples { t.Run(fmt.Sprint(index),func(t *testing.T) {
        content := "// @noLib: true\n// @target: esnext\n// @strict: true\n// @Filename: "+sample.name+"\n"+sample.text
        options := &FourslashOptions{}
        if sample.mapper != "" {
            content = "// @Filename: /tsconfig.json\n{\"compilerOptions\":{\"target\":\"esnext\",\"strict\":true,\"noLib\":true},\"contentMappers\":[{\"package\":\"mapper\",\"extensions\":[\".astro\",\".vue\"]}]}\n// @Filename: /node_modules/mapper/package.json\n"+contentmappertest.PackageJSON(sample.mapper)+"\n// @Filename: "+sample.name+"\n"+sample.text
            options.ContentMapperSpawner = contentmappertest.NewSpawner(); options.RunExternalCode = true
        }
        f,done:=NewFourslashWithOptions(t,content,options);defer done();f.GoToFile(t,sample.name)
        for _, pref := range []lsutil.InlayHintsPreferences{{}, all, literals, matching,
            {IncludeInlayFunctionParameterTypeHints:core.TSTrue},{IncludeInlayVariableTypeHints:core.TSTrue},
            {IncludeInlayPropertyDeclarationTypeHints:core.TSTrue},{IncludeInlayFunctionLikeReturnTypeHints:core.TSTrue},{IncludeInlayEnumMemberValueHints:core.TSTrue},
        } {
            prefs := lsutil.NewDefaultUserPreferences(); prefs.InlayHints = pref
            if index%2==0 { prefs.QuotePreference=lsutil.QuotePreferenceSingle }
            reset := f.ConfigureWithReset(t, prefs)
            script := f.getScriptInfo(sample.name)
            length:=len(script.content)
            for _, span := range [][2]int{{0,length},{length/2,length},{length/2,length/2}} {
                rng:=lsproto.Range{Start:f.converters.PositionToLineAndCharacter(script,core.TextPos(span[0])),End:f.converters.PositionToLineAndCharacter(script,core.TextPos(span[1]))}
                f.sendRequest(t,lsproto.TextDocumentInlayHintInfo,&lsproto.InlayHintParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(sample.name)},Range:rng})
            }
            reset()
        }
    }) }
}

func TestCSharpCodeLens(t *testing.T) {
    samples := []struct{name, text, mapper string}{
        {"/unicode.ts", "const 字 = '😀';\r\nexport function 方法(値:number):number;\r\nexport function 方法(値:any) { return 値; }\r\nfunction 葉() { return 方法(1); }\r\nexport const 矢 = () => 方法(2);\r\n方法(3); 矢();", ""},
        {"/class.ts", "interface 表 { 方法(): void; 名: string }\nabstract class 基 { abstract 方法():void; abstract 名:string; static 零() {} }\nclass 派 extends 基 implements 表 { 方法() {} 名 = '😀'; private 隠(){} #秘密(){} get 字(){ return this.名; } }\nconst 値 = new 派(); 値.方法(); type 型 = { 方法():void }; enum 色 { 赤, 青 }; 色.赤;", ""},
        {"/app.astro", "export function 方法() { return 葉(); }\r\nfunction 葉() {}\r\n方法();", contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro", "// folding-duplicate\nexport function 方法() { return 葉(); }\r\nfunction 葉() {}\r\n方法();", contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro", "// folding-disabled\nexport function 方法() { return 葉(); }\r\nfunction 葉() {}\r\n方法();", contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro", "export function 方法() { return 葉(); }\r\nfunction 葉() {}\r\n方法();", contentmappertest.DuplicateProjectionMapper},
        {"/app.vue", "<script>\nexport function 方法() { return 葉(); }\nfunction 葉() {}\n方法();\n</script>\n<main>{{ 方法() }}</main>", contentmappertest.ComponentMapper},
    }
    for index, sample := range samples { t.Run(fmt.Sprint(index),func(t *testing.T) {
        content := "// @noLib: true\n// @Filename: "+sample.name+"\n"+sample.text
        options := &FourslashOptions{}
        if sample.mapper != "" {
            content = "// @Filename: /tsconfig.json\n{\"compilerOptions\":{\"target\":\"esnext\",\"noLib\":true},\"contentMappers\":[{\"package\":\"mapper\",\"extensions\":[\".astro\",\".vue\"]}]}\n// @Filename: /node_modules/mapper/package.json\n"+contentmappertest.PackageJSON(sample.mapper)+"\n// @Filename: "+sample.name+"\n"+sample.text
            options.ContentMapperSpawner = contentmappertest.NewSpawner(); options.RunExternalCode = true
        }
        f,done:=NewFourslashWithOptions(t,content,options);defer done();f.GoToFile(t,sample.name)
        for _, pref := range []lsutil.CodeLensUserPreferences{
            {}, {ReferencesCodeLensEnabled:core.TSTrue},
            {ReferencesCodeLensEnabled:core.TSTrue,ImplementationsCodeLensEnabled:core.TSTrue,ReferencesCodeLensShowOnAllFunctions:core.TSTrue,ImplementationsCodeLensShowOnInterfaceMethods:core.TSTrue,ImplementationsCodeLensShowOnAllClassMethods:core.TSTrue},
        } {
            prefs := lsutil.NewDefaultUserPreferences(); prefs.CodeLens = pref
            reset := f.ConfigureWithReset(t, prefs)
            response:=f.sendRequest(t,lsproto.TextDocumentCodeLensInfo,&lsproto.CodeLensParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(sample.name)}})
            if response.CodeLenses != nil { for _, lens:=range *response.CodeLenses { f.sendRequest(t,lsproto.CodeLensResolveInfo,lens) } }
            reset()
        }
    }) }
}

func TestCSharpCallHierarchy(t *testing.T) {
    samples := []struct{name, text, mapper string; names []string}{
        {"/unicode.ts", "const 字 = '😀';\r\nexport function 方法(値 = () => 葉()) { return 葉(); }\r\nfunction 葉() {}\r\nconst 矢 = () => 方法();\r\n矢(); 方法(); (方法)();", "", []string{"方法","葉","矢","字"}},
        {"/class.ts", "function 飾(...args:any[]) {}\nfunction 値() { return 'key'; }\n@飾 class 表 { @飾 [値()]() { this.方法(); }\n 方法() { this.#秘密(); } #秘密() {}\n get 名() { return 値(); } set 名(v) { 値(); }\n static { 値(); } property = () => this.方法(); }\nconst obj = new 表(); obj.方法(); obj.名; obj[値()]();", "", []string{"飾","表","方法","秘密","名","値","static"}},
        {"/ambient.ts", "declare function 関数(x:number):void;\n// separate overload\ndeclare function 関数(x:string):void;\ninterface 形 { 方法():void; }\ninterface 形 { 方法(x:number):void; }\nlet 形:形; 関数(1); 形.方法();\nnamespace 空 { export const 矢 = () => 関数('x'); } 空.矢();", "", []string{"関数","方法","矢","空"}},
        {"/app.astro", "export function 方法() { return 葉(); }\r\nfunction 葉() {}\r\n方法();", contentmappertest.PrefixedSupplementalMapper, []string{"方法","葉"}},
        {"/folding-duplicate.astro", "export function 方法() { return 葉(); }\r\nfunction 葉() {}\r\n方法();", contentmappertest.PrefixedSupplementalMapper, []string{"方法","葉"}},
        {"/folding-disabled.astro", "export function 方法() { return 葉(); }\r\nfunction 葉() {}\r\n方法();", contentmappertest.PrefixedSupplementalMapper, []string{"方法","葉"}},
        {"/app.astro", "export function 方法() { return 葉(); }\r\nfunction 葉() {}\r\n方法();", contentmappertest.DuplicateProjectionMapper, []string{"方法","葉"}},
        {"/app.vue", "<script>\nexport function 方法() { return 葉(); }\nfunction 葉() {}\n方法();\n</script>\n<main>{{ 方法() }}</main>", contentmappertest.ComponentMapper, []string{"方法","葉"}},
    }
    for index, sample := range samples { t.Run(fmt.Sprint(index),func(t *testing.T) {
        content := "// @noLib: true\n// @Filename: "+sample.name+"\n"+sample.text
        options := &FourslashOptions{}
        if sample.mapper != "" {
            content = "// @Filename: /tsconfig.json\n{\"compilerOptions\":{\"target\":\"esnext\",\"noLib\":true},\"contentMappers\":[{\"package\":\"mapper\",\"extensions\":[\".astro\",\".vue\"]}]}\n// @Filename: /node_modules/mapper/package.json\n"+contentmappertest.PackageJSON(sample.mapper)+"\n// @Filename: "+sample.name+"\n"+sample.text
            options.ContentMapperSpawner = contentmappertest.NewSpawner(); options.RunExternalCode = true
        }
        f,done:=NewFourslashWithOptions(t,content,options);defer done();f.GoToFile(t,sample.name)
        script:=f.getScriptInfo(sample.name)
        positions:=[]int{0,len(script.content)}
        for _, name:=range sample.names { for from:=0;from<len(script.content); {
            offset:=strings.Index(script.content[from:],name);if offset<0 {break};offset+=from
            positions=append(positions,offset);from=offset+len(name)
        } }
        items:=[]*lsproto.CallHierarchyItem{}
        for _, offset:=range positions {
            params:=&lsproto.CallHierarchyPrepareParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(sample.name)},Position:f.converters.PositionToLineAndCharacter(script,core.TextPos(offset))}
            response:=f.sendRequest(t,lsproto.TextDocumentPrepareCallHierarchyInfo,params)
            if response.CallHierarchyItems!=nil {items=append(items,(*response.CallHierarchyItems)...)}
        }
        seen:=map[string]bool{}
        for len(items)!=0 {
            item:=items[0];items=items[1:];key:=fmt.Sprintf("%s:%v",item.Uri,item.SelectionRange);if seen[key] {continue};seen[key]=true
            incoming:=f.sendRequest(t,lsproto.CallHierarchyIncomingCallsInfo,&lsproto.CallHierarchyIncomingCallsParams{Item:item})
            outgoing:=f.sendRequest(t,lsproto.CallHierarchyOutgoingCallsInfo,&lsproto.CallHierarchyOutgoingCallsParams{Item:item})
            if incoming.CallHierarchyIncomingCalls!=nil {for _,call:=range *incoming.CallHierarchyIncomingCalls {items=append(items,call.From)}}
            if outgoing.CallHierarchyOutgoingCalls!=nil {for _,call:=range *outgoing.CallHierarchyOutgoingCalls {items=append(items,call.To)}}
        }
    }) }
}

func TestCSharpFileRename(t *testing.T) {
    samples := []struct{content string; moves [][2]string}{
        {"// @Filename: /tsconfig.json\n{\"files\":[\"源/a.ts\",\"使用.ts\"],\"include\":[\"源/*.ts\"],\"exclude\":[\"源/排除\"],\"compilerOptions\":{\"noLib\":true,\"rootDirs\":[\"源\"],\"outDir\":\"源/out\",\"paths\":{\"別/*\":[\"源/*\"]}}}\n// @Filename: /源/a.ts\nexport const 名前 = '😀';\r\n// @Filename: /使用.ts\n/// <reference path=\"./源/a.ts\" />\r\nimport { 名前 } from './源/a'; import './源/missing'; 名前;", [][2]string{{"/源/a.ts","/源/新.ts"},{"/源","/新しい"},{"/使用.ts","/別/使用.ts"},{"/absent.ts","/new.ts"}}},
        {"// @Filename: /tsconfig.json\n{\"include\": [\"dir/*.ts\"]}\n// @Filename: /dir/a.ts\nexport const 名前 = '😀';\n// @Filename: /dir/b.ts\nimport { 名前 } from './a.js'; import('./a.js'); 名前;", [][2]string{{"/dir/a.ts","/outside/a.ts"},{"/dir/b.ts","/outside/b.ts"}}},
        {"// @allowArbitraryExtensions: true\n// @Filename: /app.d.css.ts\ndeclare const 名前: string; export default 名前;\n// @Filename: /app.css\nbody {}\n// @Filename: /a.ts\nimport 名前 from './app.css';", [][2]string{{"/app.d.css.ts","/新.d.css.ts"},{"/app.css","/新.css"}}},
        {"// @useCaseSensitiveFileNames: false\n// @Filename: /a/kkkk/x.ts\nexport const 名前 = '😀';\n// @Filename: /a/use.ts\nimport { 名前 } from './kkkk/x'; 名前;", [][2]string{{"/a/KKKK","/a/new"},{"/a/KKKK/","/a/new/"}}},
    }
    for _, sample := range samples { for _, documents := range []bool{true,false} {
        caps:=&lsproto.ClientCapabilities{Workspace:&lsproto.WorkspaceClientCapabilities{WorkspaceEdit:&lsproto.WorkspaceEditClientCapabilities{DocumentChanges:&documents}}}
        f,done:=NewFourslash(t,caps,sample.content)
        func(){defer done();for _, move:=range sample.moves {
            params:=&lsproto.RenameFilesParams{Files:[]*lsproto.FileRename{{OldUri:lsconv.FileNameToDocumentURI(move[0]),NewUri:lsconv.FileNameToDocumentURI(move[1])}}}
            response,result,ok:=f.client.SendRequest(t,lsproto.WorkspaceWillRenameFilesInfo,params)
            if !ok || response.AsResponse().Error!=nil {t.Fatalf("file rename failed: %v",response.AsResponse().Error)}
            f.csharpRecordSyntax(t,lsproto.WorkspaceWillRenameFilesInfo,params,result,response.AsResponse().Result)
        }}()
    }}
}

func TestCSharpRename(t *testing.T) {
    samples := []string{
        "// @strict: true\n// @Filename: /unicode.ts\nconst /*local*/名前 = '😀'; const 対象 = { /*shorthand*/名前 }; 対象./*property*/名前;\r\nconst { /*binding*/名前: 別名 } = 対象;\r\n/*label*/外: for (;;) { break /*jump*/外; }\r\n/*keyword*/string; /*missing*/未定義; /*eof*/",
        "// @Filename: /a.ts\nexport const /*declaration*/名前 = '😀';\n// @Filename: /b.ts\nimport { /*import*/名前 } from './a'; export { /*export*/名前 }; 名前/*use*/;\n// @Filename: /c.ts\nimport { 名前 } from './b'; /*alias*/名前;",
        "// @Filename: /numeric.ts\ninterface 表 { /*numeric*/0: string; '名前': string; } let 値: 表; 値[/*access*/0]; 値[/*string*/'名前']; let 字: '赤' | '青'; 字 = /*literal*/'赤';\nconst 偽 = `/*template*/赤`;",
        "// @Filename: /lib.ts\nconst 値: /*library*/Array<string> = []; class 表 { #名前 = 1; 方法() { return this./*private*/#名前; } }\n",
        "// @allowJs: true\n// @Filename: /common.js\nconst /*value*/名前 = 1; module.exports = { /*export*/名前 };\n",
    }
    for _, content := range samples { for _, aliases := range []core.Tristate{core.TSTrue,core.TSFalse} {
        f,done := NewFourslash(t,nil,content)
        func(){defer done(); defer f.ConfigureWithReset(t,lsutil.UserPreferences{UseAliasesForRename:aliases,QuotePreference:"single"})()
            for _, marker := range f.Markers() {
                doc := lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(marker.fileName)}
                prepare := &lsproto.PrepareRenameParams{TextDocument:doc,Position:marker.LSPosition}
                response,result,_ := f.client.SendRequest(t,lsproto.TextDocumentPrepareRenameInfo,prepare)
                f.csharpRecordSyntax(t,lsproto.TextDocumentPrepareRenameInfo,prepare,result,response.AsResponse().Result,response.AsResponse().Error)
                params := &lsproto.RenameParams{TextDocument:doc,Position:marker.LSPosition,NewName:"新しい名前"}
                renameResponse,renamed,_ := f.client.SendRequest(t,lsproto.TextDocumentRenameInfo,params)
                f.csharpRecordSyntax(t,lsproto.TextDocumentRenameInfo,params,renamed,renameResponse.AsResponse().Result,renameResponse.AsResponse().Error)
            }
        }()
    }}
}

func TestCSharpHighlights(t *testing.T) {
    samples := []string{
        "// @Filename: /unicode.ts\nlet /*value*/名前 = '😀'; 名前 = 'é';\r\nasync function 方法(値: boolean) { /*if*/if (値) { /*return*/return 名前; } /*else*/else if (名前) { /*throw*/throw 名前; } else { return 'é'; } }\r\n/*label*/外: /*for*/for (;;) { while (true) { /*break*/break 外; } /*continue*/continue; }\r\n/*do*/do { 名前 += 'é'; } /*while*/while (false);\r\n/*eof*/",
        "// @Filename: /flow.ts\n/*async*/async function 方法() { /*await*/await 1; try { /*inside*/throw 1; } /*catch*/catch (e) { /*escape*/throw e; } /*finally*/finally { await 2; } return 1; }\nfunction* 生産() { /*yield*/yield 1; yield 2; function* 入れ子() { yield 3; } }\n/*switch*/switch (1) { /*case*/case 1: /*stop*/break; /*default*/default: break; }\nif (1) {} /*elseComment*/else /* comment */ if (2) {} else\nif (3) {}\nif (1) {} else\u00a0/*nbsp*/if (2) {}",
        "// @Filename: /modifiers.ts\n/*abstract*/abstract class 表 { abstract 方法(): void; /*public*/public 名前 = 1; private 字 = 2; constructor(public 別: string) {} /*get*/get 値() { return this.名前; } set 値(v) { this.名前 = v; } }\nclass 子 extends 表 { /*override*/override 方法() {} }\n// @Filename: /view.tsx\nconst /*element*/表 = (p: any) => p; let node = </*open*/表>😀</*close*/表>;\n",
        "// @Filename: /a.ts\nexport let 名前 = '😀';\n// @Filename: /b.ts\nimport { 名前 } from './a'; /*use*/名前;\n// @Filename: /c.ts\nimport { 名前 } from './a'; 名前 = 'é';",
        "// @Filename: /empty.ts\n/*eof*/",
    }
    for _, content := range samples {
        f,done := NewFourslash(t,nil,content)
        func(){defer done();for _, marker := range f.Markers() {
            doc := lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(marker.fileName)}
            params := &lsproto.DocumentHighlightParams{TextDocument:doc,Position:marker.LSPosition}
            response,result,ok := f.client.SendRequest(t,lsproto.TextDocumentDocumentHighlightInfo,params)
            if !ok || response.AsResponse().Error != nil {t.Fatalf("highlights failed: %v",response.AsResponse().Error)}
            f.csharpRecordSyntax(t,lsproto.TextDocumentDocumentHighlightInfo,params,result,response.AsResponse().Result)
            for _, files := range [][]lsproto.DocumentUri{nil,{doc.Uri,doc.Uri,"file:///missing.ts","file:///a.ts","file:///b.ts","file:///c.ts"}} {
                multi := &lsproto.MultiDocumentHighlightParams{TextDocument:doc,Position:marker.LSPosition,FilesToSearch:files}
                response,result,ok := f.client.SendRequest(t,lsproto.CustomTextDocumentMultiDocumentHighlightInfo,multi)
                if !ok || response.AsResponse().Error != nil {t.Fatalf("multi highlights failed: %v",response.AsResponse().Error)}
                f.csharpRecordSyntax(t,lsproto.CustomTextDocumentMultiDocumentHighlightInfo,multi,result,response.AsResponse().Result)
            }
        }}()
    }
}

func TestCSharpVisualStudioReferences(t *testing.T) {
    samples := []string{
        "// @Filename: /unicode.ts\ninterface 表 { /*member*/名前: string; }\r\nconst 名前 = '😀'; const 対象: 表 = { /*shorthand*/名前 }; 対象./*property*/名前 = 'é';\r\nlet /*variable*/値 = 1; 値++; ({x:値} = {x:2});\r\n/*label*/外: for (;;) { break /*jump*/外; }\r\ntype 字 = /*keyword*/string; type 別 = string; const 種: /*literal*/'😀' = '😀';\r\n/*eof*/",
        "// @Filename: /a.ts\nexport class 表 { 名前 = 1; } export namespace 表 { export type 字 = string; }\n// @Filename: /b.ts\nimport { /*import*/表 as 別 } from './a'; export { 別 as /*export*/表 }; const 値 = new /*use*/別();",
        "// @Filename: /empty.ts\n/*eof*/",
    }
    for _, content := range samples { for _, classified := range []bool{false,true} {
        f,done := NewFourslash(t,&lsproto.ClientCapabilities{VSSupportsVisualStudioExtensions:&classified},content)
        func(){defer done();for _, marker := range f.Markers() { for _, include := range []bool{false,true} {
            params := &lsproto.ReferenceParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(marker.fileName)},Position:marker.LSPosition,Context:&lsproto.ReferenceContext{IncludeDeclaration:include}}
            response,result,ok := f.client.SendRequest(t,lsproto.TextDocumentVSReferencesInfo,params)
            if !ok || response.AsResponse().Error != nil {t.Fatalf("VS references failed: %v",response.AsResponse().Error)}
            f.csharpRecordSyntax(t,lsproto.TextDocumentVSReferencesInfo,params,result,response.AsResponse().Result)
        }}}()
    }}
}

func TestCSharpReferences(t *testing.T) {
    samples := []string{
        "// @strict: true\n// @Filename: /unicode.ts\ninterface 表 { /*member*/名前: string; }\r\nconst 名前 = '😀'; const 対象: 表 = { /*shorthand*/名前 }; 対象./*property*/名前;\r\nclass 子 implements /*type*/表 { constructor(public /*parameter*/名前: string) {} }\r\nconst 字 = new 子('é'); 字.名前;\r\n/*label*/外: for (;;) { break /*jump*/外; }\r\n/*eof*/",
        "// @Filename: /a.ts\nexport const 名前 = '😀'; export interface 表 { 文字: string; }\n// @Filename: /b.ts\nimport { /*import*/名前 as 別名, 表 } from './a'; export { 別名 as /*export*/名前 }; const 字: 表 = {文字:別名}; 別名/*use*/;\n// @Filename: /c.ts\nimport { 名前 } from './b'; /*alias*/名前;",
        "// @allowSyntheticDefaultImports: true\n// @Filename: /value.ts\nconst /*value*/名前 = 1; export = 名前;\n// @Filename: /reexport.ts\nexport { /*default*/default } from './value';\n// @Filename: /use.ts\nimport /*import*/名前 from './reexport'; 名前;",
        "// @Filename: /links.ts\nnamespace 空 { export class 表 { 名前 = class { 方法() {} }; } }\n/** {@link 空.表#名前.方法} {@link 空.表#名前#方法} */\nfunction 説明() {}\n空./*type*/表;\n/*keyword*/string; /*unknown*/未定義;",
        "// @Filename: /empty.ts\n/*eof*/",
    }
    for _, content := range samples { for _, links := range []bool{false,true} {
        caps := &lsproto.ClientCapabilities{TextDocument:&lsproto.TextDocumentClientCapabilities{Implementation:&lsproto.ImplementationClientCapabilities{LinkSupport:&links}}}
        f,done := NewFourslash(t,caps,content)
        func(){defer done();for _, marker := range f.Markers() {
            doc := lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(marker.fileName)}
            for _, include := range []bool{false,true} {
                params := &lsproto.ReferenceParams{TextDocument:doc,Position:marker.LSPosition,Context:&lsproto.ReferenceContext{IncludeDeclaration:include}}
                response,result,ok := f.client.SendRequest(t,lsproto.TextDocumentReferencesInfo,params)
                if !ok || response.AsResponse().Error != nil {t.Fatalf("references failed: %v",response.AsResponse().Error)}
                f.csharpRecordSyntax(t,lsproto.TextDocumentReferencesInfo,params,result,response.AsResponse().Result)
            }
            params := &lsproto.ImplementationParams{TextDocument:doc,Position:marker.LSPosition}
            response,result,ok := f.client.SendRequest(t,lsproto.TextDocumentImplementationInfo,params)
            if !ok || response.AsResponse().Error != nil {t.Fatalf("implementation failed: %v",response.AsResponse().Error)}
            f.csharpRecordSyntax(t,lsproto.TextDocumentImplementationInfo,params,result,response.AsResponse().Result)
        }}()
    }}
}

func TestCSharpDefinitions(t *testing.T) {
    samples := []string{
        "// @strict: true\n// @Filename: /unicode.ts\ninterface 表 { 名: string; }\r\nfunction 方法(値: 表): 表 { return 値; }\r\nconst 字 = '😀'; /*call*/方法({/*property*/名: 字});\r\nconst 対象 = 方法({名: 字}); 対/*value*/象.名; let 列: Array<表>; 列/*array*/;\r\nlet { /*binding*/名 } = 対象; let shorthand: 表 = { /*shorthand*/名 };\r\n",
        "// @Filename: /a.ts\nexport const 名 = '😀'; export interface 表 { 名: string; }\n// @Filename: /b.ts\nimport { /*import*/名, 表 } from './a'; const 字: 表 = {名}; 字/*value*/; 名/*use*/; import('./a'/*module*/);",
        "// @Filename: /keywords.ts\nclass 元 { 方法(): void {} } class 子 extends 元 { /*override*/override 方法(): void { /*return*/return; } }\n/*label*/外: for (;;) { break /*jump*/外; }\nswitch (1) { /*case*/case 1: break; /*default*/default: break; }\n",
        "// @allowArbitraryExtensions: true\n// @Filename: /component.d.vue.ts\nexport declare const 名: string;\n// @Filename: /component.d.vue.js\nexport const 名 = '😀';\n// @Filename: /entry.ts\nimport { 名 } from './component.vue'; 名/*arbitrary*/;",
        "// @Filename: /dir/index.ts\nexport const 名 = '😀';\n// @Filename: /dir/use.ts\nimport { 名 } from '.'/*relative*/; import('.'/*dynamic*/);",
    }
    for _, content := range samples { for _, links := range []bool{false,true} {
        caps := &lsproto.ClientCapabilities{TextDocument:&lsproto.TextDocumentClientCapabilities{
            Definition:&lsproto.DefinitionClientCapabilities{LinkSupport:&links},TypeDefinition:&lsproto.TypeDefinitionClientCapabilities{LinkSupport:&links}}}
        f,done := NewFourslash(t,caps,content)
        func(){defer done();for _, marker := range f.Markers() {
            params := &lsproto.DefinitionParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(marker.fileName)},Position:marker.LSPosition}
            response,result,ok := f.client.SendRequest(t,lsproto.TextDocumentDefinitionInfo,params)
            if !ok || response.AsResponse().Error != nil {t.Fatalf("definition failed: %v",response.AsResponse().Error)}
            f.csharpRecordSyntax(t,lsproto.TextDocumentDefinitionInfo,params,result,response.AsResponse().Result)
            typeParams := &lsproto.TypeDefinitionParams{TextDocument:params.TextDocument,Position:params.Position}
            typeResponse,typeResult,typeOk := f.client.SendRequest(t,lsproto.TextDocumentTypeDefinitionInfo,typeParams)
            if !typeOk || typeResponse.AsResponse().Error != nil {t.Fatalf("type definition failed: %v",typeResponse.AsResponse().Error)}
            f.csharpRecordSyntax(t,lsproto.TextDocumentTypeDefinitionInfo,typeParams,typeResult,typeResponse.AsResponse().Result)
            sourceParams := &lsproto.TextDocumentPositionParams{TextDocument:params.TextDocument,Position:params.Position}
            sourceResponse,sourceResult,sourceOk := f.client.SendRequest(t,lsproto.CustomTextDocumentSourceDefinitionInfo,sourceParams)
            if !sourceOk || sourceResponse.AsResponse().Error != nil {t.Fatalf("source definition failed: %v",sourceResponse.AsResponse().Error)}
            f.csharpRecordSyntax(t,lsproto.CustomTextDocumentSourceDefinitionInfo,sourceParams,sourceResult,sourceResponse.AsResponse().Result)
        }}()
    }}
}

func TestCSharpSignatureRecovery(t *testing.T) {
    for _, content := range []string{
        "function f<T>(x:T){}; f< )/*cursor*/ (",
        "function f<T>(x:T){}; f< ]/*cursor*/ [",
        "function f<T>(x:T){}; f< }/*cursor*/ {",
    } {
        f,done := NewFourslash(t,nil,"// @noLib: true\n// @Filename: /recovery.ts\n"+content)
        func(){defer done();marker:=f.MarkerByName(t,"cursor")
            params:=&lsproto.SignatureHelpParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(marker.fileName)},Position:marker.LSPosition}
            response,result,ok:=f.client.SendRequest(t,lsproto.TextDocumentSignatureHelpInfo,params)
            if !ok || response.AsResponse().Error!=nil {t.Fatalf("signature recovery failed: %v",response.AsResponse().Error)}
            f.csharpRecordSyntax(t,lsproto.TextDocumentSignatureHelpInfo,params,result,response.AsResponse().Result)
        }()
    }
}

func TestCSharpSignatureHelp(t *testing.T) {
    samples := []string{
        "// @strict: true\n// @Filename: /unicode.ts\ninterface 表 { 名: string; }\r\n/** 説明 {@link 表} 😀.\r\n * @param 値 - 入力 {@linkcode 表}\r\n */\r\nfunction 方法<型 extends 表 = 表>(値: 型, ...残り: [字: string, 数?: number]): 型 { return 値; }\r\n方法(/*first*/{名:'😀'}, /*second*/'é', /*third*/2);\r\n方法</*type*/表>({名:'é'}, '字');\r\ndeclare function 中(...値: [名: string, ...数: number[], 終: boolean]): void;\r\n中('😀', /*middle*/1, true);\r\ndeclare function 無(): void; 無(/*none*/);\r\n",
        "// @strict: true\n// @Filename: /callback.ts\ntype 処理 = (名: string, 𐀀: number) => boolean;\nconst callback: 処理 = (/*callback*/名, 𐀀) => true;\ninterface 箱<型 extends string = string> {値:型;} let a: 箱</*type*/\n",
        "// @allowJs: true\n// @Filename: /unicode.js\n/** 説明 😀.\n * @param {string} 名 - 入力.\n */\nfunction 方法(名) { return 名; }\n方法(/*call*/'é');",
        "// @allowJs: true\n// @Filename: /fallback.js\nconst 定義 = { ['方法'](名) { return 名; } };\nlet 未知; 未知.方法(/*fallback*/'😀');",
    }
    for _, content := range samples {
        for _, markdown := range []bool{false,true} { for _, vs := range []bool{false,true} {
            for _, active := range []bool{false,true} { for _, noActive := range []bool{false,true} {
                format:=lsproto.MarkupKindPlainText;if markdown {format=lsproto.MarkupKindMarkdown}
                caps:=&lsproto.ClientCapabilities{VSSupportsVisualStudioExtensions:&vs,TextDocument:&lsproto.TextDocumentClientCapabilities{
                    SignatureHelp:&lsproto.SignatureHelpClientCapabilities{ContextSupport:new(true),SignatureInformation:&lsproto.ClientSignatureInformationOptions{
                        DocumentationFormat:&[]lsproto.MarkupKind{format},ActiveParameterSupport:&active,NoActiveParameterSupport:&noActive}}}}
                f,done:=NewFourslash(t,caps,content)
                func(){defer done();for _,marker:=range f.Markers(){
                    contexts:=[]*lsproto.SignatureHelpContext{nil,
                        {TriggerKind:lsproto.SignatureHelpTriggerKindInvoked},
                        {TriggerKind:lsproto.SignatureHelpTriggerKindTriggerCharacter,TriggerCharacter:new("(")},
                        {TriggerKind:lsproto.SignatureHelpTriggerKindContentChange,IsRetrigger:true}}
                    for _,context:=range contexts {
                        params:=&lsproto.SignatureHelpParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(marker.fileName)},Position:marker.LSPosition,Context:context}
                        response,result,ok:=f.client.SendRequest(t,lsproto.TextDocumentSignatureHelpInfo,params)
                        if !ok || response.AsResponse().Error!=nil {t.Fatalf("signature help failed: %v",response.AsResponse().Error)}
                        f.csharpRecordSyntax(t,lsproto.TextDocumentSignatureHelpInfo,params,result,response.AsResponse().Result)
                    }
                }}()
            }}
        }}
    }
}

func TestCSharpHover(t *testing.T) {
    samples := []string{
        "// @Filename: /unicode.ts\ninterface 表 { 名: string; 次?: 表; }\r\nconst /*value*/値: 表 = { 名: '😀' };\r\n/** 説明 {@link 値}.\r\n * @param 引数 - 値 {@linkcode 表}\r\n * @returns 引数\r\n */\r\nfunction /*function*/方法(引数: 表): 表 { return 引数; }\r\n方法(/*argument*/値)./*property*/名;",
        "// @allowJs: true\n// @Filename: /unicode.js\n/** 名前 {@link https://example.org|外部}. */\nclass 表 { constructor() { this.名 = '😀'; }\n/** 説明\n * @param {string} 値 - `{@link 表}`\n */\n/*method*/方法(値) { return 値; } }\nconst /*value*/値 = new 表();\n値./*call*/方法('é');\n値./*property*/名;",
    }
    for _, content := range samples {
        for _, markdown := range []bool{false,true} {
            for _, vs := range []bool{false,true} {
                for _, maximum := range []int{30,500} {
                    format := lsproto.MarkupKindPlainText; if markdown { format = lsproto.MarkupKindMarkdown }
                    supportsVerbosity := maximum == 500
                    caps := &lsproto.ClientCapabilities{VSSupportsVisualStudioExtensions:&vs,
                        Experimental:&lsproto.ExperimentalClientCapabilities{HoverVerbosityLevel:&supportsVerbosity},
                        TextDocument:&lsproto.TextDocumentClientCapabilities{Hover:&lsproto.HoverClientCapabilities{ContentFormat:&[]lsproto.MarkupKind{format}}}}
                    f,done := NewFourslash(t,caps,content)
                    func(){ defer done()
                        prefs:=lsutil.NewDefaultUserPreferences();prefs.MaximumHoverLength=maximum;f.Configure(t,prefs)
                        for _, marker := range f.Markers() {
                            for _, level := range []int32{0,1,2} {
                                params:=&lsproto.HoverParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(marker.fileName)},Position:marker.LSPosition,VerbosityLevel:&level}
                                response,result,ok:=f.client.SendRequest(t,lsproto.TextDocumentHoverInfo,params)
                                if !ok || response.AsResponse().Error!=nil || result.Hover==nil {t.Fatalf("hover failed: %v",response.AsResponse().Error)}
                                f.csharpRecordSyntax(t,lsproto.TextDocumentHoverInfo,params,result)
                            }
                        }
                    }()
                }
            }
        }
    }
}

func TestCSharpWorkspaceSymbols(t *testing.T) {
    samples := []string{
        "// @Filename: /symbols.ts\nconst 名 = '😀'; function 方法(引数) { const 葉 = class Named {}; return 葉; }\r\nnamespace 空.間 { export const 字 = 'é'; } namespace 空.間 { export class 表 {} }\nfunction overload(a: string): void; function overload(a: number): void; function overload(a: any) {}",
        "// @allowJs: true\n// @Filename: /symbols.js\nfunction Foo() { this.名 = 1; this['字'] = 2; } Foo.prototype.葉 = function named() {}; Foo.表 = class Named {}; exports.方法 = 1; module.exports.星 = 2; Object.defineProperty(Foo, '除外', {value: 1});",
        "// @Filename: /symbols.ts\nconst Aa = 1, AA = 2, aA = 3, ab = 4, AlphaBeta = 5, A_B = 6, İ = 7, ı = 8, I = 9, i = 0, Σ = 1, σ = 2, ς = 3, K = 1, k = 2, 𐐀 = 1, 𐐨 = 2; class C { ['literal'] = 1; [Symbol.iterator]() {} constructor(public 名: string, readonly 字: number) {} }",
        "// @Filename: /symbols.ts\n"+strings.Repeat("const a = 1;\n",270),
    }
    for _, sample := range samples {
        f,done:=NewFourslash(t,nil,sample)
        func(){defer done();for _,query:=range []string{"","a","A","ab","AB","i","İ","Σ","σ","𐐨","名","zz"} {
            params:=&lsproto.WorkspaceSymbolParams{Query:query}
            response,result,ok:=f.client.SendRequest(t,lsproto.WorkspaceSymbolInfo,params)
            if !ok || response.AsResponse().Error!=nil {t.Fatalf("workspace symbols failed: %v",response.AsResponse().Error)}
            f.csharpRecordSyntax(t,lsproto.WorkspaceSymbolInfo,params,result)
        }}()
    }
    for _, disabled := range []bool{false,true} {
        content:=fmt.Sprintf(`// @Filename: /tsconfig.json
{"files":[],"references":[{"path":"./one"},{"path":"./two"}],"compilerOptions":{"disableReferencedProjectLoad":%t}}
// @Filename: /one/tsconfig.json
{"compilerOptions":{"composite":true},"files":["index.ts"]}
// @Filename: /one/index.ts
export const Alpha = 1;
// @Filename: /two/tsconfig.json
{"compilerOptions":{"composite":true},"files":["index.ts"]}
// @Filename: /two/index.ts
export const Beta = 2;`,disabled)
        f,done:=NewFourslash(t,nil,content)
        func(){defer done();f.GoToFile(t,"/one/index.ts")
            for _,scope:=range []lsutil.WorkspaceSymbolsScope{lsutil.WorkspaceSymbolsScopeCurrentProject,lsutil.WorkspaceSymbolsScopeAllOpenProjects} {
                prefs:=lsutil.NewDefaultUserPreferences();prefs.WorkspaceSymbolsScope=scope;f.Configure(t,prefs)
                params:=&lsproto.WorkspaceSymbolParams{Query:"",TextDocument:&lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI("/one/index.ts")}}
                response,result,ok:=f.client.SendRequest(t,lsproto.WorkspaceSymbolInfo,params)
                if !ok || response.AsResponse().Error!=nil {t.Fatal("workspace scope failed")};f.csharpRecordSyntax(t,lsproto.WorkspaceSymbolInfo,params,result)
            }
        }()
    }
}

func TestCSharpDocumentSymbols(t *testing.T) {
    samples:=[]string{
        "const 名 = '😀'; function 方法(引数) { const 葉 = class {}; return 葉; }\r\nnamespace 空.間 { export const 字 = 'é'; } namespace 空.間 { export class 表 {} }",
        "class 表 { constructor(public 名: string, readonly 字 = '😀') {} ['a\\nb']() {} #葉 = 1; static { const 名前 = 1; } }",
        "const obj = { ...other, ['a']: () => {}, `bad`: 1 }; call('😀', `a\nb`, () => { let 名 = 1; }); export default function() {}",
        "/** @typedef {{名: string}} 表 */\n/** @callback 方法\n * @param {表} 引数 */\nfunction Foo() {} Foo.prototype.名 = function() {}; Foo.x = class {}; Object.defineProperty(Foo, '字', {value: 1});",
        "call('"+strings.Repeat("😀",160)+"', function() {}); class "+strings.Repeat("字",155)+" {}",
        "",
    }
    for _,hierarchical:=range []bool{false,true} {
        for _,sample:=range samples {
            caps:=&lsproto.ClientCapabilities{TextDocument:&lsproto.TextDocumentClientCapabilities{DocumentSymbol:&lsproto.DocumentSymbolClientCapabilities{HierarchicalDocumentSymbolSupport:&hierarchical}}}
            f,done:=NewFourslash(t,caps,"// @allowJs: true\n// @Filename: /symbols.js\n"+sample)
            func(){defer done();params:=&lsproto.DocumentSymbolParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI("/symbols.js")}}
                response,result,ok:=f.client.SendRequest(t,lsproto.TextDocumentDocumentSymbolInfo,params)
                if !ok || response.AsResponse().Error!=nil {t.Fatalf("document symbols request failed: %v; sample: %q", response.AsResponse().Error, sample)};f.csharpRecordSyntax(t,lsproto.TextDocumentDocumentSymbolInfo,params,result)
            }()
        }
    }
}

func TestCSharpSyntaxUnicode(t *testing.T) {
    samples := []string{
        "const 名 = <組.件 title=\"😀\"><葉>日本語</葉></組.件>;\r\nconst 𐀀 = 'é';",
        "const 名 = <>日本語 😀\r\n<組.件 /></>;",
        "/** 日本語 😀\n * @template T\n * @param {T} 名 value\n */\nfunction 方法(名) { return `😀${名}é`; }",
        "type 表 = { -readonly [名 in keyof { 字: 'é😀' }]-?: 日本語 };\r\n// コメント 😀\r\nconst 字 = '日本語';",
    }
    for _, sample := range samples {
        f, done := NewFourslash(t, nil, "// @Filename: /unicode.tsx\n"+sample)
        func() {
            defer done()
            script := f.getScriptInfo("/unicode.tsx")
            folding := &lsproto.FoldingRangeParams{TextDocument: lsproto.TextDocumentIdentifier{Uri: lsconv.FileNameToDocumentURI("/unicode.tsx")}}
            response, foldingResult, ok := f.client.SendRequest(t, lsproto.TextDocumentFoldingRangeInfo, folding)
            if !ok || response.AsResponse().Error != nil { t.Fatal("folding request failed") }
            f.csharpRecordSyntax(t, lsproto.TextDocumentFoldingRangeInfo, folding, foldingResult)
            positions := []int{}
            for position := range sample { positions = append(positions, position) }
            positions = append(positions, len(sample))
            for _, position := range positions {
                point := f.converters.PositionToLineAndCharacter(script, core.TextPos(position))
                document := lsproto.TextDocumentIdentifier{Uri: lsconv.FileNameToDocumentURI("/unicode.tsx")}
                selection := &lsproto.SelectionRangeParams{TextDocument:document, Positions:[]lsproto.Position{point}}
                response, result, ok := f.client.SendRequest(t, lsproto.TextDocumentSelectionRangeInfo, selection)
                if !ok || response.AsResponse().Error != nil { t.Fatal("selection request failed") }
                f.csharpRecordSyntax(t, lsproto.TextDocumentSelectionRangeInfo, selection, result)
                linked := &lsproto.LinkedEditingRangeParams{TextDocument:document, Position:point}
                response, linkedResult, ok := f.client.SendRequest(t, lsproto.TextDocumentLinkedEditingRangeInfo, linked)
                if !ok || response.AsResponse().Error != nil { t.Fatal("linked editing request failed") }
                f.csharpRecordSyntax(t, lsproto.TextDocumentLinkedEditingRangeInfo, linked, linkedResult)
            }
        }()
    }
}

func TestCSharpFoldingCapabilities(t *testing.T) {
    samples := []string{
        "// #region 日本語 😀\r\nconst 名 = <組.件\r\n title=\"😀\">\r\n日本語\r\n</組.件>;\r\n// #endregion\r\n",
        "// first\n// second\n// #region outer\n// #region\nfunction 名(\n 字: string,\n 𐀀: string\n) {\n return `😀\n${字}`;\n}\n// #endregion\n// #endregion\n",
        "/* multi\n// #region ignored\ninside\n// #endregion\n*/\n// #region unmatched\nlet a = [\n {\n  x: 'é'\n },\n];\n",
        "import {\n 名,\n 字\n} from '例';\nimport 𐀀 from '字';\n/** 文書 😀\n * @param 名 - 値\n */\nconst f = (名) =>\n 名\n .join(\n 'é'\n );",
        "// #region first\u2028const 名 = `a\u2028b`;\u2028// #endregion\u2029// #region second\rconst 表 = {\r 字: 'é'\r};\r// #endregion",
    }
    for _, lineOnly := range []bool{false,true} {
        for _, collapsed := range []bool{false,true} {
            for _, sample := range samples {
                capabilities := &lsproto.ClientCapabilities{TextDocument:&lsproto.TextDocumentClientCapabilities{
                    FoldingRange:&lsproto.FoldingRangeClientCapabilities{LineFoldingOnly:&lineOnly,
                        FoldingRange:&lsproto.ClientFoldingRangeOptions{CollapsedText:&collapsed}},
                }}
                f, done := NewFourslash(t, capabilities, "// @Filename: /folding.tsx\n"+sample)
                func() {
                    defer done()
                    params := &lsproto.FoldingRangeParams{TextDocument:lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI("/folding.tsx")}}
                    response, result, ok := f.client.SendRequest(t, lsproto.TextDocumentFoldingRangeInfo, params)
                    if !ok || response.AsResponse().Error != nil { t.Fatal("folding request failed") }
                    f.csharpRecordSyntax(t, lsproto.TextDocumentFoldingRangeInfo, params, result)
                }()
            }
        }
    }
}

func TestCSharpSemanticCapabilities(t *testing.T) {
    samples := []string{
        "const 名 = '😀'; const 𐀀 = (字: string) => 字;\r\nfunction 方法(引数: string) { const local = 𐀀(引数); return local; }\r\n方法(名);",
        "class 表 { static readonly 字 = 'é'; async 方法(値 = 表.字) { return 値; } }\nconst 組 = new 表();\n組.方法();\nconst view = <組.方法 attr={組.方法()} />;",
        "interface 表 { 名: string; 方法(): void }\nconst 表 = { 名: '😀' };\nlet 変数: 表;\nconst 参照 = 表.名;\nnamespace 空 { export interface 葉 {} }\nlet 葉: 空.葉;",
        "const 文字 = '😀';\u2028const 別 = 文字;\u2029function 方法({ 名, ...残り }: { 名: number, 字: string }) { try { return 名; } catch (失敗) { return 失敗; } }",
    }
    for option := 0; option < 3; option++ {
        types, modifiers := defaultSemanticTokenTypes(), defaultSemanticTokenModifiers()
        if option == 1 { types = []string{"function", "variable", "class", "parameter"}; modifiers = []string{"local", "readonly", "declaration"} }
        if option == 2 { types = []string{}; modifiers = []string{} }
        caps := &lsproto.ClientCapabilities{TextDocument:&lsproto.TextDocumentClientCapabilities{SemanticTokens:&lsproto.SemanticTokensClientCapabilities{
            Requests:&lsproto.ClientSemanticTokensRequestOptions{Full:&lsproto.BooleanOrClientSemanticTokensRequestFullDelta{Boolean:new(true)}},
            TokenTypes:types,TokenModifiers:modifiers,Formats:[]lsproto.TokenFormat{lsproto.TokenFormatRelative},
        }}}
        for _, sample := range samples {
            f, done := NewFourslash(t, caps, "// @Filename: /semantic.tsx\n"+sample)
            func() {
                defer done()
                doc := lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI("/semantic.tsx")}
                params := &lsproto.SemanticTokensParams{TextDocument:doc}
                response,result,ok := f.client.SendRequest(t,lsproto.TextDocumentSemanticTokensFullInfo,params)
                if !ok || response.AsResponse().Error != nil { t.Fatal("semantic token request failed") }
                f.csharpRecordSyntax(t,lsproto.TextDocumentSemanticTokensFullInfo,params,result)
                script := f.getScriptInfo("/semantic.tsx")
                positions := []int{0,1,len(sample)/2,len(sample)-1,len(sample)}
                for _, start := range positions {
                    for _, end := range []int{start,min(start+9,len(sample))} {
                        rng := lsproto.Range{Start:f.converters.PositionToLineAndCharacter(script,core.TextPos(start)),End:f.converters.PositionToLineAndCharacter(script,core.TextPos(end))}
                        params := &lsproto.SemanticTokensRangeParams{TextDocument:doc,Range:rng}
                        response,result,ok := f.client.SendRequest(t,lsproto.TextDocumentSemanticTokensRangeInfo,params)
                        if !ok || response.AsResponse().Error != nil { t.Fatal("semantic range request failed") }
                        f.csharpRecordSyntax(t,lsproto.TextDocumentSemanticTokensRangeInfo,params,result)
                    }
                }
            }()
        }
    }
}

func TestCSharpCodeActions(t *testing.T) {
    samples := []struct{name,text,mapper string}{
        {"/a.ts", "const 字='😀';\r\nimport { z, a, unused } from './dep';\r\nexport const value={z,a};", ""},
        {"/a.ts", "const 字='😀';\r\ndeclare function make():number;\r\nexport let 値=make();\r\nexport const other=make();", ""},
        {"/a.ts", "interface Foo<T=string,U=unknown>{ a:T;b:U; }\nexport function f(x:Foo){return x}", ""},
        {"/a.ts", "const 字='😀';\ninterface 表 { 名前:string; $方法(値:number):void }\nexport class 実装 implements 表 {}", ""},
        {"/a.ts", "const 字='😀';\n$Value; let variable:$ValueType;", ""},
        {"/a.ts", "declare const source:{x?:number;y:string};\nexport const {x=1,y}=source;", ""},
        {"/a.ts", "export function f() {return 1} f.extra=2;\nexport class C extends (class { a=1 }) {}", ""},
        {"/a.tsx", "import { jsx,unused } from './dep';\n/** @jsx jsx */\nexport const node=<div/>;", ""},
        {"/app.vue", "<template>日本語 😀</template>\n<script>\nimport { z, a, unused } from './dep';\nexport let 値={z,a};\n$Value;\n</script>", contentmappertest.ComponentMapper},
        {"/app.astro", "declare function make():number;\nexport let 値=make();", contentmappertest.PrefixedSupplementalMapper},
        {"/app.astro", "import { z, a, unused } from './dep';\nexport const value={z,a};", contentmappertest.DuplicateProjectionMapper},
        {"/app.astro", "ignored", contentmappertest.UnmappedFoldingMapper},
    }
    kinds:=[][]lsproto.CodeActionKind{nil,{}, {"quickfix"},{"source"},{"source.organizeImports"},{"source.organizeImports.ts"},{"source.removeUnusedImports.ts"},{"source.sortImports.ts"},{"source.fixAll.ts"},{"refactor"},{""},{"quickfix","source.sortImports.ts"},{"source.sortImports.ts","source.sortImports.ts"}}
    for index,sample:=range samples {t.Run(fmt.Sprint(index),func(t *testing.T){for format:=0;format<3;format++ {
        options:=&FourslashOptions{Capabilities:GetDefaultCapabilities()}
        content:="// @noLib: true\n// @declaration: true\n// @isolatedDeclarations: true\n// @Filename: "+sample.name+"\n"+sample.text
        if sample.mapper!="" {
            content="// @Filename: /tsconfig.json\n{\"compilerOptions\":{\"noLib\":true,\"declaration\":true,\"isolatedDeclarations\":true},\"contentMappers\":[{\"package\":\"mapper\",\"extensions\":[\".astro\",\".vue\"]}]}\n// @Filename: /node_modules/mapper/package.json\n"+contentmappertest.PackageJSON(sample.mapper)+"\n// @Filename: "+sample.name+"\n"+sample.text
            options.ContentMapperSpawner=contentmappertest.NewSpawner();options.RunExternalCode=true
        }
        if format==2 {
            if sample.mapper=="" {content="// @newline: crlf\n"+content}else{content=strings.Replace(content,"\"isolatedDeclarations\":true","\"isolatedDeclarations\":true,\"newLine\":\"crlf\"",1)}
        }
        content+="\n// @Filename: /dep.ts\nexport const a=1;export const z=2;export const unused=3;export const $Value=4;export interface $ValueType { name:string };export const jsx:any=null;"
        f,done:=NewFourslashWithOptions(t,content,options)
        func(){defer done();f.GoToFile(t,sample.name)
            prefs:=lsutil.NewDefaultUserPreferences()
            if format==1 {prefs.QuotePreference=lsutil.QuotePreferenceSingle;prefs.FormatCodeSettings.NewLineCharacter="\r\n";prefs.FormatCodeSettings.IndentSize=2;prefs.FormatCodeSettings.Semicolons=lsutil.SemicolonPreferenceRemove;prefs.PreferTypeOnlyAutoImports=core.TSTrue}
            f.Configure(t,prefs)
            doc:=lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(sample.name)}
            report:=f.sendRequest(t,lsproto.TextDocumentDiagnosticInfo,&lsproto.DocumentDiagnosticParams{TextDocument:doc})
            var diagnostics []*lsproto.Diagnostic
            if report.FullDocumentDiagnosticReport!=nil {diagnostics=report.FullDocumentDiagnosticReport.Items}
            end:=f.converters.PositionToLineAndCharacter(f.getScriptInfo(sample.name),core.TextPos(len(sample.text)))
            for _,only:=range kinds {
                context:=&lsproto.CodeActionContext{Diagnostics:diagnostics}
                if only!=nil {context.Only=&only}
                f.sendRequest(t,lsproto.TextDocumentCodeActionInfo,&lsproto.CodeActionParams{TextDocument:doc,Range:lsproto.Range{Start:lsproto.Position{},End:end},Context:context})
            }
            if len(diagnostics)>0 {
                duplicate:=append(append([]*lsproto.Diagnostic{},diagnostics...),diagnostics...)
                f.sendRequest(t,lsproto.TextDocumentCodeActionInfo,&lsproto.CodeActionParams{TextDocument:doc,Context:&lsproto.CodeActionContext{Diagnostics:duplicate}})
                copied:=make([]*lsproto.Diagnostic,len(diagnostics))
                for i,d:=range diagnostics {copy:=*d;copy.Source=new("custom");copied[i]=&copy}
                f.sendRequest(t,lsproto.TextDocumentCodeActionInfo,&lsproto.CodeActionParams{TextDocument:doc,Context:&lsproto.CodeActionContext{Diagnostics:copied}})
            }
            f.sendRequest(t,lsproto.TextDocumentCodeActionInfo,&lsproto.CodeActionParams{TextDocument:doc,Context:&lsproto.CodeActionContext{}})
        }()
    }})}
}
