using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal static class MemberModifiers
{
    private static readonly (ModifierFlags Flag, K Kind)[] Order =
    [
        (ModifierFlags.Export, K.ExportKeyword), (ModifierFlags.Ambient, K.DeclareKeyword), (ModifierFlags.Default, K.DefaultKeyword),
        (ModifierFlags.Const, K.ConstKeyword), (ModifierFlags.Public, K.PublicKeyword), (ModifierFlags.Private, K.PrivateKeyword),
        (ModifierFlags.Protected, K.ProtectedKeyword), (ModifierFlags.Abstract, K.AbstractKeyword), (ModifierFlags.Static, K.StaticKeyword),
        (ModifierFlags.Override, K.OverrideKeyword), (ModifierFlags.Readonly, K.ReadonlyKeyword), (ModifierFlags.Accessor, K.AccessorKeyword),
        (ModifierFlags.Async, K.AsyncKeyword), (ModifierFlags.In, K.InKeyword), (ModifierFlags.Out, K.OutKeyword),
    ];

    internal static ModifierFlags Flag(K kind) => kind == K.Decorator ? ModifierFlags.Decorator : Order.FirstOrDefault(item => item.Kind == kind).Flag;
    internal static ModifierFlags Flags(SyntaxNode node) => (node.ModifierList ?? new([])).Aggregate(ModifierFlags.None, (flags, modifier) => flags | Flag(modifier.Kind));

    internal static NodeList? Create(NodeFactory factory, ModifierFlags flags, IReadOnlyList<SyntaxNode>? decorators = null)
    {
        List<SyntaxNode> nodes = decorators?.Select(node => node.DeepClone<SyntaxNode>(factory)).ToList() ?? [];
        nodes.AddRange(Order.Where(item => (flags & item.Flag) != 0).Select(item => factory.NewToken(item.Kind)));
        return nodes.Count == 0 ? null : new([.. nodes]);
    }

    internal static ModifierFlags FromSymbol(Symbol symbol)
    {
        if ((symbol.CheckFlags & CheckFlags.Synthetic) != 0)
        {
            var flags = (symbol.CheckFlags & CheckFlags.ContainsPublic) != 0 ? ModifierFlags.Public
                : (symbol.CheckFlags & CheckFlags.ContainsProtected) != 0 ? ModifierFlags.Protected
                : (symbol.CheckFlags & CheckFlags.ContainsPrivate) != 0 ? ModifierFlags.Private : ModifierFlags.None;
            return flags | ((symbol.CheckFlags & CheckFlags.ContainsStatic) != 0 ? ModifierFlags.Static : ModifierFlags.None);
        }
        if (symbol.ValueDeclaration is { } declaration)
        {
            if ((symbol.Flags & SymbolFlags.GetAccessor) != 0) declaration = symbol.Declarations.OfType<GetAccessorDeclarationNode>().FirstOrDefault() ?? declaration;
            var root = SemanticSyntax.RootDeclaration(declaration);
            if (root is VariableDeclarationNode) root = root.Parent!;
            if (root is VariableDeclarationListNode) root = root.Parent!;
            var flags = Flags(root);
            return symbol.Parent is { } parent && (parent.Flags & SymbolFlags.Class) != 0 ? flags : flags & ~ModifierFlags.AccessibilityModifier;
        }
        return (symbol.Flags & SymbolFlags.Prototype) != 0 ? ModifierFlags.Public | ModifierFlags.Static : ModifierFlags.None;
    }
}
