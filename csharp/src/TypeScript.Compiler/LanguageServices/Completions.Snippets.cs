using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Mapping;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private sealed record ClassMemberSnippet(Utf8String Text, DocumentTextEdit[]? Edits);

        private async ValueTask<bool> ClassMemberSnippetEligibleAsync(Symbol symbol)
        {
            if (JavaScript || kind != CompletionKind.Member || preferences.IncludeCompletionsWithClassMemberSnippets != true
                || (symbol.Flags & SymbolFlags.ClassMember & SymbolFlags.EnumMemberExcludes) == 0) return false;
            return SemanticSyntax.ClassLike(location) || location.Kind == K.SyntaxList && SemanticSyntax.ClassLike(location.Parent)
                || location.Parent is { } member && SemanticSyntax.ClassElement(member) && member.DeclarationName == location
                    && await LastTokenAsync(member) == location && SemanticSyntax.ClassLike(member.Parent);
        }

        private async ValueTask<ClassMemberSnippet?> ClassMemberSnippetAsync(Symbol symbol, Utf8String name)
        {
            if (Ancestor(location, SemanticSyntax.ClassLike) is not { } owner) return null;
            var (present, decorators, erase) = await PresentMemberModifiersAsync();
            bool abstractMember = (present & ModifierFlags.Abstract) != 0 && SemanticSyntax.HasModifier(owner, K.AbstractKeyword);
            var settings = await SourceFormatter.ForWritingAsync(preferences.FormatCodeSettings, File, cancellation);
            var printer = new SnippetPrinter(settings, options.EmitTargetYear);
            var imports = CreateImportAdder();
            var generator = new MissingMemberGenerator(checker, printer.Context, RenameQuote(File, preferences) == "'"u8, options.NoImplicitOverride == true, imports);
            var nodes = await generator.CreateAsync(symbol, owner, printer.Body(capabilities.Snippets), PreserveOptional.Property, abstractMember, cancellation);
            DocumentTextEdit[]? edits = erase is { } range ? [new(range, Utf8String.Empty)] : null;
            if (nodes.Count == 0) return new(name, edits);
            var modifiers = MemberModifiers.Flags(nodes[0]);
            if (abstractMember) modifiers |= ModifierFlags.Abstract;
            if (SemanticSyntax.ClassElement(nodes[0]) && await checker.MemberNeedsOverrideAsync(owner, nodes[0], symbol, cancellation)) modifiers |= ModifierFlags.Override;
            var allowed = modifiers | ModifierFlags.Override | ModifierFlags.Public
                | ((symbol.Flags & SymbolFlags.Method) != 0 ? ModifierFlags.Async : ModifierFlags.Ambient | ModifierFlags.Readonly);
            if ((present & ~allowed) != 0) return null;
            if ((modifiers & ModifierFlags.Protected) != 0 && (present & ModifierFlags.Public) != 0) modifiers &= ~ModifierFlags.Protected;
            if (present != 0 && (present & ModifierFlags.Public) == 0) modifiers &= ~ModifierFlags.Public;
            modifiers |= present;
            List<Utf8String> texts = [];
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node is IModifiedNode modified) modified.Modifiers = MemberModifiers.Create(printer.Factory, modifiers,
                    i == nodes.Count - 1 && node is PropertyDeclarationNode or MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode ? decorators : null);
                texts.Add(await printer.PrintAsync(node, File, cancellation));
            }
            var text = Utf8String.Join(settings.NewLineCharacter, texts);
            if (imports is not null && await imports.EditsAsync() is { Length: > 0 } importEdits) edits = [.. importEdits, .. edits ?? []];
            return text.IsEmpty ? null : new(text, edits);
        }

        private AutoImportView? autoImportView;
        private ImportAdder? CreateImportAdder() => File.FileName.StartsWith("^/"u8) ? null
            : (autoImportView ??= new(program, checker, projection, preferences, cancellation, cache: autoImportCache)).CreateAdder();

        private async ValueTask<(ModifierFlags Flags, IReadOnlyList<SyntaxNode> Decorators, DocumentRange? Erase)> PresentMemberModifiersAsync()
        {
            ModifierFlags flags = 0;
            List<SyntaxNode> decorators = [];
            int start = position, end = position;
            if (contextToken is null || Line(position) > Line(contextToken.End)) return (flags, decorators, null);
            if (contextToken.Parent is PropertyDeclarationNode property)
            {
                var contextFlag = MemberModifiers.Flag(KeywordOf(contextToken));
                if ((contextFlag & ModifierFlags.Modifier) == 0) return (flags, decorators, null);
                flags = MemberModifiers.Flags(property) & ModifierFlags.Modifier;
                foreach (var modifier in property.Modifiers ?? new([]))
                {
                    if (modifier is DecoratorNode) decorators.Add(modifier);
                    start = Math.Min(start, await StartAsync(modifier));
                }
                if ((flags & contextFlag) == 0) { flags |= contextFlag; start = Math.Min(start, await StartAsync(contextToken)); }
                if (property.Name != contextToken && property.Name is not null) end = await StartAsync(property.Name);
            }
            var mapped = start < end ? projection.ToRange(start, end) : default;
            return (flags, decorators, start < end && mapped.Fidelity == MappingFidelity.Exact ? mapped.Range : null);
        }

        private sealed record ObjectMethodSnippet(Utf8String Text, Utf8String Detail);

        private async ValueTask<ObjectMethodSnippet?> ObjectMethodSnippetAsync(Symbol symbol, SyntaxNode container)
        {
            if (JavaScript || (symbol.Flags & (SymbolFlags.Property | SymbolFlags.Method)) == 0
                || symbol.Declarations.FirstOrDefault() is not (PropertySignatureDeclarationNode or PropertyDeclarationNode or MethodSignatureDeclarationNode or MethodDeclarationNode)) return null;
            if (await checker.GetObjectMethodCompletionTypeAsync(symbol, container, cancellation) is not { } type) return null;
            var flags = NodeBuilderFlags.OmitThisParameter;
            if (RenameQuote(File, preferences) == "'"u8) flags |= NodeBuilderFlags.UseSingleQuotesForStringLiteralType;
            if (await checker.TypeToTypeNodeAsync(type, container, flags, cancellation) is not FunctionTypeNode signature) return null;
            var printer = new SnippetPrinter(preferences.FormatCodeSettings, options.EmitTargetYear);
            var factory = printer.Factory;
            List<SyntaxNode> parameters = [];
            foreach (var parameter in signature.Parameters ?? new([]))
            {
                var declaration = (ParameterDeclarationNode)parameter;
                parameters.Add(factory.NewParameterDeclaration(null, declaration.DotDotDotToken, declaration.Name!.DeepClone<SyntaxNode>(factory),
                    null, null, declaration.Initializer));
            }
            var method = factory.NewMethodDeclaration(null, null, symbol.Declarations[0].DeclarationName!.DeepClone<SyntaxNode>(factory),
                null, null, new([.. parameters]), null, null, printer.Body(capabilities.Snippets));
            var text = await printer.PrintAsync(method, File, cancellation) + ","u8;
            var label = factory.NewMethodSignatureDeclaration(null, factory.NewIdentifier(Utf8String.Empty), null, null, method.Parameters, null);
            var detail = new SyntaxPrinter(new() { RemoveComments = true, OmitTrailingSemicolon = true,
                NewLine = preferences.FormatCodeSettings.NewLineCharacter, TargetYear = options.EmitTargetYear }).Print(label, File, cancellation: cancellation);
            return new(text, detail);
        }
    }
}
