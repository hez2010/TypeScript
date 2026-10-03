using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed record CodeLensData(Utf8String Kind, Utf8String Uri, int Position, int? SupplementalFileIndex = null);
public sealed record CodeLensCommand(Utf8String Title, Utf8String Command, IReadOnlyList<DocumentLocation>? Locations = null);
public sealed record CodeLens(DocumentRange Range, CodeLensData Data, CodeLensCommand? Command = null);

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<CodeLens[]?> GetCodeLensesAsync(ProjectSnapshot project, CodeLensPreferences? preferences = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        preferences ??= new();
        if (preferences.ReferencesEnabled != true && preferences.ImplementationsEnabled != true) return null;
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request);
        List<CodeLens> result = [];
        HashSet<(Utf8String, DocumentRange)> seen = [];
        for (int index = 0; index < projections.Length; index++)
        {
            var projection = projections[index];
            var scanner = new Scanner(projection.File.Source);
            Symbol? last = null;
            Stack<(SyntaxNode? Node, Symbol? Restore)> pending = new();
            pending.Push((projection.File, null));
            while (pending.TryPop(out var frame))
            {
                cancellation.ThrowIfCancellationRequested();
                if (frame.Node is not { } node) { last = frame.Restore; continue; }
                if (node.BindingSymbol != last)
                {
                    last = node.BindingSymbol;
                    if (preferences.ReferencesEnabled == true && ValidReferenceLens(node, preferences)) Add("references"u8);
                    if (preferences.ImplementationsEnabled == true && ValidImplementationLens(node, preferences)) Add("implementations"u8);
                }
                pending.Push((null, last));
                for (int child = node.ChildCount - 1; child >= 0; child--) pending.Push((node.GetChild(child), null));

                void Add(Utf8String kind)
                {
                    int start = scanner.SkipTriviaAt((node.DeclarationName ?? node).Pos);
                    var range = projection.ToRange(start, node.End, MappingFeature.CodeLens);
                    if (range.Fidelity != MappingFidelity.None && seen.Add((kind, range.Range)))
                        result.Add(new(range.Range, new(kind, DocumentUris.FromFileName(projections[0].OriginalFileName), start, index == 0 ? null : index - 1)));
                }
            }
        }
        return result.ToArray();
    }

    public async ValueTask<CodeLens> ResolveCodeLensAsync(ProjectSnapshot project, CodeLens lens, Utf8String? showLocationsCommand = null,
        CancellationToken cancellation = default)
    {
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request);
        var source = CodeLensSource(lens.Data);
        bool implementations = lens.Data.Kind == "implementations"u8;
        if (!implementations && lens.Data.Kind != "references"u8) return ResolveCodeLens(lens, [], showLocationsCommand);
        var data = await ReferenceDataAsync(project, lens.Range.Start, new(ReferenceUse.References, Implementations: implementations),
            lease.Checker, cancellation, (source, lens.Data.Position));
        var locations = implementations
            ? (await ImplementationLocationsAsync(project, data, false, cancellation, dropOriginNodes: true)).Select(item => new DocumentLocation(item.Uri, item.SelectionRange)).ToArray()
            : await ReferenceLocationsAsync(project, data, false, cancellation);
        return ResolveCodeLens(lens, locations, showLocationsCommand);
    }

    internal SourceFileNode CodeLensSource(CodeLensData data)
    {
        int index = data.SupplementalFileIndex is { } supplemental ? supplemental + 1 : 0;
        if (index < 0 || index >= projections.Length || data.SupplementalFileIndex < 0)
            throw new ArgumentException($"supplemental source file index not found: {data.SupplementalFileIndex}");
        return projections[index].File;
    }

    internal static CodeLens ResolveCodeLens(CodeLens lens, IReadOnlyList<DocumentLocation> locations, Utf8String? showLocationsCommand)
    {
        var message = lens.Data.Kind == "references"u8 ? locations.Count == 1 ? Messages.X_1_reference : Messages.X_0_references
            : lens.Data.Kind == "implementations"u8 ? locations.Count == 1 ? Messages.X_1_implementation : Messages.X_0_implementations : null;
        var title = message?.Format(null, Utf8String.FromString(locations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))) ?? default;
        bool arguments = locations.Count != 0 && showLocationsCommand.HasValue;
        return lens with { Command = new(title, arguments ? showLocationsCommand!.Value : default, arguments ? locations : null) };
    }

    private static bool ValidReferenceLens(SyntaxNode node, CodeLensPreferences preferences) => node.Kind switch
    {
        K.FunctionDeclaration when preferences.ShowOnAllFunctions == true => true,
        K.FunctionDeclaration or K.VariableDeclaration => SemanticSyntax.HasModifier(node, K.ExportKeyword)
            || node is VariableDeclarationNode && node.Parent?.Parent is { } statement && SemanticSyntax.HasModifier(statement, K.ExportKeyword),
        K.ClassDeclaration or K.InterfaceDeclaration or K.TypeAliasDeclaration or K.EnumDeclaration or K.EnumMember => true,
        K.MethodDeclaration or K.MethodSignature or K.Constructor or K.GetAccessor or K.SetAccessor or K.PropertyDeclaration or K.PropertySignature =>
            node.Parent?.Kind is K.ClassDeclaration or K.InterfaceDeclaration or K.TypeLiteral,
        _ => false,
    };

    private static bool ValidImplementationLens(SyntaxNode node, CodeLensPreferences preferences) => node.Kind switch
    {
        K.InterfaceDeclaration => true,
        K.MethodSignature => preferences.ShowOnInterfaceMethods == true && node.Parent is InterfaceDeclarationNode,
        K.MethodDeclaration when preferences.ShowOnAllClassMethods == true && node.Parent is ClassDeclarationNode =>
            !SemanticSyntax.HasModifier(node, K.PrivateKeyword) && node.DeclarationName is not PrivateIdentifierNode,
        K.MethodDeclaration or K.ClassDeclaration or K.Constructor or K.GetAccessor or K.SetAccessor or K.PropertyDeclaration => SemanticSyntax.HasModifier(node, K.AbstractKeyword),
        _ => false,
    };
}
