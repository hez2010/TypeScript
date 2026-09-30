using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compatibility;

internal static class ParserSafetyTests
{
    public static void Run()
    {
        VerifyScalarProperties();
        VerifySingleWorkerDocumentation(false);
        const int depth = 21_000;
        int cases = 0, nodes = 0;
        SourceFileNode Parse(Utf8String name, Utf8String text, ScriptKind kind = ScriptKind.TS)
        {
            var timer = Stopwatch.StartNew();
            SourceFileNode file = Parser.ParseSourceFile(new(name, kind), new(text));
            Check(
                file.ParseDiagnostics.Count == 0,
                Utf8String.ConcatMany(name, ": "u8, Utf8String.Join(", "u8, file.ParseDiagnostics.Select(d => Utf8String.ConcatMany(Utf8String.EnumName(d.Code), "@"u8, Utf8String.Format(d.Start))))));
            foreach (SyntaxNode node in file.DescendantsAndSelf())
            {
                Check(0 <= node.Pos && node.Pos <= node.End && node.End <= file.Source.Bytes.Length, Utf8String.ConcatMany(name, ": "u8, Utf8String.EnumName(node.Kind), " source range"u8));
                for (int i = 0; i < node.ChildCount; i++)
                {
                    SyntaxNode child = node.GetChild(i);
                    Check(ReferenceEquals(child.Parent, node), Utf8String.ConcatMany(name, ": "u8, Utf8String.EnumName(child.Kind), " parent"u8));
                    // A JS annotation reparsed from a preceding comment retains
                    // the comment range even when attached to a later declaration.
                    Check(
                        (child.Flags & NodeFlags.Reparsed) != 0 || node.Pos <= child.Pos && child.End <= node.End,
                        Utf8String.ConcatMany(name, ": "u8, Utf8String.EnumName(child.Kind), " child range"u8));
                }
                nodes++;
            }
            cases++;
            Console.WriteLine($"{name}: {timer.ElapsedMilliseconds} ms");
            return file;
        }
        SourceFileNode Type(Utf8String name, Utf8String prefix, Utf8String suffix, K kind)
        {
            SourceFileNode file = Parse(name, Utf8String.Concat("type T="u8, Repeat(prefix, depth), "A"u8) + Repeat(suffix, depth) + ";"u8);
            Check(file.DescendantsAndSelf().Count(n => n.Kind == kind) == depth, Utf8String.ConcatMany(name, ": nesting preserved"u8));
            return file;
        }

        Utf8String unicodePrefix = "/*é😀*/type T="u8;
        SourceFileNode parentheses = Parse("parentheses.ts"u8, Utf8String.Concat(unicodePrefix, new Utf8String('(', depth), "A"u8) + new Utf8String(')', depth) + ";"u8);
        SyntaxNode nested = ((TypeAliasDeclarationNode)parentheses.Statements![0]).Type!;
        for (int i = 0; i < depth; i++)
        {
            Check(nested is ParenthesizedTypeNode, "parenthesized type kind"u8);
            Check(nested.Pos == unicodePrefix.Length + i, "parenthesized byte start"u8);
            Check(nested.End == unicodePrefix.Length + depth * 2 + 1 - i, "parenthesized byte end"u8);
            nested = ((ParenthesizedTypeNode)nested).Type!;
        }
        Check(nested is TypeReferenceNode { TypeName: IdentifierNode { Text: { Span: var matchedText } } } && matchedText.SequenceEqual("A"u8), "parenthesized leaf"u8);

        SourceFileNode precedence = Parse("parenthesis-precedence.ts"u8, "type T=((A)|B); type U=(A&(B|C))[];"u8);
        Check(
            ((TypeAliasDeclarationNode)precedence.Statements![0]).Type is ParenthesizedTypeNode { Type: UnionTypeNode { Types.Count: 2 } union }
            && union.Types[0] is ParenthesizedTypeNode, "parenthesized union structure"u8);

        SourceFileNode awaitSpans = Parse("await-spans.ts"u8,
            "/*é😀*/export {}; await value; await (value); const untouched=1; await [value]; const last=2;"u8);
        Check(awaitSpans.Statements!.Select(n => (n.Flags & NodeFlags.AwaitContext) != 0)
            .SequenceEqual([false, false, true, false, true, false]), "only ambiguous await statements are reparsed"u8);
        Check(awaitSpans.DescendantsAndSelf().OfType<AwaitExpressionNode>().Count() == 3, "await expression shapes"u8);

        SourceFileNode awaitOverlap = Parse("await-overlap.ts"u8,
            "export {}; await\nvalue; const between=1; await (value); const after=2;"u8);
        Check(awaitOverlap.Statements!.Select(n => (n.Flags & NodeFlags.AwaitContext) != 0)
            .SequenceEqual([false, true, true, true, false]), "await reparse extends to the next marked span"u8);

        SourceFileNode awaitEnd = Parse("await-end.ts"u8, "export {}; await\nvalue; const tail=1;"u8);
        Check(awaitEnd.Statements!.Select(n => (n.Flags & NodeFlags.AwaitContext) != 0)
            .SequenceEqual([false, true, true]), "await reparse extends to EOF after consuming the last boundary"u8);

        SourceFileNode signatures = Parse("index-signatures.ts"u8, """
            type Index = {
                [key: string,]: number;
                [idx?: number]: any;
                [...rest]: string;
                [first, second]: number;
                []: number;
                [public name: string]: string;
                [readonly: string]: number;
                [key: string = ""]: number;
                [a ? b : c]: number;
                [single]: number;
            };
            """u8);
        var indices = signatures.DescendantsAndSelf().OfType<IndexSignatureDeclarationNode>().ToArray();
        Check(indices.Length == 8 && indices[0].Parameters is { Count: 1, HasTrailingComma: true }, "index signature trailing comma"u8);
        Check(indices[1].Parameters![0] is ParameterDeclarationNode { QuestionToken: not null }
            && indices[2].Parameters![0] is ParameterDeclarationNode { DotDotDotToken: not null },
            "index parameter optional and rest syntax"u8);
        Check(indices[3].Parameters?.Count == 2 && indices[4].Parameters?.Count == 0, "index parameter list recovery"u8);
        Check(indices[5].Parameters![0] is ParameterDeclarationNode { Modifiers.Count: 1 }
            && indices[6].Parameters![0] is ParameterDeclarationNode { Name: IdentifierNode { Text: { Span: var matchedText2 } } } && matchedText2.SequenceEqual("readonly"u8)
            && indices[7].Parameters![0] is ParameterDeclarationNode { Initializer: StringLiteralNode },
            "index parameter modifiers, name and initializer"u8);
        Check(
            signatures.DescendantsAndSelf().Count(n => n is ComputedPropertyNameNode) == 2,
            "computed names stay distinct from index signatures"u8);

        SourceFileNode nestedFunctions = Parse("generic-function-types.ts"u8, """
            type F = Box<<T>(x: T) => T>;
            type G = import("module").Modifier<<T>(x: T) => T>;
            type H = (readonly: string) => string;
            type I<X> = any extends ((any extends any ? any : string) extends any ? import("./name").Name<X> : any) ? any : any;
            type J = import("module", { with: { type: "json" }, });
            type K = typeof f< <T>()=>T>;
            """u8);
        Check(nestedFunctions.DescendantsAndSelf().Count(n => n is FunctionTypeNode) == 4, "nested generic function types"u8);
        Check(
            nestedFunctions.DescendantsAndSelf().Count(n => n is ConditionalTypeNode) == 3,
            "import type remains the true conditional branch"u8);

        SourceFileNode heritage = Parse("heritage-expressions.ts"u8, """
            class C {}
            interface I extends ns.Base<T>, (typeof C) {}
            class D extends C, {}
            class E implements ns.Base<T>, a?.b {}
            """u8);
        var bases = heritage.DescendantsAndSelf().OfType<HeritageClauseNode>().ToArray();
        Check(bases[0].Types![0] is TypeReferenceNode { TypeName: QualifiedNameNode }
            && bases[0].Types![1] is ExpressionWithTypeArgumentsNode { Expression: ParenthesizedExpressionNode },
            "interface heritage retains nonentity expressions"u8);
        Check(
            bases[1].Types?.Count == 1 && bases[1].Types![0] is ExpressionWithTypeArgumentsNode { Expression: IdentifierNode },
            "class heritage trailing comma"u8);
        Check(bases[2].Types![1] is ExpressionWithTypeArgumentsNode { Expression: PropertyAccessExpressionNode { Flags: var flags } }
            && (flags & NodeFlags.OptionalChain) != 0, "optional heritage chain is not converted to an entity name"u8);

        SourceFileNode privateNames = Parse(
            "private-type-query.ts"u8,
            "class C { #x = 0; get #v(){ return this.#x; } set #v(value){} get 1n(){ return 0; } get enum(){ return 0; } value: typeof this.#x; }"u8);
        Check(
            privateNames.DescendantsAndSelf().OfType<TypeQueryNode>().Single().ExprName is QualifiedNameNode { Right: PrivateIdentifierNode },
            "type query preserves private identifier"u8);
        Parse(
            "this-parameters.ts"u8,
            "function f(this: C, value: string){} type F=(this:C, value:string)=>void; interface I { get value(): number {} }"u8);
        (Utf8String Text, DiagnosticCode Diagnostic)[] invalidTypes =
        [
            (Utf8String.Copy("var v:void.x;"u8), DiagnosticCode.X0Expected),
            (Utf8String.Copy("type T=typeof f<<A>()=>A>;"u8), DiagnosticCode.X0Expected),
            (Utf8String.Copy("function f(this?:C){}"u8), DiagnosticCode.X0Expected),
            (Utf8String.Copy("function f(this:C=foo){}"u8), DiagnosticCode.X0Expected),
            (Utf8String.Copy("function f(@dec this:C){}"u8), DiagnosticCode.NeitherDecoratorsNorModifiersMayBeAppliedToThisParameters),
            (Utf8String.Copy("function f(public this:C){}"u8), DiagnosticCode.NeitherDecoratorsNorModifiersMayBeAppliedToThisParameters),
            (Utf8String.Copy("interface I { f(): void {} }"u8), DiagnosticCode.X0Expected),
            (Utf8String.Copy("type F=A|()=>B;"u8), DiagnosticCode.FunctionTypeNotationMustBeParenthesizedWhenUsedInAUnionType),
        ];
        foreach (var invalid in invalidTypes)
        {
            SourceFileNode file = Parser.ParseSourceFile(new("invalid-type.ts"u8), new(invalid.Text));
            Check(
                file.ParseDiagnostics.Any(d => d.Code == invalid.Diagnostic),
                Utf8String.ConcatMany("invalid type syntax must report "u8, Utf8String.EnumName(invalid.Diagnostic), ": "u8, invalid.Text));
        }
        (Utf8String Text, Utf8String[] Methods)[] memberRecovery =
        [
            (Utf8String.Copy("class C { private a(): boolean { private b(): boolean {} }"u8), ["a"u8, "b"u8]),
            (Utf8String.Copy("class Foo { f1(){ if(a.b){} public f2(){} f3(){} }"u8), ["f1"u8, "f2"u8, "f3"u8]),
        ];
        foreach (var recovery in memberRecovery)
        {
            SourceFileNode file = Parser.ParseSourceFile(new("class-recovery.ts"u8), new(recovery.Text));
            var declaration = file.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single();
            Check(
                file.ParseDiagnostics.Count != 0
                    && declaration.Members!.OfType<MethodDeclarationNode>().Select(m => ((IdentifierNode)m.Name!).Text).SequenceEqual(recovery.Methods),
                "class body recovery retains following methods"u8);
        }

        Type("tuples.ts"u8, "["u8, "]"u8, K.TupleType);
        SourceFileNode generic = Parse("generics.ts"u8, Utf8String.Concat(Utf8String.Copy("type T="u8), Repeat("Box<"u8, depth), "A"u8) + new Utf8String('>', depth) + ";"u8);
        Check(generic.DescendantsAndSelf().Count(n => n is TypeReferenceNode) == depth + 1, "generic nesting preserved"u8);
        Type("indexed-access.ts"u8, "A["u8, "]"u8, K.IndexedAccessType);
        Type("operators.ts"u8, "keyof "u8, ""u8, K.TypeOperator);
        Type("functions.ts"u8, "()=>"u8, ""u8, K.FunctionType);
        Type("conditionals.ts"u8, "A extends B ? C : "u8, ""u8, K.ConditionalType);
        Type("mapped.ts"u8, "{[P in K]:"u8, "}"u8, K.MappedType);
        Type("type-literals.ts"u8, "{value:"u8, "}"u8, K.TypeLiteral);
        Type("template-types.ts"u8, "`a${"u8, "}`"u8, K.TemplateLiteralType);

        (Utf8String Prefix, Utf8String Suffix)[] productions =
        [
            (Utf8String.Copy("("u8), Utf8String.Copy("|B)"u8)), (Utf8String.Copy("Box<"u8), Utf8String.Copy(">"u8)), (Utf8String.Copy("["u8), Utf8String.Copy("]"u8)), (Utf8String.Copy("{value:"u8), Utf8String.Copy("}"u8)),
            (Utf8String.Copy("(x:"u8), Utf8String.Copy(")=>A"u8)), (Utf8String.Copy("A extends B ? "u8), Utf8String.Copy(" : C"u8)), (Utf8String.Copy("{[P in K]:"u8), Utf8String.Copy("}"u8)), (Utf8String.Copy("keyof ("u8), Utf8String.Copy(")"u8)),
        ];
        var compound = new Utf8StringBuilder(Utf8String.Copy("type T="u8));
        for (int i = 0; i < depth; i++)
            compound.Append(productions[i % productions.Length].Prefix);
        compound.Append((byte)'A');
        for (int i = depth - 1; i >= 0; i--)
            compound.Append(productions[i % productions.Length].Suffix);
        compound.Append((byte)';');
        SourceFileNode combined = Parse("compound-types.ts"u8, compound.ToUtf8String());
        Check(MaxDepth(combined) >= depth, "compound type depth"u8);

        Parse("blocks.ts"u8, Utf8String.Concat(new Utf8String('{', depth), ";"u8) + new Utf8String('}', depth));
        Parse("if-statements.ts"u8, Repeat("if(a)"u8, depth) + ";"u8);
        Parse("expression-parentheses.ts"u8, Utf8String.Concat("const x="u8, new Utf8String('(', depth), "1"u8) + new Utf8String(')', depth) + ";"u8);
        Parse("array-expressions.ts"u8, Utf8String.Concat("const x="u8, new Utf8String('[', depth), "1"u8) + new Utf8String(']', depth) + ";"u8);
        Parse("object-expressions.ts"u8, Utf8String.Concat(Utf8String.Copy("const x="u8), Repeat("{a:"u8, depth), "1"u8) + new Utf8String('}', depth) + ";"u8);
        Parse("binary-expressions.ts"u8, Utf8String.Copy("const x="u8) + Repeat("a**"u8, depth) + "a;"u8);
        Parse("function-expressions.ts"u8, Utf8String.Concat(Utf8String.Copy("const x="u8), Repeat("(function(){return "u8, depth), "0"u8) + Repeat(";})()"u8, depth) + ";"u8);
        Parse("jsx.tsx"u8, Utf8String.Concat(Utf8String.Copy("const x="u8), Repeat("<a>"u8, depth), "text"u8) + Repeat("</a>"u8, depth) + ";"u8, ScriptKind.TSX);
        Utf8String jsxName = Repeat("ns."u8, depth) + "Tag"u8;
        Parse("jsx-qualified.tsx"u8, Utf8String.Concat("const x=<"u8, jsxName, "></"u8) + jsxName + ">;"u8, ScriptKind.TSX);
        SourceFileNode jsxThis = Parse(
            "jsx-names.tsx"u8,
            "const x=<this.Item xml:lang={language} {...left,right}>{...items}<ns:tag/><this/></this.Item>; const y=<Component<number>/>;"u8,
            ScriptKind.TSX);
        Check(
            jsxThis.DescendantsAndSelf().OfType<JsxSelfClosingElementNode>().Any(
                n => n.TagName is KeywordExpressionNode { Kind: K.ThisKeyword }),
            "JSX this tag keeps keyword semantics"u8);
        (Utf8String Text, ScriptKind Kind, DiagnosticCode Diagnostic)[] invalidJsx =
        [
            (Utf8String.Copy("<a></b>;"u8), ScriptKind.TSX, DiagnosticCode.ExpectedCorrespondingJSXClosingTagFor0),
            (Utf8String.Copy("<a:b></b>;"u8), ScriptKind.TSX, DiagnosticCode.ExpectedCorrespondingJSXClosingTagFor0),
            (Utf8String.Copy("<a.b.c></a>;"u8), ScriptKind.TSX, DiagnosticCode.ExpectedCorrespondingJSXClosingTagFor0),
            (Utf8String.Copy("<a><b></a>;"u8), ScriptKind.TSX, DiagnosticCode.JSXElement0HasNoCorrespondingClosingTag),
            (Utf8String.Copy(@"<\u0061/>;"u8), ScriptKind.TSX, DiagnosticCode.UnicodeEscapeSequenceCannotAppearHere),
            (Utf8String.Copy(@"<a data-\u0061/>;"u8), ScriptKind.TSX, DiagnosticCode.UnicodeEscapeSequenceCannotAppearHere),
            (Utf8String.Copy("<this.#private/>;"u8), ScriptKind.TSX, DiagnosticCode.IdentifierExpected),
            (Utf8String.Copy("<X a={...a}/>;"u8), ScriptKind.TSX, DiagnosticCode.ExpressionExpected),
            (Utf8String.Copy("<X<T>/>;"u8), ScriptKind.JSX, DiagnosticCode.IdentifierExpected),
            (Utf8String.Copy("<a/><b/>;"u8), ScriptKind.TSX, DiagnosticCode.JSXExpressionsMustHaveOneParentElement),
        ];
        foreach (var invalid in invalidJsx)
        {
            SourceFileNode file = Parser.ParseSourceFile(new("invalid.tsx"u8, invalid.Kind), new(invalid.Text));
            Check(
                file.ParseDiagnostics.Any(d => d.Code == invalid.Diagnostic),
                Utf8String.ConcatMany("invalid JSX must report "u8, Utf8String.EnumName(invalid.Diagnostic), ": "u8, invalid.Text));
        }
        Parse("nested.json"u8, Utf8String.Concat(new Utf8String('[', depth), "1"u8) + new Utf8String(']', depth), ScriptKind.JSON);
        Parse("documentation.js"u8, Utf8String.Concat(Utf8String.Copy("/** @type {"u8), Repeat("Box<"u8, depth), "A"u8) + new Utf8String('>', depth) + "} */ const x=0;"u8, ScriptKind.JS);
        Parse("documentation-array.js"u8, Utf8String.Copy("/** @param {Object"u8) + Repeat("[]"u8, depth) + "} value */ function f(value){}"u8, ScriptKind.JS);
        Parse(
            "documentation-host.js"u8,
            Utf8String.Concat("/** @returns {number} */ const f="u8, new Utf8String('(', depth), "()=>0"u8) + new Utf8String(')', depth) + ";"u8,
            ScriptKind.JS);
        SourceFileNode names = Parse(
            "documentation-names.js"u8,
            Utf8String.Copy("/** @param {Object} "u8) + Repeat("a."u8, depth) + "value */ function f(value){}"u8,
            ScriptKind.JS);
        Check(
            names.GetDocumentation(names.Statements![0]).SelectMany(n => n.DescendantsAndSelf()).Count(n => n is QualifiedNameNode) == depth,
            "documentation name depth"u8);
        Utf8String qualified = Repeat("ns."u8, depth) + "Base"u8;
        Parse(
            "documentation-heritage.js"u8,
            Utf8String.Concat("/** @extends {"u8, qualified, "<number>} */ class C extends "u8) + qualified + " {}"u8,
            ScriptKind.JS);

        const int propertyDepth = 384;
        var properties = new Utf8StringBuilder(Utf8String.Copy("/**\n"u8));
        Utf8String propertyName = "value"u8;
        for (int i = 0; i < propertyDepth; i++)
        {
            properties.Append(" * @param {Object} "u8).Append(propertyName).Append((byte)'\n');
            propertyName += ".p"u8;
        }
        properties.Append(" * @param {number} "u8).Append(propertyName).Append("\n */ function f(value){}"u8);
        SourceFileNode grouped = Parse("documentation-properties.js"u8, properties.ToUtf8String(), ScriptKind.JS);
        Check(
            grouped.DescendantsAndSelf().Count(n => n is TypeLiteralNode) == propertyDepth,
            "documentation property grouping and cloning"u8);
        SourceFileNode typedef = Parse(
            "documentation-typedef.js"u8,
            "/** @typedef {Object} T\n * @property {Object} a\n * @property {number} a.b\n */"u8,
            ScriptKind.JS);
        Check(typedef.DescendantsAndSelf().OfType<TypeLiteralNode>().First().Members is { Count: 1 } members
            && members[0] is PropertySignatureDeclarationNode { Type: TypeLiteralNode }, "typedef preserves nested property ownership"u8);
        foreach (int lineBreak in new[] { 0x2028, 0x2029 })
        {
            SourceFileNode documentation = Parse("documentation-unicode-lines.js"u8,
                Utf8String.Concat("/**"u8, new Utf8String(lineBreak, 2), "text*/ function f() {}"u8), ScriptKind.JS);
            Check(documentation.GetDocumentation(documentation.Statements![0])[0].Comment![0] is JSDocTextNode comment
                && comment.Text is [var text] && text == "\ntext"u8, "Unicode documentation line breaks retain leading empty lines"u8);
        }

        SynchronizationContext? previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
            Type("synchronization-context.ts"u8, "Box<("u8, ")>"u8, K.ParenthesizedType);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        var cancellationSource = new SourceText(Utf8String.Concat("type T="u8, new Utf8String('(', 1_000_000), "A"u8) + new Utf8String(')', 1_000_000) + ";"u8);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            ExpectCancellation(cancellationSource, cancelled.Token);
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.CancelAfter(10);
            ExpectCancellation(cancellationSource, cancelled.Token);
        }
        var documentationSource = new SourceText(
            Utf8String.Concat("/** @type {"u8, new Utf8String('(', 1_000_000), "A"u8) + new Utf8String(')', 1_000_000) + "} */ const x=0;"u8);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.CancelAfter(10);
            ExpectCancellation(documentationSource, cancelled.Token, ScriptKind.JS);
        }
        Console.WriteLine(
            $"Parser safety: {cases} cases, {nodes} nodes; depth {depth}, source positions, parents and cancellation passed.");
    }

    private static Utf8String Repeat(Utf8String value, int count)
    {
        var result = new Utf8StringBuilder(value.Length * count);
        for (int i = 0; i < count; i++)
            result.Append(value);
        return result.ToUtf8String();
    }

    private static void VerifyScalarProperties()
    {
        static JsonDocument Scalars(SyntaxNode node)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
                AstScalarProperties.Write(writer, node);
            return JsonDocument.Parse(buffer.ToArray());
        }
        var factory = new NodeFactory();
        var identifier = factory.NewIdentifier("value"u8);
        using var plus = Scalars(factory.NewPrefixUnaryExpression(K.PlusToken, identifier));
        using var minus = Scalars(factory.NewPrefixUnaryExpression(K.MinusToken, identifier));
        Check(plus.RootElement.GetProperty("Operator"u8).GetInt32() == (int)K.PlusToken
            && minus.RootElement.GetProperty("Operator"u8).GetInt32() == (int)K.MinusToken,
            "scalar oracle distinguishes unary operators with identical tree shape"u8);
        using var typeOnly = Scalars(factory.NewImportSpecifier(true, null, identifier));
        using var valueImport = Scalars(factory.NewImportSpecifier(false, null, identifier));
        Check(
            typeOnly.RootElement.GetProperty("IsTypeOnly"u8).GetBoolean() && !valueImport.RootElement.GetProperty("IsTypeOnly"u8).GetBoolean(),
            "scalar oracle preserves import erasure semantics"u8);
        using var phase = Scalars(factory.NewImportClause(K.DeferKeyword, identifier, null));
        Check(
            phase.RootElement.GetProperty("PhaseModifier"u8).GetInt32() == (int)K.DeferKeyword,
            "scalar oracle preserves import evaluation phase"u8);
        using var template = Scalars(factory.NewTemplateHead("\n"u8, "\\n"u8, TokenFlags.ContainsInvalidEscape));
        Check(Wtf8.DecodeString(template.RootElement.GetProperty("RawText"u8).GetBytesFromBase64()) == "\\n"
            && template.RootElement.GetProperty("TemplateFlags"u8).GetUInt32() == (uint)TokenFlags.ContainsInvalidEscape,
            "scalar oracle preserves raw template spelling and flags"u8);
        Check(
            template.RootElement.EnumerateObject().Select(p => JsonStrings.GetName(p)).SequenceEqual([Utf8String.Copy("RawText"u8), Utf8String.Copy("TemplateFlags"u8), Utf8String.Copy("Text"u8)]),
            "scalar property ordering is canonical"u8);
        using var text = Scalars(factory.NewJSDocText(["a"u8, Utf8String.Copy([0xED, 0xA0, 0x80])]));
        Check(text.RootElement.GetProperty("Text"u8).GetArrayLength() == 2
            && Wtf8.DecodeString(
                text.RootElement.GetProperty("Text"u8)[1].GetBytesFromBase64()) == "\ud800",
            "scalar oracle preserves string chunks and unpaired surrogates"u8);
        Utf8String callText = "/*é😀*/f(1,);"u8;
        SourceFileNode file = Parser.ParseSourceFile(new("lists.ts"u8), new(callText));
        CallExpressionNode call = file.DescendantsAndSelf().OfType<CallExpressionNode>().Single();
        using var listBuffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(listBuffer))
            AstScalarProperties.WriteLists(writer, call, static value => value);
        using JsonDocument lists = JsonDocument.Parse(listBuffer.ToArray());
        JsonElement arguments = lists.RootElement.GetProperty("Arguments"u8);
        Check(arguments[0].GetInt32() == 1 && arguments[1].GetInt32() == callText.IndexOf((byte)'(') + 1
            && arguments[2].GetInt32() == callText.IndexOf((byte)')') && arguments[3].GetBoolean(),
            "list audit preserves Unicode positions and trailing comma"u8);
        Check(
            lists.RootElement.GetProperty("TypeArguments"u8).ValueKind == JsonValueKind.Null,
            "list audit distinguishes null from empty lists"u8);
        var missing = new NodeList([], 3, 3, true);
        var empty = new NodeList([], 3, 3);
        var signature = factory.NewFunctionTypeNode(null, missing, null);
        FunctionTypeNode clonedSignature = signature.DeepClone<FunctionTypeNode>();
        Check(missing.IsMissing && !empty.IsMissing && clonedSignature.Parameters is { IsMissing: true, Count: 0 }
            && !ReferenceEquals(
                missing,
                clonedSignature.Parameters), "missing parameter lists survive cloning independently of empty lists"u8);
        var parameterName = factory.NewIdentifier("value"u8);
        var parameterType = factory.NewJSDocTypeExpression(factory.NewKeywordTypeNode(K.StringKeyword));
        var parameterTag = factory.NewJSDocParameterOrPropertyTag(
            K.JSDocParameterTag,
            factory.NewIdentifier("param"u8),
            parameterName,
            false,
            parameterType,
            false,
            null);
        Check(
            ReferenceEquals(parameterTag.GetChild(1), parameterType) && ReferenceEquals(parameterTag.GetChild(2), parameterName),
            "JSDoc type-first traversal follows source spelling"u8);
        parameterTag.IsNameFirst = true;
        Check(
            ReferenceEquals(parameterTag.GetChild(1), parameterName) && ReferenceEquals(parameterTag.GetChild(2), parameterType),
            "JSDoc name-first traversal follows source spelling"u8);
    }

    public static void RunSingleWorkerDocumentation() => VerifySingleWorkerDocumentation(true);

    private static void VerifySingleWorkerDocumentation(bool requireWorkerLimit)
    {
        const int depth = 21_000;
        var source = new SourceText(
            Utf8String.Concat("/*é😀*/ /** @throws {"u8, new Utf8String('(', depth), "import('module').T"u8) + new Utf8String(')', depth) + "} */ function f(){}"u8);
        ThreadPool.GetMinThreads(out int minimumWorkers, out int minimumIo);
        ThreadPool.GetMaxThreads(out int maximumWorkers, out int maximumIo);
        using var cancellation = new CancellationTokenSource();
        try
        {
            bool singleWorker = ThreadPool.SetMinThreads(1, minimumIo) && ThreadPool.SetMaxThreads(1, maximumIo);
            Check(!requireWorkerLimit || singleWorker, "strict single-worker test requires a configurable worker pool"u8);
            Task verify = Task.Run(async () =>
            {
                SourceFileNode original = await Parser.ParseSourceFileAsync(
                    new("single-worker.js"u8),
                    source,
                    cancellation.Token).ConfigureAwait(false);
                Check(original.ParseDiagnostics.Count == 0 && original.Imports.Count == 1, "async JSDoc parser completes on one worker"u8);
                SourceFileNode clone = original.DeepClone<SourceFileNode>();
                Check(
                    clone.Imports.Count == 1 && !ReferenceEquals(clone.Imports[0], original.Imports[0]),
                    "clone owns the documentation import reference"u8);
                Check(
                    clone.Imports[0].Pos == original.Imports[0].Pos && clone.Imports[0].End == original.Imports[0].End,
                    "cloned documentation retains UTF-8 source positions"u8);
                SyntaxNode owner = clone.Imports[0];
                while (owner.Parent is { } parent)
                    owner = parent;
                Check(ReferenceEquals(owner, clone), "cloned documentation import has complete parent ownership"u8);
                IReadOnlyList<JSDocNode> comments = await clone.GetDocumentationAsync(
                    clone.Statements![0],
                    cancellation.Token).ConfigureAwait(false);
                Check(
                    comments.SelectMany(c => c.DescendantsAndSelf()).Any(n => ReferenceEquals(n, clone.Imports[0])),
                    "clone metadata reuses its cached documentation node"u8);
                SourceFileNode typed = await Parser.ParseSourceFileAsync(
                    new("single-worker.ts"u8),
                    source,
                    cancellation.Token).ConfigureAwait(false);
                IReadOnlyList<JSDocNode> lazy = await typed.GetDocumentationAsync(
                    typed.Statements![0],
                    cancellation.Token).ConfigureAwait(false);
                Check(
                    lazy.Count == 1 && MaxDepth(lazy[0]) >= depth && ReferenceEquals(lazy[0].Parent, typed.Statements[0]),
                    "lazy TypeScript documentation parses on one worker"u8);
                IReadOnlyList<JSDocNode> cached = await typed.GetDocumentationAsync(
                    typed.Statements[0],
                    cancellation.Token).ConfigureAwait(false);
                Check(ReferenceEquals(lazy[0], cached[0]), "async documentation preserves cached query identity"u8);
                SourceFileNode aliases = await Parser.ParseSourceFileAsync(
                    new("alias.js"u8),
                    new("/** @typedef {number} T */ const x=1;"u8),
                    cancellation.Token).ConfigureAwait(false);
                SyntaxNode alias = aliases.Statements!.Single(n => n.Kind == K.JSTypeAliasDeclaration);
                JSDocNode originalComment = aliases.GetDocumentation(alias).Single();
                SourceFileNode clonedAliases = aliases.DeepClone<SourceFileNode>();
                SyntaxNode clonedAlias = clonedAliases.Statements!.Single(n => n.Kind == K.JSTypeAliasDeclaration);
                JSDocNode clonedComment = (await clonedAliases.GetDocumentationAsync(
                    clonedAlias,
                    cancellation.Token).ConfigureAwait(false)).Single();
                Check(!ReferenceEquals(originalComment, clonedComment)
                    && clonedComment.Parent is not null
                    && !ReferenceEquals(clonedComment.Parent, originalComment.Parent)
                    && clonedComment.Pos == originalComment.Pos && clonedComment.End == originalComment.End,
                    "synthetic aliases retain source-local documentation after cloning"u8);
                SyntaxNode originalHost = originalComment.Parent!;
                SyntaxNode clonedHost = clonedComment.Parent!;
                Check(ReferenceEquals(aliases.GetDocumentation(originalHost).Single(), originalComment)
                    && ReferenceEquals(
                        (await clonedAliases.GetDocumentationAsync(clonedHost, cancellation.Token).ConfigureAwait(false)).Single(),
                        clonedComment), "cloned aliases share the cloned original host's comment identity"u8);
            });
            Check(verify.Wait(TimeSpan.FromSeconds(10)), "async parsing and documentation cloning do not block the sole worker"u8);
            verify.GetAwaiter().GetResult();
            Console.WriteLine(singleWorker
                ? Utf8String.Copy("Async documentation: single-worker parsing, lazy queries, cloning and cache identity passed."u8)
                : Utf8String.Copy("Async documentation: default worker pool passed; worker limits unsupported, separate portable-pool single-worker gate required."u8));
        }
        finally
        {
            cancellation.Cancel();
            ThreadPool.SetMaxThreads(maximumWorkers, maximumIo);
            ThreadPool.SetMinThreads(minimumWorkers, minimumIo);
        }
    }

    private static int MaxDepth(SyntaxNode root)
    {
        int maximum = 0;
        var pending = new Stack<(SyntaxNode Node, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out var item))
        {
            maximum = Math.Max(maximum, item.Depth);
            for (int i = 0; i < item.Node.ChildCount; i++)
                pending.Push((item.Node.GetChild(i), item.Depth + 1));
        }
        return maximum;
    }

    private static void ExpectCancellation(SourceText source, CancellationToken cancellation, ScriptKind kind = ScriptKind.TS)
    {
        try
        {
            Parser.ParseSourceFile(new("cancel.ts"u8, kind), source, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        throw new InvalidDataException("The production parser did not observe cancellation.");
    }

    private static void Check(bool condition, Utf8String message)
    {
        if (!condition)
            throw new InvalidDataException(message.ToString());
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            throw new InvalidOperationException("Parser captured the caller's synchronization context.");

        public override void Send(SendOrPostCallback callback, object? state) =>
            throw new InvalidOperationException("Parser sent work to the caller's synchronization context.");
    }
}
