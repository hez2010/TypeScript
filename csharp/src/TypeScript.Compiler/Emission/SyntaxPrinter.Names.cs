using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

public sealed partial class SyntaxPrinter
{
    private NameGenerator nameGenerator = null!;
    private readonly Dictionary<Utf8String, IdentifierNode> helperNames = [];
    private bool importedHelpers;
    private IdentifierNode? externalHelpers;

    private void InitializeNames()
    {
        helperNames.Clear();
        importedHelpers = sourceFile is not null && (context.GetFlags(context.MostOriginal(sourceFile)) & EmitFlags.ExternalHelpers) != 0;
        externalHelpers = sourceFile is not null ? context.GetExternalHelpersModuleName(sourceFile) : null;
        // Collecting every identifier in the file is only needed to keep a generated name from
        // colliding with a declared one, so the walk is deferred until a name is actually generated.
        // Files whose transform minted no names (the common case) never pay for it.
        HashSet<Utf8String>? identifiers = null;
        nameGenerator = new(context, (name, _) =>
            sourceFile is null || !Identifiers().Contains(name) && options.HasGlobalName?.Invoke(name) != true, NameText);
        HashSet<Utf8String> Identifiers()
        {
            if (identifiers is not null)
                return identifiers;
            identifiers = [];
            foreach (var node in context.MostOriginal(sourceFile!).DescendantsAndSelf())
            {
                cancellation.ThrowIfCancellationRequested();
                if (node is IdentifierNode identifier)
                    identifiers.Add(identifier.Text);
                else if (node is PrivateIdentifierNode privateIdentifier)
                    identifiers.Add(privateIdentifier.Text);
            }
            return identifiers;
        }
    }

    private Utf8String HelperText(IdentifierNode node)
    {
        if ((context.GetFlags(node) & EmitFlags.HelperName) == 0) return nameGenerator.GenerateName(node);
        if (externalHelpers is not null) return Utf8String.Concat(nameGenerator.GenerateName(externalHelpers), "."u8, node.Text);
        if (!importedHelpers) return nameGenerator.GenerateName(node);
        if (!helperNames.TryGetValue(node.Text, out var name))
        {
            name = context.NewUniqueName(node.Text, new(GeneratedIdentifierFlags.FileLevel | GeneratedIdentifierFlags.Optimistic));
            helperNames.Add(node.Text, name);
        }
        return nameGenerator.GenerateName(name);
    }

    private Utf8String NameText(SyntaxNode node)
    {
        if (context.GetAutoGenerateInfo(node) is not null)
            return nameGenerator.GenerateName(node);
        return node switch
        {
            IdentifierNode name => OriginalText(name) ?? name.Text,
            PrivateIdentifierNode name => OriginalText(name) ?? name.Text,
            StringLiteralNode literal => literal.Text,
            NumericLiteralNode literal => literal.Text,
            JsxNamespacedNameNode name => Utf8String.Concat(NameText(name.Namespace!), ":"u8, NameText(name.Name!)),
            _ => default
        };
    }

    private void GenerateName(SyntaxNode? name)
    {
        if (name is IdentifierNode or PrivateIdentifierNode)
            nameGenerator.GenerateName(name);
        else if (name is BindingPatternNode)
            GenerateNames(name);
    }

    private void GenerateNames(SyntaxNode node)
    {
        var pendingNames = new Stack<SyntaxNode>();
        pendingNames.Push(node);
        while (pendingNames.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            switch (current)
            {
                case BlockNode n: AddList(n.Statements); break;
                case CaseOrDefaultClauseNode n: AddList(n.Statements); break;
                case LabeledStatementNode n: Add(n.Statement); break;
                case WithStatementNode n: Add(n.Statement); break;
                case DoStatementNode n: Add(n.Statement); break;
                case WhileStatementNode n: Add(n.Statement); break;
                case IfStatementNode n: Add(n.ElseStatement); Add(n.ThenStatement); break;
                case ForStatementNode n: Add(n.Statement); Add(n.Initializer); break;
                case ForInOrOfStatementNode n: Add(n.Statement); Add(n.Initializer); break;
                case SwitchStatementNode n: Add(n.CaseBlock); break;
                case CaseBlockNode n: AddList(n.Clauses); break;
                case TryStatementNode n: Add(n.FinallyBlock); Add(n.CatchClause); Add(n.TryBlock); break;
                case CatchClauseNode n: Add(n.Block); Add(n.VariableDeclaration); break;
                case VariableStatementNode n: Add(n.DeclarationList); break;
                case VariableDeclarationListNode n: AddList(n.Declarations); break;
                case FunctionDeclarationNode n:
                    GenerateName(n.Name);
                    if ((context.GetFlags(n) & EmitFlags.ReuseTempVariableScope) != 0)
                    {
                        Add(n.Body);
                        AddList(n.Parameters);
                    }
                    break;
                case BindingPatternNode n: AddList(n.Elements); break;
                case ImportDeclarationNode n: Add(n.ImportClause); break;
                case ImportClauseNode n: GenerateName(n.Name); Add(n.NamedBindings); break;
                case NamedImportsNode n: AddList(n.Elements); break;
                case ImportSpecifierNode n: GenerateName(n.PropertyName ?? n.Name); break;
                default: GenerateName(current.DeclarationName); break;
            }
        }

        void Add(SyntaxNode? child)
        {
            if (child is not null)
                pendingNames.Push(child);
        }
        void AddList(NodeList? children)
        {
            if (children is not null)
                for (int i = children.Count - 1; i >= 0; i--)
                    pendingNames.Push(children[i]);
        }
    }

    private Utf8String StringText(StringLiteralNode literal)
    {
        while (context.GetTextSource(literal) is StringLiteralNode sourceLiteral) literal = sourceLiteral;
        if (context.GetTextSource(literal) is NoSubstitutionTemplateLiteralNode template)
            return OriginalText(template) ?? Checker.QuoteSymbolText(template.Text, '`', !options.NeverAsciiEscape && (context.GetFlags(literal) & EmitFlags.NoAsciiEscaping) == 0);
        if (context.GetTextSource(literal) is { } source)
            return Checker.QuoteSymbolText(NameText(source), '"', !options.NeverAsciiEscape && (context.GetFlags(literal) & EmitFlags.NoAsciiEscaping) == 0);
        return OriginalText(literal) ?? Checker.QuoteSymbolText(literal.Text,
            (literal.TokenFlags & Syntax.TokenFlags.SingleQuote) != 0 ? '\'' : '"',
            !options.NeverAsciiEscape && (context.GetFlags(literal) & EmitFlags.NoAsciiEscaping) == 0);
    }
}
