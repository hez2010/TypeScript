using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly Dictionary<SyntaxNode, Symbol?> jsxNamespaces = [];
    private readonly Dictionary<SourceFileNode, Symbol?> jsxImplicitModules = [];
    private readonly Dictionary<SourceFileNode, Type> jsxFragmentTypes = [];
    private readonly Dictionary<SyntaxNode, Type> jsxIntrinsicTypes = [];
    private readonly Dictionary<SyntaxNode, int> jsxReferenceKinds = [];
    private readonly Dictionary<TextSlice, TextSlice?> jsxFactoryNames = [];

    private int JsxMode => (int?)program.Symbols.Program.Configuration.Options.Number("jsx") ?? program.Symbols.Program.Configuration.Options.String("jsx") switch
    { "preserve" => 1, "react" => 2, "react-native" => 3, "react-jsx" => 4, "react-jsxdev" => 5, _ => 0 };

    private static SyntaxNode? JsxTag(SyntaxNode node) => node switch
    { JsxOpeningElementNode opening => opening.TagName, JsxSelfClosingElementNode self => self.TagName, JsxClosingElementNode closing => closing.TagName, _ => null };

    private static JsxAttributesNode? JsxAttributes(SyntaxNode node) => node switch
    { JsxOpeningElementNode opening => opening.Attributes, JsxSelfClosingElementNode self => self.Attributes, _ => null };

    private static bool JsxOpening(SyntaxNode node) => node is JsxOpeningElementNode or JsxSelfClosingElementNode or JsxOpeningFragmentNode;

    private static bool IntrinsicJsx(SyntaxNode? node) => node is JsxNamespacedNameNode
            || node is IdentifierNode identifier
                && (identifier.Text.Length != 0 && identifier.Text[0] is >= 'a' and <= 'z' || identifier.Text.Span.Contains('-'));

    private static TextSlice JsxName(SyntaxNode node) => node is JsxNamespacedNameNode namespaced
        ? TextSlice.Concat(((IdentifierNode)namespaced.Namespace!).Text, ":", ((IdentifierNode)namespaced.Name!).Text) : SyntaxNameText.Get(node);

    private static TextSlice? JsxPragma(SourceFileNode file, TextSlice name) =>
        TextSlice.FromNullable(file.Pragmas.LastOrDefault(p => p.Name == name)?.Arguments.GetValueOrDefault("factory")?.Value);

    private TextSlice JsxFactoryName(SyntaxNode node, bool fragment = false)
    {
        var file = SemanticSyntax.Source(node)!;
        var options = program.Symbols.Program.Configuration.Options;
        if (fragment && ValidJsxFactory(JsxPragma(file, "jsxfrag") ?? TextSlice.FromNullable(options.String("jsxFragmentFactory"))) is { } fragmentFactory)
            return CacheEmitJsxFactory(fragmentFactory, file, true);
        if (!fragment && ValidJsxFactory(JsxPragma(file, "jsx")) is { } local)
            return CacheEmitJsxFactory(local, file);
        return CacheEmitJsxFactory(
            options.String("jsxFactory") is { Length: > 0 } configured ? ValidJsxFactory(configured) ?? "React.createElement"
            : TextSlice.Concat((options.String("reactNamespace") is { Length: > 0 } reactNamespace ? reactNamespace : "React"), ".createElement"));
    }

    private TextSlice? ValidJsxFactory(TextSlice? text)
    {
        if (text is null or { IsEmpty: true })
            return null;
        if (!jsxFactoryNames.TryGetValue((text).Value, out var name))
            jsxFactoryNames[(text).Value] = name = Parser.ParseIsolatedEntityName((text).Value) is { } entity ? SyntaxNameText.Get(entity) : (TextSlice?)null;
        return name;
    }

    private static TextSlice JsxFactoryRoot(TextSlice name)
    {
        int dot = name.Span.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    private static SyntaxNode JsxFactoryEntity(TextSlice text, SyntaxNode location)
    {
        var parts = text.Span.Split('.');
        parts.MoveNext();
        SyntaxNode result = new IdentifierNode { Text = text[parts.Current], Pos = -1, End = -1, Flags = NodeFlags.Synthesized };
        while (parts.MoveNext())
        {
            var right = new IdentifierNode { Text = text[parts.Current], Pos = -1, End = -1, Flags = NodeFlags.Synthesized };
            var qualified = new QualifiedNameNode { Left = result, Right = right, Pos = -1, End = -1, Flags = NodeFlags.Synthesized };
            result.Parent = right.Parent = qualified;
            result = qualified;
        }
        result.Parent = location;
        return result;
    }

    private async ValueTask<Symbol?> JsxImplicitModuleAsync(SyntaxNode location, CancellationToken cancellation)
    {
        var file = SemanticSyntax.Source(location)!;
        if (jsxImplicitModules.TryGetValue(file, out var cached))
            return cached;
        TextSlice? runtime = JsxPragma(file, "jsxruntime");
        var options = program.Symbols.Program.Configuration.Options;
        if (runtime == "classic" || JsxMode is not (4 or 5) && options.String("jsxImportSource") is null
            && JsxPragma(file, "jsximportsource") is null && runtime != "automatic")
            return null;
        TextSlice source = JsxPragma(file, "jsximportsource") ?? options.String("jsxImportSource") ?? "react";
        TextSlice name = TextSlice.Concat(source, (JsxMode == 5 ? "/jsx-dev-runtime" : "/jsx-runtime"));
        var first = file.DescendantsAndSelf().FirstOrDefault(n => n is JsxElementNode or JsxSelfClosingElementNode or JsxFragmentNode);
        var errorNode = first is JsxFragmentNode fragment ? fragment.OpeningFragment! : first ?? location;
        var specifier = new StringLiteralNode
        {
            Text = name,
            Parent = errorNode,
            Pos = errorNode.Pos,
            End = errorNode.End,
            Flags = NodeFlags.Synthesized
        };
        var module = await ResolveImportModuleAsync(
            errorNode,
            specifier,
            null,
            cancellation,
            true,
            DiagnosticCode.ThisJSXTagRequiresTheModulePath0ToExistButNoneCouldBeFoundMakeSureYouHaveTypesForTheAppropriatePackageInstalled);
        if (module is not null)
            module = await program.Aliases.SymbolAsync(module, cancellation: cancellation);
        cancellation.ThrowIfCancellationRequested();
        return jsxImplicitModules[file] = module;
    }

    private async ValueTask<Symbol?> JsxNamespaceAsync(SyntaxNode location, CancellationToken cancellation)
    {
        if (jsxNamespaces.TryGetValue(location, out var cached))
            return cached;
        var container = await JsxImplicitModuleAsync(location, cancellation);
        if (container is null || container == UnknownSymbol)
        {
            TextSlice name = JsxFactoryRoot(JsxFactoryName(location, location is JsxOpeningFragmentNode));
            container = program.Symbols.NameResolver(cancellation).Resolve(location, name, SymbolFlags.Namespace);
        }
        Symbol? result = null;
        if (container is not null)
        {
            container = await program.Aliases.SymbolAsync(container, cancellation: cancellation);
            if (container is not null)
                result = await program.Aliases.SymbolAsync(
                    program.Symbols.Lookup(await program.ExportsAsync(container, cancellation), "JSX", SymbolFlags.Namespace),
                    cancellation: cancellation);
        }
        if (result is null || result == UnknownSymbol)
            result = await program.Aliases.SymbolAsync(
                program.Symbols.Lookup(program.Symbols.Globals, "JSX", SymbolFlags.Namespace),
                cancellation: cancellation);
        cancellation.ThrowIfCancellationRequested();
        return jsxNamespaces[location] = result == UnknownSymbol ? null : result;
    }

    private async ValueTask<Type> JsxTypeAsync(TextSlice name, SyntaxNode location, CancellationToken cancellation)
    {
        if (await JsxNamespaceAsync(location, cancellation) is { } ns
            && program.Symbols.Lookup(await program.ExportsAsync(ns, cancellation), name, SymbolFlags.Type) is { } symbol)
            return await Declared.GetAsync(symbol, cancellation);
        return context.ErrorType;
    }

    private async ValueTask<Type> JsxIntrinsicTypeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (jsxIntrinsicTypes.TryGetValue(node, out var cached))
            return cached;
        var intrinsic = await JsxTypeAsync("IntrinsicElements", node, cancellation);
        Symbol? symbol = null;
        Type result = context.ErrorType;
        if (intrinsic != context.ErrorType)
        {
            TextSlice name = JsxName(JsxTag(node)!);
            symbol = await Properties.PropertyAsync(intrinsic, name, cancellation: cancellation);
            if (symbol is not null)
                result = await Values.GetAsync(symbol, cancellation);
            else if (await ApplicableIndexAsync(intrinsic, name, cancellation) is { } index)
            {
                symbol = intrinsic.Symbol;
                result = index.ValueType;
            }
            else
                Error(node, DiagnosticCode.Property0DoesNotExistOnType1, name, "JSX.IntrinsicElements");
        }
        else if (NoImplicitAny)
            Error(node, DiagnosticCode.JSXElementImplicitlyHasTypeAnyBecauseNoInterfaceJSX0Exists, "IntrinsicElements");
        links.SymbolNodes.Get(node).ResolvedSymbol = symbol ?? UnknownSymbol;
        return jsxIntrinsicTypes[node] = result;
    }

    private async ValueTask<TextSlice?> JsxPropertyNameAsync(TextSlice container, SyntaxNode node, CancellationToken cancellation)
    {
        if (container == "ElementChildrenAttribute" && JsxMode is 4 or 5)
            return "children";
        var type = await JsxTypeAsync(container, node, cancellation);
        if (type == context.ErrorType)
            return null;
        var properties = await Properties.GetAsync(type, cancellation);
        if (properties.Count == 0)
            return "";
        if (properties.Count == 1)
            return properties[0].Name;
        if (type.Symbol?.Declarations.FirstOrDefault() is { } declaration)
            Error(declaration, DiagnosticCode.TheGlobalTypeJSX0MayNotHaveMoreThanOneProperty, container);
        return null;
    }
}
