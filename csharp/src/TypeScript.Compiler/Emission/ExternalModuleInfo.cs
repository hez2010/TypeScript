using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed class ExternalModuleInfo
{
    internal readonly Dictionary<Utf8String, List<ExportSpecifierNode>> Specifiers = [];
    internal readonly Dictionary<SyntaxNode, List<SyntaxNode>> Bindings = new(ReferenceEqualityComparer.Instance);
    internal readonly List<SyntaxNode> Names = [];
    internal readonly List<SyntaxNode> Functions = [];
    internal ExportAssignmentNode? ExportEquals;

    internal static async ValueTask<ExternalModuleInfo> CollectAsync(SourceFileNode file, EmitContext context, Checker checker, CancellationToken cancellation)
    {
        var result = new ExternalModuleInfo();
        HashSet<Utf8String> unique = [];
        HashSet<SyntaxNode> functions = new(ReferenceEqualityComparer.Instance);
        bool hasDefault = false;
        foreach (var node in file.Statements ?? new([]))
        {
            cancellation.ThrowIfCancellationRequested();
            switch (node)
            {
                case NotEmittedStatementNode when context.MostOriginal(node) is ExportAssignmentNode { IsExportEquals: true } assignment:
                    result.ExportEquals ??= assignment;
                    break;
                case ExportAssignmentNode { IsExportEquals: true } assignment:
                    result.ExportEquals ??= assignment;
                    break;
                case ExportDeclarationNode { ExportClause: NamedExportsNode named } export:
                    foreach (var specifier in named.Elements?.OfType<ExportSpecifierNode>() ?? [])
                    {
                        var exported = specifier.Name!;
                        if (!unique.Add(ExportName(exported))) continue;
                        var name = specifier.PropertyName ?? exported;
                        if (name is IdentifierNode identifier)
                        {
                            if (export.ModuleSpecifier is null)
                            {
                                if (!result.Specifiers.TryGetValue(identifier.Text, out var items)) result.Specifiers.Add(identifier.Text, items = []);
                                items.Add(specifier);
                            }
                            var original = (IdentifierNode)context.MostOriginal(identifier);
                            var declaration = await checker.GetReferencedImportForEmitAsync(original, cancellation)
                                ?? await checker.GetReferencedValueForEmitAsync(original, cancellation);
                            if (declaration is FunctionDeclarationNode)
                            {
                                unique.Remove(ExportName(exported));
                                AddFunction(declaration, exported, ExportName(exported) == "default"u8);
                                continue;
                            }
                            if (declaration is not null) AddBinding(declaration, exported);
                        }
                        result.Names.Add(exported);
                    }
                    break;
                case ExportDeclarationNode { ModuleSpecifier: not null, ExportClause: NamespaceExportNode space }:
                    if (unique.Add(ExportName(space.Name!))) { AddBinding(node, space.Name!); result.Names.Add(space.Name!); }
                    break;
                case VariableStatementNode variable when Exported(variable):
                    Stack<SyntaxNode> declarations = new((variable.DeclarationList!.Declarations ?? new([])).Reverse());
                    while (declarations.TryPop(out var declaration))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var name = declaration.DeclarationName;
                        if (name is BindingPatternNode pattern)
                        {
                            for (int i = (pattern.Elements?.Count ?? 0) - 1; i >= 0; i--) declarations.Push(pattern.Elements![i]);
                        }
                        else if (name is not null && context.GetAutoGenerateInfo(name) is null && unique.Add(ExportName(name)))
                        {
                            result.Names.Add(name);
                            if ((context.GetFlags(name) & EmitFlags.LocalName) != 0) AddBinding(declaration, name);
                        }
                    }
                    break;
                case FunctionDeclarationNode when Exported(node):
                    AddFunction(node, null, SemanticSyntax.HasModifier(node, K.DefaultKeyword));
                    break;
                case ClassDeclarationNode declaration when Exported(node):
                    if (SemanticSyntax.HasModifier(node, K.DefaultKeyword))
                    {
                        if (!hasDefault) { AddBinding(node, declaration.Name ?? context.NewGeneratedNameForNode(node)); hasDefault = true; }
                    }
                    else if (declaration.Name is { } name && unique.Add(name.Text)) { AddBinding(node, name); result.Names.Add(name); }
                    break;
            }
        }
        return result;

        void AddBinding(SyntaxNode declaration, SyntaxNode name)
        {
            declaration = context.MostOriginal(declaration);
            if (!result.Bindings.TryGetValue(declaration, out var bindings)) result.Bindings.Add(declaration, bindings = []);
            bindings.Add(name);
        }
        void AddFunction(SyntaxNode declaration, SyntaxNode? name, bool isDefault)
        {
            var original = context.MostOriginal(declaration);
            if (functions.Add(original)) result.Functions.Add(original);
            if (isDefault)
            {
                if (!hasDefault) { AddBinding(declaration, name ?? context.NewGeneratedNameForNode(declaration)); hasDefault = true; }
            }
            else
            {
                name ??= declaration.DeclarationName!;
                if (unique.Add(ExportName(name))) AddBinding(declaration, name);
            }
        }
    }
    private static Utf8String ExportName(SyntaxNode node) => node is StringLiteralNode literal ? literal.Text : SyntaxNameText.Get(node);
    private static bool Exported(SyntaxNode node) => SemanticSyntax.HasModifier(node, K.ExportKeyword);
}
