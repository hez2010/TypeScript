using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
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
    private readonly Dictionary<Utf8String, Utf8String?> jsxFactoryNames = [];

    private int JsxMode => (int)program.Symbols.Program.Configuration.Options.Jsx;

    private static SyntaxNode? JsxTag(SyntaxNode node) => node switch
    { JsxOpeningElementNode opening => opening.TagName, JsxSelfClosingElementNode self => self.TagName, JsxClosingElementNode closing => closing.TagName, _ => null };

    private static JsxAttributesNode? JsxAttributes(SyntaxNode node) => node switch
    { JsxOpeningElementNode opening => opening.Attributes, JsxSelfClosingElementNode self => self.Attributes, _ => null };

    private static bool JsxOpening(SyntaxNode node) => node is JsxOpeningElementNode or JsxSelfClosingElementNode or JsxOpeningFragmentNode;

    private static bool IntrinsicJsx(SyntaxNode? node) => node is JsxNamespacedNameNode
            || node is IdentifierNode identifier
                && (identifier.Text.Length != 0 && identifier.Text[0] is >= (byte)'a' and <= (byte)'z' || identifier.Text.Span.Contains((byte)'-'));

    private static Utf8String JsxName(SyntaxNode node) => node is JsxNamespacedNameNode namespaced
        ? Utf8String.Concat(((IdentifierNode)namespaced.Namespace!).Text, ":"u8, ((IdentifierNode)namespaced.Name!).Text) : SyntaxNameText.Get(node);

    private static Utf8String? JsxPragma(SourceFileNode file, Utf8String name) =>
        file.Pragmas.LastOrDefault(p => p.Name == name).Arguments?.GetValueOrDefault(Utf8Literals.Factory).Value;

    private Utf8String JsxFactoryName(SyntaxNode node, bool fragment = false)
    {
        var file = SemanticSyntax.Source(node)!;
        var options = program.Symbols.Program.Configuration.Options;
        if (fragment && ValidJsxFactory(JsxPragma(file, Utf8Literals.Jsxfrag) ?? options.JsxFragmentFactory) is { } fragmentFactory)
            return CacheEmitJsxFactory(fragmentFactory, file, true);
        if (!fragment && ValidJsxFactory(JsxPragma(file, Utf8Literals.JsxKeyword)) is { } local)
            return CacheEmitJsxFactory(local, file);
        return CacheEmitJsxFactory(
            options.JsxFactory is { Length: > 0 } configured ? ValidJsxFactory(configured) ?? Utf8Literals.ReactCreateElement
            : Utf8String.Concat(options.ReactNamespace is { Length: > 0 } reactNamespace ? reactNamespace : "React"u8, ".createElement"u8));
    }

    private Utf8String? ValidJsxFactory(Utf8String? text)
    {
        if (text is null or { IsEmpty: true })
            return null;
        if (!jsxFactoryNames.TryGetValue(text.Value, out var name))
            jsxFactoryNames[text.Value] = name = Parser.ParseIsolatedEntityName(text.Value) is { } entity ? SyntaxNameText.Get(entity) : (Utf8String?)null;
        return name;
    }

    private static Utf8String JsxFactoryRoot(Utf8String name)
    {
        int dot = name.Span.IndexOf((byte)'.');
        return dot < 0 ? name : name[..dot];
    }

    private static SyntaxNode JsxFactoryEntity(Utf8String text, SyntaxNode location)
    {
        var parts = text.Span.Split((byte)'.');
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
        Utf8String? runtime = JsxPragma(file, Utf8Literals.Jsxruntime);
        var options = program.Symbols.Program.Configuration.Options;
        if (runtime == Utf8Literals.Classic || JsxMode is not (4 or 5) && options.JsxImportSource is null
            && JsxPragma(file, Utf8Literals.Jsximportsource) is null && runtime != Utf8Literals.Automatic)
            return null;
        Utf8String source = JsxPragma(file, Utf8Literals.Jsximportsource) ?? options.JsxImportSource ?? Utf8Literals.React;
        Utf8String name = Utf8String.Concat(source, JsxMode == 5 ? "/jsx-dev-runtime"u8 : "/jsx-runtime"u8);
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
            Utf8String name = JsxFactoryRoot(JsxFactoryName(location, location is JsxOpeningFragmentNode));
            container = program.Symbols.NameResolver(cancellation).Resolve(location, name, SymbolFlags.Namespace);
        }
        Symbol? result = null;
        if (container is not null)
        {
            container = await program.Aliases.SymbolAsync(container, cancellation: cancellation);
            if (container is not null)
                result = await program.Aliases.SymbolAsync(
                    program.Symbols.Lookup(await program.ExportsAsync(container, cancellation), Utf8Literals.JSX, SymbolFlags.Namespace),
                    cancellation: cancellation);
        }
        if (result is null || result == UnknownSymbol)
            result = await program.Aliases.SymbolAsync(
                program.Symbols.Lookup(program.Symbols.Globals, Utf8Literals.JSX, SymbolFlags.Namespace),
                cancellation: cancellation);
        cancellation.ThrowIfCancellationRequested();
        return jsxNamespaces[location] = result == UnknownSymbol ? null : result;
    }

    private async ValueTask<Type> JsxTypeAsync(Utf8String name, SyntaxNode location, CancellationToken cancellation)
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
        var intrinsic = await JsxTypeAsync(Utf8Literals.IntrinsicElements, node, cancellation);
        Symbol? symbol = null;
        Type result = context.ErrorType;
        if (intrinsic != context.ErrorType)
        {
            Utf8String name = JsxName(JsxTag(node)!);
            symbol = await Properties.PropertyAsync(intrinsic, name, cancellation: cancellation);
            if (symbol is not null)
                result = await Values.GetAsync(symbol, cancellation);
            else if (await ApplicableIndexAsync(intrinsic, name, cancellation) is { } index)
            {
                symbol = await ApplicableIndexSymbolAsync(intrinsic, context.GetStringLiteralType(name), cancellation) ?? intrinsic.Symbol;
                result = index.ValueType;
            }
            else
                Error(node, DiagnosticCode.Property0DoesNotExistOnType1, name, Utf8Literals.JSXIntrinsicElements);
        }
        else if (NoImplicitAny)
            Error(node, DiagnosticCode.JSXElementImplicitlyHasTypeAnyBecauseNoInterfaceJSX0Exists, Utf8Literals.IntrinsicElements);
        links.SymbolNodes.Get(node).ResolvedSymbol = symbol ?? UnknownSymbol;
        return jsxIntrinsicTypes[node] = result;
    }

    private async ValueTask<Utf8String?> JsxPropertyNameAsync(Utf8String container, SyntaxNode node, CancellationToken cancellation)
    {
        if (container == Utf8Literals.ElementChildrenAttribute && JsxMode is 4 or 5)
            return Utf8Literals.Children;
        var type = await JsxTypeAsync(container, node, cancellation);
        if (type == context.ErrorType)
            return null;
        var properties = await Properties.GetAsync(type, cancellation);
        if (properties.Count == 0)
            return Utf8String.Empty;
        if (properties.Count == 1)
            return properties[0].Name;
        if (type.Symbol?.Declarations.FirstOrDefault() is { } declaration)
            Error(declaration, DiagnosticCode.TheGlobalTypeJSX0MayNotHaveMoreThanOneProperty, container);
        return null;
    }
}
