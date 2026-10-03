using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public static partial class SyntaxLanguageService
{
    private static ModuleDeclarationNode InteriorModule(ModuleDeclarationNode node)
    {
        while (node.Body is ModuleDeclarationNode inner) node = inner;
        return node;
    }

    private static bool ParameterProperty(SyntaxNode node) => node is ParameterDeclarationNode { Parent: ConstructorDeclarationNode }
        && node.ModifierList?.Any(modifier => modifier.Kind is K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword or K.OverrideKeyword) == true;

    internal static SyntaxNode? Name(SyntaxNode node)
    {
        if (NonAssignedName(node) is { } name) return name;
        if (node is not (FunctionExpressionNode or ArrowFunctionNode or ClassExpressionNode)) return null;
        return node.Parent switch
        {
            PropertyAssignmentNode property => property.Name,
            BindingElementNode binding => binding.Name,
            BinaryExpressionNode binary when binary.Right == node => binary.Left is IdentifierNode ? binary.Left : AccessName(binary.Left),
            VariableDeclarationNode { Name: IdentifierNode variable } => variable,
            _ => null,
        };
    }

    private static Utf8String LiteralText(SyntaxNode? node) => node switch
    {
        IdentifierNode n => n.Text, PrivateIdentifierNode n => n.Text, StringLiteralNode n => n.Text,
        NoSubstitutionTemplateLiteralNode n => n.Text, NumericLiteralNode n => n.Text, _ => default,
    };

    private static SyntaxNode? NonAssignedName(SyntaxNode node) => node switch
    {
        BinaryExpressionNode binary when AssignmentKind(node) is AssignmentDeclaration.Property or AssignmentDeclaration.ThisProperty or AssignmentDeclaration.ExportsProperty
            => AccessName(binary.Left) ?? binary.Left,
        CallExpressionNode call when AssignmentKind(node) is AssignmentDeclaration.DefineProperty or AssignmentDeclaration.DefineExports => call.Arguments![1],
        ExportAssignmentNode { Expression: IdentifierNode name } => name,
        _ => node.DeclarationName,
    };

    internal static Utf8String DeclarationText(SyntaxNode node) => NonAssignedName(node) switch
    {
        ComputedPropertyNameNode { Expression: PropertyAccessExpressionNode access } => LiteralText(access.Name),
        ComputedPropertyNameNode computed when LiteralLike(computed.Expression) => LiteralText(computed.Expression),
        { } name => LiteralText(name), _ => default,
    };

    private static Utf8String NameText(SyntaxNode node, SourceFileNode file) => node switch
    {
        IdentifierNode or PrivateIdentifierNode or NumericLiteralNode => LiteralText(node),
        StringLiteralNode literal => Checker.QuoteSymbolText(literal.Text, (byte)'"', ascii: false),
        NoSubstitutionTemplateLiteralNode literal => Checker.QuoteSymbolText(literal.Text, (byte)'`', ascii: false),
        ComputedPropertyNameNode computed when LiteralLike(computed.Expression) => NameText(computed.Expression!, file),
        _ => file.Source.Text[SmartIndenter.Start(node, file)..node.End],
    };

    private static Utf8String UnnamedLabel(SyntaxNode node, SourceFileNode file)
    {
        var parent = node.Parent;
        while (parent is ParenthesizedExpressionNode) parent = parent.Parent;
        if (parent is ExportAssignmentNode export) return export.IsExportEquals ? "export="u8 : "default"u8;
        switch (node.Kind)
        {
            case K.FunctionDeclaration: case K.FunctionExpression: case K.ArrowFunction:
                if (SemanticSyntax.HasModifier(node, K.DefaultKeyword)) return "default"u8;
                if (node.Parent is CallExpressionNode call && CallName(call.Expression) is { IsEmpty: false } callName)
                {
                    callName = CleanCallbackText(callName);
                    if (callName.Length > 150) return callName + " callback"u8;
                    var args = call.Arguments?.Where(arg => arg is StringLiteralNode or NoSubstitutionTemplateLiteralNode or TemplateExpressionNode)
                        .Select(arg => file.Source.Text[SmartIndenter.Start(arg, file)..arg.End]) ?? [];
                    return callName + "("u8 + CleanCallbackText(Utf8String.Join(", "u8, args)) + ") callback"u8;
                }
                return "<function>"u8;
            case K.ClassDeclaration: case K.ClassExpression:
                return SemanticSyntax.HasModifier(node, K.DefaultKeyword) ? "default"u8 : "<class>"u8;
            case K.Constructor: return "constructor"u8;
            case K.CallSignature: return "()"u8;
            case K.ConstructSignature: return "new()"u8;
            case K.IndexSignature: return "[]"u8;
            default: return default;
        }
    }

    private static Utf8String CallName(SyntaxNode? node)
    {
        Stack<Utf8String> pieces = [];
        while (node is PropertyAccessExpressionNode access)
        {
            pieces.Push(access.Name is IdentifierNode or PrivateIdentifierNode ? LiteralText(access.Name) : default);
            node = access.Expression;
        }
        if (node is IdentifierNode or PrivateIdentifierNode) pieces.Push(LiteralText(node));
        // The reference omits a leading empty base, but retains empty later names.
        var result = Utf8String.Empty;
        foreach (var part in pieces) result = result.IsEmpty ? part : result + "."u8 + part;
        return result;
    }

    private static Utf8String TruncateSymbolText(Utf8String text)
    {
        int end = 0;
        for (int count = 0; count < 150 && end < text.Length; count++)
        { Wtf8.Decode(text.Span[end..], out int width); end += width; }
        return end < text.Length ? text[..end] + "..."u8 : text;
    }

    private static Utf8String CleanCallbackText(Utf8String text)
    {
        text = TruncateSymbolText(text);
        var result = new Utf8StringBuilder();
        for (int pos = 0; pos < text.Length;)
        {
            int point = Wtf8.Decode(text.Span[pos..], out int width);
            if (!TokenFacts.IsLineBreak(point)) result.Append(text.Span.Slice(pos, width));
            pos += width;
        }
        return result.ToUtf8String();
    }

    private static bool Anonymous(Utf8String name) => name == "<function>"u8 || name == "<class>"u8 || name == "export="u8 || name == "default"u8
        || name == "constructor"u8 || name == "()"u8 || name == "new()"u8 || name == "[]"u8 || name.EndsWith(") callback"u8);

    private static bool LiteralLike(SyntaxNode? node) => node is StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode;
    private static SyntaxNode? AccessBase(SyntaxNode? node) => node switch
    { PropertyAccessExpressionNode n => n.Expression, ElementAccessExpressionNode n => n.Expression, _ => null };
    internal static SyntaxNode? AccessName(SyntaxNode? node)
    {
        if (node is PropertyAccessExpressionNode { Name: IdentifierNode name }) return name;
        if (node is ElementAccessExpressionNode element)
        {
            var argument = element.ArgumentExpression;
            while (argument is ParenthesizedExpressionNode parentheses) argument = parentheses.Expression;
            if (LiteralLike(argument)) return argument;
        }
        return null;
    }
    private static bool EntityName(SyntaxNode? node, bool javascript, bool allowThis = true)
    {
        while (true)
        {
            if (node is IdentifierNode) return true;
            if (javascript && allowThis && node?.Kind == K.ThisKeyword) return true;
            if (node is PropertyAccessExpressionNode { Name: IdentifierNode } access) node = access.Expression;
            else if (javascript && node is ElementAccessExpressionNode element && LiteralLike(element.ArgumentExpression)) node = element.Expression;
            else return false;
        }
    }
    private static bool Exports(SyntaxNode? node) => node is IdentifierNode { Text: var text } && text == "exports"u8;
    private static bool ModuleExports(SyntaxNode? node) => AccessBase(node) is IdentifierNode { Text: var module } && module == "module"u8
        && AccessName(node) is { } name && LiteralText(name) == "exports"u8;

    internal enum AssignmentDeclaration { None, ModuleExports, ExportsProperty, ThisProperty, Property, DefineProperty, DefineExports }
    internal static bool IsAssignmentDeclaration(SyntaxNode node) => AssignmentKind(node) != AssignmentDeclaration.None;

    internal static AssignmentDeclaration AssignmentKind(SyntaxNode node)
    {
        bool js = (node.Flags & NodeFlags.JavaScriptFile) != 0;
        if (node is BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken, Left: PropertyAccessExpressionNode or ElementAccessExpressionNode } binary)
        {
            var expression = AccessBase(binary.Left);
            if (js)
            {
                if (ModuleExports(binary.Left) && !Exports(binary.Right)) return AssignmentDeclaration.ModuleExports;
                if ((ModuleExports(expression) || Exports(expression)) && AccessName(binary.Left) is not null) return AssignmentDeclaration.ExportsProperty;
                if (expression?.Kind == K.ThisKeyword) return AssignmentDeclaration.ThisProperty;
            }
            if (EntityName(expression, js) && (binary.Left is ElementAccessExpressionNode || binary.Left is PropertyAccessExpressionNode { Name: IdentifierNode }))
                return AssignmentDeclaration.Property;
        }
        if (js && node is CallExpressionNode { Expression: PropertyAccessExpressionNode { Expression: IdentifierNode { Text: var owner }, Name: IdentifierNode { Text: var method } }, Arguments: { Count: 3 } args }
            && owner == "Object"u8 && method == "defineProperty"u8 && LiteralLike(args[1]) && EntityName(args[0], true, false))
            return Exports(args[0]) || ModuleExports(args[0]) ? AssignmentDeclaration.DefineExports : AssignmentDeclaration.DefineProperty;
        return AssignmentDeclaration.None;
    }

    private static (SyntaxNode Target, SyntaxNode? Function, SyntaxNode? Definition, SyntaxNode? Name)? Expando(SyntaxNode node)
    {
        if (AssignmentKind(node) == AssignmentDeclaration.Property && node is BinaryExpressionNode binary)
            return (binary.Left!, AccessBase(binary.Left), binary.Right, binary.Left is PropertyAccessExpressionNode property ? property.Name : ((ElementAccessExpressionNode)binary.Left!).ArgumentExpression);
        if (AssignmentKind(node) == AssignmentDeclaration.DefineProperty && node is CallExpressionNode { Arguments: { } args })
            return (args[1], args[0], args[2], args[1]);
        return null;
    }

    internal static DocumentSymbolKind SymbolKind(SyntaxNode node) => node.Kind switch
    {
        K.SourceFile => ((SourceFileNode)node).ExternalModuleIndicator is null ? DocumentSymbolKind.File : DocumentSymbolKind.Module,
        K.ModuleDeclaration => DocumentSymbolKind.Namespace,
        K.ClassDeclaration or K.ClassExpression or K.TypeAliasDeclaration or K.JSDocTypedefTag or K.JSDocCallbackTag => DocumentSymbolKind.Class,
        K.InterfaceDeclaration => DocumentSymbolKind.Interface,
        K.EnumDeclaration => DocumentSymbolKind.Enum,
        K.ArrowFunction or K.FunctionDeclaration or K.FunctionExpression => DocumentSymbolKind.Function,
        K.GetAccessor or K.SetAccessor or K.PropertyDeclaration or K.PropertySignature or K.PropertyAssignment or K.ShorthandPropertyAssignment
            or K.SpreadAssignment or K.IndexSignature or K.StringLiteral or K.NoSubstitutionTemplateLiteral or K.NumericLiteral => DocumentSymbolKind.Property,
        K.MethodDeclaration or K.MethodSignature or K.CallSignature => DocumentSymbolKind.Method,
        K.ConstructSignature or K.Constructor or K.ClassStaticBlockDeclaration => DocumentSymbolKind.Constructor,
        K.TypeParameter => DocumentSymbolKind.TypeParameter,
        K.EnumMember => DocumentSymbolKind.EnumMember,
        K.Parameter when node.ModifierList?.Any(modifier => modifier.Kind is K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword or K.OverrideKeyword) == true => DocumentSymbolKind.Property,
        K.BinaryExpression or K.CallExpression when AssignmentKind(node) is AssignmentDeclaration.ThisProperty or AssignmentDeclaration.Property or AssignmentDeclaration.DefineProperty => DocumentSymbolKind.Property,
        _ => DocumentSymbolKind.Variable,
    };
}
