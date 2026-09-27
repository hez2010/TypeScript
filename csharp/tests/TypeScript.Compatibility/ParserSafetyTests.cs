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
        SourceFileNode Parse(string name, string text, ScriptKind kind = ScriptKind.TS)
        {
            var timer = Stopwatch.StartNew();
            SourceFileNode file = Parser.ParseSourceFile(new(name, kind), new(text));
            Check(
                file.ParseDiagnostics.Count == 0,
                $"{name}: {string.Join(", ", file.ParseDiagnostics.Select(d => $"{d.Code}@{d.Start}"))}");
            foreach (SyntaxNode node in file.DescendantsAndSelf())
            {
                Check(0 <= node.Pos && node.Pos <= node.End && node.End <= file.Source.Bytes.Length, $"{name}: {node.Kind} source range");
                for (int i = 0; i < node.ChildCount; i++)
                {
                    SyntaxNode child = node.GetChild(i);
                    Check(ReferenceEquals(child.Parent, node), $"{name}: {child.Kind} parent");
                    // A JS annotation reparsed from a preceding comment retains
                    // the comment range even when attached to a later declaration.
                    Check(
                        (child.Flags & NodeFlags.Reparsed) != 0 || node.Pos <= child.Pos && child.End <= node.End,
                        $"{name}: {child.Kind} child range");
                }
                nodes++;
            }
            cases++;
            Console.WriteLine($"{name}: {timer.ElapsedMilliseconds} ms");
            return file;
        }
        SourceFileNode Type(string name, string prefix, string suffix, K kind)
        {
            SourceFileNode file = Parse(name, "type T=" + Repeat(prefix, depth) + "A" + Repeat(suffix, depth) + ";");
            Check(file.DescendantsAndSelf().Count(n => n.Kind == kind) == depth, $"{name}: nesting preserved");
            return file;
        }

        string unicodePrefix = "/*é😀*/type T=";
        SourceFileNode parentheses = Parse("parentheses.ts", unicodePrefix + new string('(', depth) + "A" + new string(')', depth) + ";");
        SyntaxNode nested = ((TypeAliasDeclarationNode)parentheses.Statements![0]).Type!;
        for (int i = 0; i < depth; i++)
        {
            Check(nested is ParenthesizedTypeNode, "parenthesized type kind");
            Check(nested.Pos == parentheses.Source.ToBytePosition(unicodePrefix.Length + i), "parenthesized byte start");
            Check(nested.End == parentheses.Source.ToBytePosition(unicodePrefix.Length + depth * 2 + 1 - i), "parenthesized byte end");
            nested = ((ParenthesizedTypeNode)nested).Type!;
        }
        Check(nested is TypeReferenceNode { TypeName: IdentifierNode { Text: "A" } }, "parenthesized leaf");

        SourceFileNode precedence = Parse("parenthesis-precedence.ts", "type T=((A)|B); type U=(A&(B|C))[];");
        Check(
            ((TypeAliasDeclarationNode)precedence.Statements![0]).Type is ParenthesizedTypeNode { Type: UnionTypeNode { Types.Count: 2 } union }
            && union.Types[0] is ParenthesizedTypeNode, "parenthesized union structure");

        SourceFileNode awaitSpans = Parse("await-spans.ts",
            "/*é😀*/export {}; await value; await (value); const untouched=1; await [value]; const last=2;");
        Check(awaitSpans.Statements!.Select(n => (n.Flags & NodeFlags.AwaitContext) != 0)
            .SequenceEqual([false, false, true, false, true, false]), "only ambiguous await statements are reparsed");
        Check(awaitSpans.DescendantsAndSelf().OfType<AwaitExpressionNode>().Count() == 3, "await expression shapes");

        SourceFileNode awaitOverlap = Parse("await-overlap.ts",
            "export {}; await\nvalue; const between=1; await (value); const after=2;");
        Check(awaitOverlap.Statements!.Select(n => (n.Flags & NodeFlags.AwaitContext) != 0)
            .SequenceEqual([false, true, true, true, false]), "await reparse extends to the next marked span");

        SourceFileNode awaitEnd = Parse("await-end.ts", "export {}; await\nvalue; const tail=1;");
        Check(awaitEnd.Statements!.Select(n => (n.Flags & NodeFlags.AwaitContext) != 0)
            .SequenceEqual([false, true, true]), "await reparse extends to EOF after consuming the last boundary");

        SourceFileNode signatures = Parse("index-signatures.ts", """
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
            """);
        var indices = signatures.DescendantsAndSelf().OfType<IndexSignatureDeclarationNode>().ToArray();
        Check(indices.Length == 8 && indices[0].Parameters is { Count: 1, HasTrailingComma: true }, "index signature trailing comma");
        Check(indices[1].Parameters![0] is ParameterDeclarationNode { QuestionToken: not null }
            && indices[2].Parameters![0] is ParameterDeclarationNode { DotDotDotToken: not null },
            "index parameter optional and rest syntax");
        Check(indices[3].Parameters?.Count == 2 && indices[4].Parameters?.Count == 0, "index parameter list recovery");
        Check(indices[5].Parameters![0] is ParameterDeclarationNode { Modifiers.Count: 1 }
            && indices[6].Parameters![0] is ParameterDeclarationNode { Name: IdentifierNode { Text: "readonly" } }
            && indices[7].Parameters![0] is ParameterDeclarationNode { Initializer: StringLiteralNode },
            "index parameter modifiers, name and initializer");
        Check(
            signatures.DescendantsAndSelf().Count(n => n is ComputedPropertyNameNode) == 2,
            "computed names stay distinct from index signatures");

        SourceFileNode nestedFunctions = Parse("generic-function-types.ts", """
            type F = Box<<T>(x: T) => T>;
            type G = import("module").Modifier<<T>(x: T) => T>;
            type H = (readonly: string) => string;
            type I<X> = any extends ((any extends any ? any : string) extends any ? import("./name").Name<X> : any) ? any : any;
            type J = import("module", { with: { type: "json" }, });
            type K = typeof f< <T>()=>T>;
            """);
        Check(nestedFunctions.DescendantsAndSelf().Count(n => n is FunctionTypeNode) == 4, "nested generic function types");
        Check(
            nestedFunctions.DescendantsAndSelf().Count(n => n is ConditionalTypeNode) == 3,
            "import type remains the true conditional branch");

        SourceFileNode heritage = Parse("heritage-expressions.ts", """
            class C {}
            interface I extends ns.Base<T>, (typeof C) {}
            class D extends C, {}
            class E implements ns.Base<T>, a?.b {}
            """);
        var bases = heritage.DescendantsAndSelf().OfType<HeritageClauseNode>().ToArray();
        Check(bases[0].Types![0] is TypeReferenceNode { TypeName: QualifiedNameNode }
            && bases[0].Types![1] is ExpressionWithTypeArgumentsNode { Expression: ParenthesizedExpressionNode },
            "interface heritage retains nonentity expressions");
        Check(
            bases[1].Types?.Count == 1 && bases[1].Types![0] is ExpressionWithTypeArgumentsNode { Expression: IdentifierNode },
            "class heritage trailing comma");
        Check(bases[2].Types![1] is ExpressionWithTypeArgumentsNode { Expression: PropertyAccessExpressionNode { Flags: var flags } }
            && (flags & NodeFlags.OptionalChain) != 0, "optional heritage chain is not converted to an entity name");

        SourceFileNode privateNames = Parse(
            "private-type-query.ts",
            "class C { #x = 0; get #v(){ return this.#x; } set #v(value){} get 1n(){ return 0; } get enum(){ return 0; } value: typeof this.#x; }");
        Check(
            privateNames.DescendantsAndSelf().OfType<TypeQueryNode>().Single().ExprName is QualifiedNameNode { Right: PrivateIdentifierNode },
            "type query preserves private identifier");
        Parse(
            "this-parameters.ts",
            "function f(this: C, value: string){} type F=(this:C, value:string)=>void; interface I { get value(): number {} }");
        (string Text, DiagnosticCode Diagnostic)[] invalidTypes =
        [
            ("var v:void.x;", DiagnosticCode.X0Expected),
            ("type T=typeof f<<A>()=>A>;", DiagnosticCode.X0Expected),
            ("function f(this?:C){}", DiagnosticCode.X0Expected),
            ("function f(this:C=foo){}", DiagnosticCode.X0Expected),
            ("function f(@dec this:C){}", DiagnosticCode.NeitherDecoratorsNorModifiersMayBeAppliedToThisParameters),
            ("function f(public this:C){}", DiagnosticCode.NeitherDecoratorsNorModifiersMayBeAppliedToThisParameters),
            ("interface I { f(): void {} }", DiagnosticCode.X0Expected),
            ("type F=A|()=>B;", DiagnosticCode.FunctionTypeNotationMustBeParenthesizedWhenUsedInAUnionType),
        ];
        foreach (var invalid in invalidTypes)
        {
            SourceFileNode file = Parser.ParseSourceFile(new("invalid-type.ts"), new(invalid.Text));
            Check(
                file.ParseDiagnostics.Any(d => d.Code == invalid.Diagnostic),
                $"invalid type syntax must report {invalid.Diagnostic}: {invalid.Text}");
        }
        (string Text, string[] Methods)[] memberRecovery =
        [
            ("class C { private a(): boolean { private b(): boolean {} }", ["a", "b"]),
            ("class Foo { f1(){ if(a.b){} public f2(){} f3(){} }", ["f1", "f2", "f3"]),
        ];
        foreach (var recovery in memberRecovery)
        {
            SourceFileNode file = Parser.ParseSourceFile(new("class-recovery.ts"), new(recovery.Text));
            var declaration = file.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single();
            Check(
                file.ParseDiagnostics.Count != 0
                    && declaration.Members!.OfType<MethodDeclarationNode>().Select(m => ((IdentifierNode)m.Name!).Text).SequenceEqual(recovery.Methods),
                "class body recovery retains following methods");
        }

        Type("tuples.ts", "[", "]", K.TupleType);
        SourceFileNode generic = Parse("generics.ts", "type T=" + Repeat("Box<", depth) + "A" + new string('>', depth) + ";");
        Check(generic.DescendantsAndSelf().Count(n => n is TypeReferenceNode) == depth + 1, "generic nesting preserved");
        Type("indexed-access.ts", "A[", "]", K.IndexedAccessType);
        Type("operators.ts", "keyof ", "", K.TypeOperator);
        Type("functions.ts", "()=>", "", K.FunctionType);
        Type("conditionals.ts", "A extends B ? C : ", "", K.ConditionalType);
        Type("mapped.ts", "{[P in K]:", "}", K.MappedType);
        Type("type-literals.ts", "{value:", "}", K.TypeLiteral);
        Type("template-types.ts", "`a${", "}`", K.TemplateLiteralType);

        (string Prefix, string Suffix)[] productions =
        [
            ("(", "|B)"), ("Box<", ">"), ("[", "]"), ("{value:", "}"),
            ("(x:", ")=>A"), ("A extends B ? ", " : C"), ("{[P in K]:", "}"), ("keyof (", ")"),
        ];
        var compound = new StringBuilder("type T=");
        for (int i = 0; i < depth; i++)
            compound.Append(productions[i % productions.Length].Prefix);
        compound.Append('A');
        for (int i = depth - 1; i >= 0; i--)
            compound.Append(productions[i % productions.Length].Suffix);
        compound.Append(';');
        SourceFileNode combined = Parse("compound-types.ts", compound.ToString());
        Check(MaxDepth(combined) >= depth, "compound type depth");

        Parse("blocks.ts", new string('{', depth) + ";" + new string('}', depth));
        Parse("if-statements.ts", Repeat("if(a)", depth) + ";");
        Parse("expression-parentheses.ts", "const x=" + new string('(', depth) + "1" + new string(')', depth) + ";");
        Parse("array-expressions.ts", "const x=" + new string('[', depth) + "1" + new string(']', depth) + ";");
        Parse("object-expressions.ts", "const x=" + Repeat("{a:", depth) + "1" + new string('}', depth) + ";");
        Parse("binary-expressions.ts", "const x=" + Repeat("a**", depth) + "a;");
        Parse("function-expressions.ts", "const x=" + Repeat("(function(){return ", depth) + "0" + Repeat(";})()", depth) + ";");
        Parse("jsx.tsx", "const x=" + Repeat("<a>", depth) + "text" + Repeat("</a>", depth) + ";", ScriptKind.TSX);
        string jsxName = Repeat("ns.", depth) + "Tag";
        Parse("jsx-qualified.tsx", "const x=<" + jsxName + "></" + jsxName + ">;", ScriptKind.TSX);
        SourceFileNode jsxThis = Parse(
            "jsx-names.tsx",
            "const x=<this.Item xml:lang={language} {...left,right}>{...items}<ns:tag/><this/></this.Item>; const y=<Component<number>/>;",
            ScriptKind.TSX);
        Check(
            jsxThis.DescendantsAndSelf().OfType<JsxSelfClosingElementNode>().Any(
                n => n.TagName is KeywordExpressionNode { Kind: K.ThisKeyword }),
            "JSX this tag keeps keyword semantics");
        (string Text, ScriptKind Kind, DiagnosticCode Diagnostic)[] invalidJsx =
        [
            ("<a></b>;", ScriptKind.TSX, DiagnosticCode.ExpectedCorrespondingJSXClosingTagFor0),
            ("<a:b></b>;", ScriptKind.TSX, DiagnosticCode.ExpectedCorrespondingJSXClosingTagFor0),
            ("<a.b.c></a>;", ScriptKind.TSX, DiagnosticCode.ExpectedCorrespondingJSXClosingTagFor0),
            ("<a><b></a>;", ScriptKind.TSX, DiagnosticCode.JSXElement0HasNoCorrespondingClosingTag),
            (@"<\u0061/>;", ScriptKind.TSX, DiagnosticCode.UnicodeEscapeSequenceCannotAppearHere),
            (@"<a data-\u0061/>;", ScriptKind.TSX, DiagnosticCode.UnicodeEscapeSequenceCannotAppearHere),
            ("<this.#private/>;", ScriptKind.TSX, DiagnosticCode.IdentifierExpected),
            ("<X a={...a}/>;", ScriptKind.TSX, DiagnosticCode.ExpressionExpected),
            ("<X<T>/>;", ScriptKind.JSX, DiagnosticCode.IdentifierExpected),
            ("<a/><b/>;", ScriptKind.TSX, DiagnosticCode.JSXExpressionsMustHaveOneParentElement),
        ];
        foreach (var invalid in invalidJsx)
        {
            SourceFileNode file = Parser.ParseSourceFile(new("invalid.tsx", invalid.Kind), new(invalid.Text));
            Check(
                file.ParseDiagnostics.Any(d => d.Code == invalid.Diagnostic),
                $"invalid JSX must report {invalid.Diagnostic}: {invalid.Text}");
        }
        Parse("nested.json", new string('[', depth) + "1" + new string(']', depth), ScriptKind.JSON);
        Parse("documentation.js", "/** @type {" + Repeat("Box<", depth) + "A" + new string('>', depth) + "} */ const x=0;", ScriptKind.JS);
        Parse("documentation-array.js", "/** @param {Object" + Repeat("[]", depth) + "} value */ function f(value){}", ScriptKind.JS);
        Parse(
            "documentation-host.js",
            "/** @returns {number} */ const f=" + new string('(', depth) + "()=>0" + new string(')', depth) + ";",
            ScriptKind.JS);
        SourceFileNode names = Parse(
            "documentation-names.js",
            "/** @param {Object} " + Repeat("a.", depth) + "value */ function f(value){}",
            ScriptKind.JS);
        Check(
            names.GetDocumentation(names.Statements![0]).SelectMany(n => n.DescendantsAndSelf()).Count(n => n is QualifiedNameNode) == depth,
            "documentation name depth");
        string qualified = Repeat("ns.", depth) + "Base";
        Parse(
            "documentation-heritage.js",
            "/** @extends {" + qualified + "<number>} */ class C extends " + qualified + " {}",
            ScriptKind.JS);

        const int propertyDepth = 384;
        var properties = new StringBuilder("/**\n");
        string propertyName = "value";
        for (int i = 0; i < propertyDepth; i++)
        {
            properties.Append(" * @param {Object} ").Append(propertyName).Append('\n');
            propertyName += ".p";
        }
        properties.Append(" * @param {number} ").Append(propertyName).Append("\n */ function f(value){}");
        SourceFileNode grouped = Parse("documentation-properties.js", properties.ToString(), ScriptKind.JS);
        Check(
            grouped.DescendantsAndSelf().Count(n => n is TypeLiteralNode) == propertyDepth,
            "documentation property grouping and cloning");
        SourceFileNode typedef = Parse(
            "documentation-typedef.js",
            "/** @typedef {Object} T\n * @property {Object} a\n * @property {number} a.b\n */",
            ScriptKind.JS);
        Check(typedef.DescendantsAndSelf().OfType<TypeLiteralNode>().First().Members is { Count: 1 } members
            && members[0] is PropertySignatureDeclarationNode { Type: TypeLiteralNode }, "typedef preserves nested property ownership");

        SynchronizationContext? previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
            Type("synchronization-context.ts", "Box<(", ")>", K.ParenthesizedType);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        var cancellationSource = new SourceText("type T=" + new string('(', 1_000_000) + "A" + new string(')', 1_000_000) + ";");
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
            "/** @type {" + new string('(', 1_000_000) + "A" + new string(')', 1_000_000) + "} */ const x=0;");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.CancelAfter(10);
            ExpectCancellation(documentationSource, cancelled.Token, ScriptKind.JS);
        }
        Console.WriteLine(
            $"Parser safety: {cases} cases, {nodes} nodes; depth {depth}, source positions, parents and cancellation passed.");
    }

    private static string Repeat(string value, int count)
    {
        var result = new StringBuilder(value.Length * count);
        for (int i = 0; i < count; i++)
            result.Append(value);
        return result.ToString();
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
        var identifier = factory.NewIdentifier("value");
        using var plus = Scalars(factory.NewPrefixUnaryExpression(K.PlusToken, identifier));
        using var minus = Scalars(factory.NewPrefixUnaryExpression(K.MinusToken, identifier));
        Check(plus.RootElement.GetProperty("Operator").GetInt32() == (int)K.PlusToken
            && minus.RootElement.GetProperty("Operator").GetInt32() == (int)K.MinusToken,
            "scalar oracle distinguishes unary operators with identical tree shape");
        using var typeOnly = Scalars(factory.NewImportSpecifier(true, null, identifier));
        using var valueImport = Scalars(factory.NewImportSpecifier(false, null, identifier));
        Check(
            typeOnly.RootElement.GetProperty("IsTypeOnly").GetBoolean() && !valueImport.RootElement.GetProperty("IsTypeOnly").GetBoolean(),
            "scalar oracle preserves import erasure semantics");
        using var phase = Scalars(factory.NewImportClause(K.DeferKeyword, identifier, null));
        Check(
            phase.RootElement.GetProperty("PhaseModifier").GetInt32() == (int)K.DeferKeyword,
            "scalar oracle preserves import evaluation phase");
        using var template = Scalars(factory.NewTemplateHead("\n", "\\n", TokenFlags.ContainsInvalidEscape));
        Check(Wtf8.DecodeString(template.RootElement.GetProperty("RawText").GetBytesFromBase64()) == "\\n"
            && template.RootElement.GetProperty("TemplateFlags").GetUInt32() == (uint)TokenFlags.ContainsInvalidEscape,
            "scalar oracle preserves raw template spelling and flags");
        Check(
            template.RootElement.EnumerateObject().Select(p => p.Name).SequenceEqual(["RawText", "TemplateFlags", "Text"]),
            "scalar property ordering is canonical");
        using var text = Scalars(factory.NewJSDocText(["a", "\ud800"]));
        Check(text.RootElement.GetProperty("Text").GetArrayLength() == 2
            && Wtf8.DecodeString(
                text.RootElement.GetProperty("Text")[1].GetBytesFromBase64()) == "\ud800",
            "scalar oracle preserves string chunks and unpaired surrogates");

        const string callText = "/*é😀*/f(1,);";
        SourceFileNode file = Parser.ParseSourceFile(new("lists.ts"), new(callText));
        CallExpressionNode call = file.DescendantsAndSelf().OfType<CallExpressionNode>().Single();
        using var listBuffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(listBuffer))
            AstScalarProperties.WriteLists(writer, call, file.Source.ToUtf16Position);
        using JsonDocument lists = JsonDocument.Parse(listBuffer.ToArray());
        JsonElement arguments = lists.RootElement.GetProperty("Arguments");
        Check(arguments[0].GetInt32() == 1 && arguments[1].GetInt32() == callText.IndexOf('(') + 1
            && arguments[2].GetInt32() == callText.IndexOf(')') && arguments[3].GetBoolean(),
            "list audit preserves Unicode positions and trailing comma");
        Check(
            lists.RootElement.GetProperty("TypeArguments").ValueKind == JsonValueKind.Null,
            "list audit distinguishes null from empty lists");
        var missing = new NodeList([], 3, 3, true);
        var empty = new NodeList([], 3, 3);
        var signature = factory.NewFunctionTypeNode(null, missing, null);
        FunctionTypeNode clonedSignature = signature.DeepClone<FunctionTypeNode>();
        Check(missing.IsMissing && !empty.IsMissing && clonedSignature.Parameters is { IsMissing: true, Count: 0 }
            && !ReferenceEquals(
                missing,
                clonedSignature.Parameters), "missing parameter lists survive cloning independently of empty lists");
        var parameterName = factory.NewIdentifier("value");
        var parameterType = factory.NewJSDocTypeExpression(factory.NewKeywordTypeNode(K.StringKeyword));
        var parameterTag = factory.NewJSDocParameterOrPropertyTag(
            K.JSDocParameterTag,
            factory.NewIdentifier("param"),
            parameterName,
            false,
            parameterType,
            false,
            null);
        Check(
            ReferenceEquals(parameterTag.GetChild(1), parameterType) && ReferenceEquals(parameterTag.GetChild(2), parameterName),
            "JSDoc type-first traversal follows source spelling");
        parameterTag.IsNameFirst = true;
        Check(
            ReferenceEquals(parameterTag.GetChild(1), parameterName) && ReferenceEquals(parameterTag.GetChild(2), parameterType),
            "JSDoc name-first traversal follows source spelling");
    }

    public static void RunSingleWorkerDocumentation() => VerifySingleWorkerDocumentation(true);

    private static void VerifySingleWorkerDocumentation(bool requireWorkerLimit)
    {
        const int depth = 21_000;
        var source = new SourceText(
            "/*é😀*/ /** @throws {" + new string('(', depth) + "import('module').T" + new string(')', depth) + "} */ function f(){}");
        ThreadPool.GetMinThreads(out int minimumWorkers, out int minimumIo);
        ThreadPool.GetMaxThreads(out int maximumWorkers, out int maximumIo);
        using var cancellation = new CancellationTokenSource();
        try
        {
            bool singleWorker = ThreadPool.SetMinThreads(1, minimumIo) && ThreadPool.SetMaxThreads(1, maximumIo);
            Check(!requireWorkerLimit || singleWorker, "strict single-worker test requires a configurable worker pool");
            Task verify = Task.Run(async () =>
            {
                SourceFileNode original = await Parser.ParseSourceFileAsync(
                    new("single-worker.js"),
                    source,
                    cancellation.Token).ConfigureAwait(false);
                Check(original.ParseDiagnostics.Count == 0 && original.Imports.Count == 1, "async JSDoc parser completes on one worker");
                SourceFileNode clone = original.DeepClone<SourceFileNode>();
                Check(
                    clone.Imports.Count == 1 && !ReferenceEquals(clone.Imports[0], original.Imports[0]),
                    "clone owns the documentation import reference");
                Check(
                    clone.Imports[0].Pos == original.Imports[0].Pos && clone.Imports[0].End == original.Imports[0].End,
                    "cloned documentation retains UTF-8 source positions");
                SyntaxNode owner = clone.Imports[0];
                while (owner.Parent is { } parent)
                    owner = parent;
                Check(ReferenceEquals(owner, clone), "cloned documentation import has complete parent ownership");
                IReadOnlyList<JSDocNode> comments = await clone.GetDocumentationAsync(
                    clone.Statements![0],
                    cancellation.Token).ConfigureAwait(false);
                Check(
                    comments.SelectMany(c => c.DescendantsAndSelf()).Any(n => ReferenceEquals(n, clone.Imports[0])),
                    "clone metadata reuses its cached documentation node");
                SourceFileNode typed = await Parser.ParseSourceFileAsync(
                    new("single-worker.ts"),
                    source,
                    cancellation.Token).ConfigureAwait(false);
                IReadOnlyList<JSDocNode> lazy = await typed.GetDocumentationAsync(
                    typed.Statements![0],
                    cancellation.Token).ConfigureAwait(false);
                Check(
                    lazy.Count == 1 && MaxDepth(lazy[0]) >= depth && ReferenceEquals(lazy[0].Parent, typed.Statements[0]),
                    "lazy TypeScript documentation parses on one worker");
                IReadOnlyList<JSDocNode> cached = await typed.GetDocumentationAsync(
                    typed.Statements[0],
                    cancellation.Token).ConfigureAwait(false);
                Check(ReferenceEquals(lazy[0], cached[0]), "async documentation preserves cached query identity");
                SourceFileNode aliases = await Parser.ParseSourceFileAsync(
                    new("alias.js"),
                    new("/** @typedef {number} T */ const x=1;"),
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
                    "synthetic aliases retain source-local documentation after cloning");
                SyntaxNode originalHost = originalComment.Parent!;
                SyntaxNode clonedHost = clonedComment.Parent!;
                Check(ReferenceEquals(aliases.GetDocumentation(originalHost).Single(), originalComment)
                    && ReferenceEquals(
                        (await clonedAliases.GetDocumentationAsync(clonedHost, cancellation.Token).ConfigureAwait(false)).Single(),
                        clonedComment), "cloned aliases share the cloned original host's comment identity");
            });
            Check(verify.Wait(TimeSpan.FromSeconds(10)), "async parsing and documentation cloning do not block the sole worker");
            verify.GetAwaiter().GetResult();
            Console.WriteLine(singleWorker
                ? "Async documentation: single-worker parsing, lazy queries, cloning and cache identity passed."
                : "Async documentation: default worker pool passed; worker limits unsupported, separate portable-pool single-worker gate required.");
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
            Parser.ParseSourceFile(new("cancel.ts", kind), source, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        throw new InvalidDataException("The production parser did not observe cancellation.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            throw new InvalidOperationException("Parser captured the caller's synchronization context.");

        public override void Send(SendOrPostCallback callback, object? state) =>
            throw new InvalidOperationException("Parser sent work to the caller's synchronization context.");
    }
}
