using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class CodeFixQuery
{
    private static readonly int[] InterfaceErrorCodes = [
        (int)Messages.Class_0_incorrectly_implements_interface_1.Code,
        (int)Messages.Class_0_incorrectly_implements_class_1_Did_you_mean_to_extend_1_and_inherit_its_members_as_a_subclass.Code,
    ];

    private async ValueTask<IReadOnlyList<CodeFix>> InterfacesAsync(int position)
    {
        var owner = await ContainingClassAsync(position);
        if (owner is null) return [];
        List<CodeFix> result = [];
        foreach (var implemented in Heritage(owner, K.ImplementsKeyword))
        {
            var tracker = new SourceEditTracker(projection, preferences.FormatCodeSettings, cancellation);
            var adder = File.FileName.StartsWith("^/"u8) ? null : Imports.CreateAdder();
            await ImplementAsync(owner, implemented, tracker, adder);
            var changes = await MemberChangesAsync(tracker, adder);
            if (changes.Length != 0) result.Add(new(Messages.Implement_interface_0.Format(preferences.Locale,
                File.Source.Text[SmartIndenter.Start(implemented, File)..implemented.End]), changes));
        }
        return result;
    }

    private async ValueTask<CodeFix?> AllInterfacesAsync()
    {
        var tracker = new SourceEditTracker(projection, preferences.FormatCodeSettings, cancellation);
        var adder = File.FileName.StartsWith("^/"u8) ? null : Imports.CreateAdder();
        HashSet<SyntaxNode> seen = [];
        foreach (var diagnostic in await DiagnosticsAsync())
            if (Matches(diagnostic, InterfaceErrorCodes) && await ContainingClassAsync(diagnostic.Start) is { } owner && seen.Add(owner))
                foreach (var implemented in Heritage(owner, K.ImplementsKeyword)) await ImplementAsync(owner, implemented, tracker, adder);
        var changes = await MemberChangesAsync(tracker, adder);
        return changes.Length == 0 ? null : new(Messages.Implement_all_unimplemented_interfaces.Format(preferences.Locale), changes);
    }

    private async ValueTask ImplementAsync(SyntaxNode owner, SyntaxNode implemented, SourceEditTracker tracker, ImportAdder? adder)
    {
        var factory = tracker.Factory;
        bool singleQuotes = new ImportEditor(tracker, File, Options, preferences, cancellation).SingleQuotes();
        var generator = new MissingMemberGenerator(checker, tracker.Context, singleQuotes, Options.NoImplicitOverride == true, adder);
        var body = factory.NewBlock(new([factory.NewThrowStatement(factory.NewNewExpression(factory.NewIdentifier("Error"u8), null,
            new([factory.NewStringLiteral(Messages.Method_not_implemented.Format(preferences.Locale), singleQuotes ? TokenFlags.SingleQuote : 0)])))]), true);
        var implementedType = await checker.GetTypeAtLocationAsync(implemented, cancellation);
        var classType = await checker.GetTypeAtLocationAsync(owner, cancellation);
        var indexes = await checker.IndexesAsync(classType, cancellation);
        var implementedIndexes = await checker.IndexesAsync(implementedType, cancellation);
        var constructor = Members(owner)?.OfType<ConstructorDeclarationNode>().FirstOrDefault();
        foreach (var key in new[] { checker.Context.NumberType, checker.Context.StringType })
            if (!indexes.Any(index => index.KeyType == key) && implementedIndexes.FirstOrDefault(index => index.KeyType == key) is { } info
                && await checker.CreateIndexSignatureAsync(info, owner, tracker.Context, cancellation) is { } member) await InsertAsync(member);
        HashSet<Utf8String> existing = checker.Symbols.Declaration(owner)?.MemberTable.Keys.ToHashSet() ?? [];
        if (Heritage(owner, K.ExtendsKeyword).FirstOrDefault() is { } extends)
            foreach (var symbol in await checker.PropertiesAsync(await checker.GetTypeAtLocationAsync(extends, cancellation), cancellation))
                if ((MemberModifiers.FromSymbol(symbol) & ModifierFlags.Private) == 0) existing.Add(symbol.Name);
        foreach (var symbol in await checker.PropertiesAsync(implementedType, cancellation))
            if (!existing.Contains(symbol.Name) && (MemberModifiers.FromSymbol(symbol) & ModifierFlags.Private) == 0)
            {
                existing.Add(symbol.Name);
                foreach (var member in await generator.CreateAsync(symbol, owner, body, PreserveOptional.All, false, cancellation)) await InsertAsync(member);
            }

        async ValueTask InsertAsync(SyntaxNode member)
        {
            if (constructor is null) await tracker.InsertMemberAtStartAsync(owner, Members(owner)!, member);
            else tracker.InsertAfter(constructor, [member]);
        }
    }

    private async ValueTask<DocumentTextEdit[]> MemberChangesAsync(SourceEditTracker tracker, ImportAdder? adder)
    {
        var changes = await tracker.GetChangesAsync();
        if (!tracker.IsMappable) return [];
        return adder?.HasFixes == true ? [.. changes, .. await adder.EditsAsync()] : changes;
    }

    private async ValueTask<SyntaxNode?> ContainingClassAsync(int position)
    {
        var token = await SyntaxNavigation.GetTokenAtPositionAsync(File, position, cancellation);
        for (var node = token; node is not null; node = node.Parent) if (SemanticSyntax.ClassLike(node)) return node;
        return null;
    }

    private static NodeList? Members(SyntaxNode node) => node switch { ClassDeclarationNode declaration => declaration.Members, ClassExpressionNode expression => expression.Members, _ => null };
    private static IEnumerable<SyntaxNode> Heritage(SyntaxNode node, K kind) =>
        (node switch { ClassDeclarationNode declaration => declaration.HeritageClauses, ClassExpressionNode expression => expression.HeritageClauses, _ => null } ?? new([]))
            .OfType<HeritageClauseNode>().Where(clause => clause.Token == kind).SelectMany(clause => clause.Types ?? new([]));
}
