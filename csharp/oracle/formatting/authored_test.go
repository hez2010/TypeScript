package fourslash

import (
    "reflect"
    "strings"
    "testing"
    "github.com/microsoft/TypeScript/tsc/internal/core"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsconv"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsutil"
    "github.com/microsoft/TypeScript/tsc/internal/lsp/lsproto"
)

func TestCSharpFormattingOptions(t *testing.T) {
    samples:=[]struct{file,text string}{
        {"/unicode.ts","/** 日本語 😀\r\n * doc\r\n */\r\nfunction 方法(名 :string, 𐀀:number){\r\n  const 字={a:名,b:𐀀};\r\nreturn 字;\r\n}\r\n"},
        {"/jsx.tsx","const 名=<a-b.c 属性={1+2}><div> 😀 {foo   (1,2)}\n<span {...props}/></div></a-b.c>;\nconst 外=<><組:件 a:b='é'/></>;\n"},
        {"/statements.ts","import {type A,B as C} from 'm';\nexport {C};\nclass X<T>{static readonly x:number=1\nconstructor(x:number){}\nasync f (a:T,b:T){if(a){return b}else{return a}}}\n"},
        {"/expressions.ts","type F<T>=new(a:T)=>{f: (b:T)=>T;[x:string]:T};\nlet f=function (x:number){return x};\nconst x=<number>1;\nconst y=`a${ x + 1 }b`;\nfor(let i=0;i<2;i++){f(i)}\n"},
        {"/asi.ts","let x=1\nx++\n;\n[1].forEach(f)\n(x)\n+1\n-1\n/a/.test('a')\n`a`\ninterface I{foo;\n():void;\n bar:string;}\nclass C{foo=1;\n bar:string;}\nfunction f(){}\nnamespace N{}\ndo {} while(false)\n"},
        {"/comments.ts","// lead 😀\nfunction f(){\n/* middle\n * text\n */\nlet a=1; // tail\n/* text; {\n\n unclosed"},
        {"/spaces.ts","\u3000\tconst 名=1;\u2028const 字={ a:2 };\u2029\tfunction f(){\n \treturn 名+字.a;\n}\n"},
    }
    for variant:=0;variant<7;variant++ { for _,sample:=range samples {
        f,done:=NewFourslash(t,nil,"// @Filename: "+sample.file+"\n"+sample.text)
        func(){defer done()
            options:=lsutil.GetDefaultFormatCodeSettings()
            if variant==1||variant==2 {
                value:=reflect.ValueOf(&options).Elem()
                for i:=0;i<value.NumField();i++ { field:=value.Field(i);if field.Type()==reflect.TypeFor[core.Tristate]() { if variant==1 { field.SetUint(uint64(core.TSTrue)) } else { field.SetUint(uint64(core.TSFalse)) } } }
            }
            if variant==3 { options.Semicolons=lsutil.SemicolonPreferenceInsert }
            if variant==4 { options.Semicolons=lsutil.SemicolonPreferenceRemove }
            if variant==5 { options.IndentSize=2;options.TabSize=2;options.ConvertTabsToSpaces=core.TSFalse;options.NewLineCharacter="\r\n";options.BaseIndentSize=1 }
            if variant==6 { options.IndentSize=0;options.TabSize=0 }
            preferences:=f.GetOptions();preferences.FormatCodeSettings=options;f.Configure(t,preferences)
            doc:=lsproto.TextDocumentIdentifier{Uri:lsconv.FileNameToDocumentURI(sample.file)}
            f.sendRequest(t,lsproto.TextDocumentFormattingInfo,&lsproto.DocumentFormattingParams{TextDocument:doc,Options:options.ToLSFormatOptions()})
            script:=f.getScriptInfo(sample.file)
            point:=func(offset int) lsproto.Position {return f.converters.PositionToLineAndCharacter(script,core.TextPos(offset))}
            for _,start:=range []int{0,1,len(sample.text)/2,len(sample.text)-1,len(sample.text)} { for _,end:=range []int{start,min(start+17,len(sample.text))} {
                f.sendRequest(t,lsproto.TextDocumentRangeFormattingInfo,&lsproto.DocumentRangeFormattingParams{TextDocument:doc,Options:options.ToLSFormatOptions(),Range:lsproto.Range{Start:point(start),End:point(end)}})
            } }
            for position,ch:=range sample.text { if !strings.ContainsRune(";{}\n",ch) {continue}
                f.sendRequest(t,lsproto.TextDocumentOnTypeFormattingInfo,&lsproto.DocumentOnTypeFormattingParams{TextDocument:doc,Options:options.ToLSFormatOptions(),Position:point(position+1),Ch:string(ch)})
            }
        }()
    } }
}
