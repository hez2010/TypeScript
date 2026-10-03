using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.LanguageServices;

public sealed record SignatureHelpParameter(Utf8String Label, Utf8String Documentation);
public sealed record SignatureHelpInformation(Utf8String Label, Utf8String Documentation,
    IReadOnlyList<SignatureHelpParameter> Parameters, int? ActiveParameter, bool HasActiveParameter,
    IReadOnlyList<ClassifiedTextRun>? Runs);
public sealed record SignatureHelp(IReadOnlyList<SignatureHelpInformation> Signatures, int ActiveSignature,
    int? ActiveParameter, bool HasActiveParameter, bool Markdown);
public sealed record SignatureHelpOptions
{
    public bool Markdown { get; init; }
    public bool SupportsVisualStudio { get; init; }
    public bool ActiveParameterSupport { get; init; }
    public bool NoActiveParameterSupport { get; init; }
    public int? TriggerKind { get; init; }
    public Utf8String TriggerCharacter { get; init; }
    public bool IsRetrigger { get; init; }
}

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<SignatureHelp?> GetSignatureHelpAsync(ProjectSnapshot project, DocumentPosition position,
        SignatureHelpOptions? options = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (project.Program is null) throw new ArgumentException("Project has no program", nameof(project));
        options ??= new();
        using var request = new ProjectRequest(cancellation);
        foreach (var (projection, mapped) in FromPosition(position, MappingFeature.SignatureHelp))
        {
            if (mapped.Fidelity is not (MappingFidelity.Exact or MappingFidelity.Atom)) continue;
            using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request, projection.File).ConfigureAwait(false);
            if (await new SignatureHelpQuery(lease.Checker, project.Program, projection.File, options, cancellation, MapLocation)
                .GetAsync(mapped.Position) is { } result) return result;

            (Utf8String, DocumentRange, MappingFidelity) MapLocation(SourceFileNode source, int start, int end)
            {
                var programFile = project.Program.GetFile(source.FileName);
                var name = programFile?.Mapping is null ? source.FileName : project.Program.SourceFiles
                    .FirstOrDefault(file => file.SupplementalSourceFiles.Contains(source.FileName))?.Syntax.FileName ?? source.FileName;
                var range = new DocumentProjection(source, programFile?.Mapping, projection.Encoding, name)
                    .ToRange(start, end, MappingFeature.SignatureHelp);
                return (DocumentUris.FromFileName(name), range.Range, range.Fidelity);
            }
        }
        return null;
    }

    private sealed partial class SignatureHelpQuery(Checker checker, CompilerProgram program, SourceFileNode file, SignatureHelpOptions options,
        CancellationToken cancellation, DocumentationLocationMapper mapper)
    {
        private sealed record ArgumentInfo(SyntaxNode Node, int Start, int End, int Index, int Count,
            bool TypeArguments = false, bool IncompleteTypeArguments = false, Signature? Contextual = null, Symbol? Symbol = null);
        private sealed record ListInfo(NodeList? List, int Start, int End, int Index, int Count);

        internal async ValueTask<SignatureHelp?> GetAsync(int position)
        {
            var token = await SyntaxNavigation.FindPrecedingTokenAsync(file, position, cancellation: cancellation);
            if (token is null) return null;
            int trigger = options.TriggerKind switch
            {
                null => 0, 2 when options.TriggerCharacter.IsEmpty => 1,
                2 or 3 => options.IsRetrigger ? 3 : 2, _ => 1,
            };
            bool syntacticOnly = trigger == 2;
            if (syntacticOnly && (await InsideStringAsync(token, position) || await InCommentAsync(file, position, token, cancellation))) return null;
            ArgumentInfo? info = null;
            for (var node = token; node is not null and not SourceFileNode && (trigger == 1 || node is not BlockNode); node = node.Parent)
            {
                cancellation.ThrowIfCancellationRequested();
                if (await ContextualInfoAsync(node) is { } contextual) { info = contextual; break; }
                if (await ArgumentInfoAsync(node, position) is not { } candidate) continue;
                info ??= candidate;
                if (candidate.End == position || candidate.Start <= position && position < candidate.End) { info = candidate; break; }
            }
            if (info is null) return null;
            IReadOnlyList<Signature> signatures = []; Signature? resolved = null;
            if (info.Contextual is { } signature) { signatures = [signature]; resolved = signature; }
            else if (info.IncompleteTypeArguments)
            {
                if (syntacticOnly && !await ContainsPrecedingAsync(token, info.Node.Parent ?? info.Node)) return null;
                var type = await checker.GetTypeAtLocationAsync(info.Node, cancellation);
                if (OptionalExpressions.Chain(info.Node.Parent))
                    type = OptionalExpressions.Root(info.Node.Parent) ? await checker.NonNullableAsync(type, cancellation) : checker.Optional.RemoveMarker(type);
                signatures = (await checker.SignaturesAsync(type, info.Node.Parent is NewExpressionNode, cancellation))
                    .Where(item => item.TypeParameters.Count >= info.Count).ToArray();
                if (signatures.Count != 0) resolved = signatures[0];
                else if (await checker.GetSymbolAtLocationAsync(info.Node, cancellation) is { } symbol)
                {
                    var parameters = await checker.GetSignatureHelpTypeParametersAsync(symbol, info.Node, cancellation);
                    if (parameters.Count == 0) return null;
                    var label = Utf8String.Concat([await checker.GetSymbolDisplayNameAsync(symbol, cancellation: cancellation),
                        "<"u8, Utf8String.Join(", "u8, parameters), ">"u8]);
                    return new([new(label, default, parameters.Select(label => new SignatureHelpParameter(label, default)).ToArray(),
                        options.ActiveParameterSupport ? info.Index : null, options.ActiveParameterSupport, null)], 0,
                        options.ActiveParameterSupport ? null : info.Index, !options.ActiveParameterSupport, options.Markdown);
                }
            }
            else if (!syntacticOnly || await SyntacticOwnerAsync(token, info.Node))
                (resolved, signatures) = await checker.GetResolvedSignatureForSignatureHelpAsync(info.Node, info.Count, cancellation);
            if (signatures.Count != 0) return await CreateAsync(signatures, resolved!, info, syntacticOnly);
            if (file.ScriptKind is not (ScriptKind.JS or ScriptKind.JSX) || info.Contextual is not null
                || Expression(info) is not PropertyAccessExpressionNode { Name: { } property }) return null;
            var propertyName = property switch { IdentifierNode n => n.Text, PrivateIdentifierNode n => n.Text, _ => default };
            if (propertyName.IsEmpty) return null;
            foreach (var source in program.SourceFiles)
                foreach (var node in source.Syntax.DescendantsAndSelf())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (SyntaxLanguageService.DeclarationText(node) != propertyName
                        || node.BindingSymbol is not { } symbol) continue;
                    var calls = await checker.SignaturesAsync(await checker.GetTypeOfSymbolAtLocationAsync(symbol, node, cancellation), false, cancellation);
                    if (calls.Count != 0) return await CreateAsync(calls, calls[0], info, true, source.Syntax);
                }
            return null;
        }

        private static SyntaxNode Expression(ArgumentInfo info) => info.Node switch
        {
            CallExpressionNode n => n.Expression!, NewExpressionNode n => n.Expression!, TaggedTemplateExpressionNode n => n.Tag!,
            JsxOpeningElementNode n => n.TagName!, JsxSelfClosingElementNode n => n.TagName!, _ => info.Node,
        };

        private async ValueTask<SignatureHelp> CreateAsync(IReadOnlyList<Signature> candidates, Signature resolved, ArgumentInfo info,
            bool fullPrefix, SourceFileNode? displaySource = null)
        {
            displaySource ??= file;
            var symbol = info.Contextual is not null ? info.Symbol : await checker.GetSymbolAtLocationAsync(Expression(info), cancellation)
                ?? (fullPrefix ? resolved.Declaration?.BindingSymbol : null);
            Utf8String prefix = default;
            if (symbol is not null && !symbol.Name.StartsWith(Symbol.InternalPrefix))
                prefix = fullPrefix ? await checker.GetSymbolDisplayNameAsync(symbol, displaySource, SymbolFlags.None,
                    SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope, cancellation) : await checker.GetSymbolDisplayNameAsync(symbol, cancellation: cancellation);
            List<SignatureHelpInformation> results = []; List<(int? Index, bool Present)> active = [];
            int selected = 0;
            var docs = new SymbolDocumentation(checker);
            foreach (var candidate in candidates)
            {
                var displays = await checker.GetSignatureHelpDisplaysAsync(candidate, info.TypeArguments, info.Node, displaySource, cancellation);
                if (candidate == resolved)
                {
                    selected = results.Count;
                    for (int i = 0; displays.Count > 1 && i < displays.Count; i++)
                        if (displays[i].Variadic || displays[i].Parameters.Count >= info.Count) { selected += i; break; }
                }
                var doc = await docs.FromDeclarationAsync(candidate.Declaration, options.Markdown, true, mapper, cancellation);
                foreach (var display in displays)
                {
                    var parts = new DisplayParts(options.SupportsVisualStudio);
                    if (!prefix.IsEmpty) parts.Symbol(prefix, symbol!);
                    parts.Copy(display.Runs);
                    List<SignatureHelpParameter> parameters = [];
                    foreach (var parameter in display.Parameters)
                        parameters.Add(new(parameter.Label, await docs.FromDeclarationAsync(parameter.Symbol?.ValueDeclaration,
                            options.Markdown, true, mapper, cancellation)));
                    int? index = info.Index;
                    if (display.Variadic)
                    {
                        int rest = -1;
                        for (int i = 0; i < display.Parameters.Count; i++)
                            if ((display.Parameters[i].Symbol?.CheckFlags & CheckFlags.RestParameter) != 0) { rest = i; break; }
                        index = rest >= 0 && rest < parameters.Count - 1 ? options.NoActiveParameterSupport ? null : parameters.Count
                            : Math.Min(info.Index, parameters.Count - 1);
                    }
                    active.Add((index, parameters.Count != 0));
                    results.Add(new(parts.ToUtf8String(), doc, parameters, options.ActiveParameterSupport ? index : null,
                        options.ActiveParameterSupport && parameters.Count != 0, options.SupportsVisualStudio ? parts.Runs : null));
                }
            }
            return new(results, selected, options.ActiveParameterSupport ? null : active[selected].Index,
                !options.ActiveParameterSupport && active[selected].Present, options.Markdown);
        }
    }
}
