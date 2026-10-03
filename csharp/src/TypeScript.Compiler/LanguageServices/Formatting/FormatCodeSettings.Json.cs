using System.Text.Json;
using TypeScript.Compiler.Configuration;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial record FormatCodeSettings
{
    internal static FormatCodeSettings Read(JsonElement value, FormatCodeSettings? current = null, bool configuration = false)
    {
        current ??= new();
        if (value.ValueKind != JsonValueKind.Object) return current;
        foreach (var property in value.EnumerateObject())
        {
            if (configuration && (property.Value.ValueKind == JsonValueKind.Null || property.Name.Length == 0 || char.IsUpper(property.Name[0]))) continue;
            if (property.Name.ToLowerInvariant() is "baseindentsize" or "indentsize" or "tabsize" && property.Value.ValueKind != JsonValueKind.Number) continue;
            if (property.Name.Equals("newLineCharacter", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind != JsonValueKind.String) continue;
            bool? Boolean() => property.Value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
            int Integer() => property.Value.TryGetInt32(out int number) ? number : unchecked((int)property.Value.GetDouble());
            string? String() => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.ToLowerInvariant() : null;
            current = property.Name.ToLowerInvariant() switch
            {
                "baseindentsize" => current with { BaseIndentSize = Integer() },
                "indentsize" => current with { IndentSize = Integer() },
                "tabsize" => current with { TabSize = Integer() },
                "newlinecharacter" => current with { NewLineCharacter = JsonStrings.GetString(property.Value) },
                "indentstyle" => current with { IndentStyle = property.Value.ValueKind == JsonValueKind.Number ? (IndentStyle)Integer()
                    : String() switch { "none" => IndentStyle.None, "block" => IndentStyle.Block, _ => IndentStyle.Smart } },
                "semicolons" => current with { Semicolons = String() switch
                    { "insert" => SemicolonPreference.Insert, "remove" => SemicolonPreference.Remove, _ => SemicolonPreference.Ignore } },
                "converttabstospaces" => current with { ConvertTabsToSpaces = Boolean() },
                "trimtrailingwhitespace" => current with { TrimTrailingWhitespace = Boolean() },
                "insertspaceaftercommadelimiter" => current with { InsertSpaceAfterCommaDelimiter = Boolean() },
                "insertspaceaftersemicoloninforstatements" => current with { InsertSpaceAfterSemicolonInForStatements = Boolean() },
                "insertspacebeforeandafterbinaryoperators" => current with { InsertSpaceBeforeAndAfterBinaryOperators = Boolean() },
                "insertspaceafterconstructor" => current with { InsertSpaceAfterConstructor = Boolean() },
                "insertspaceafterkeywordsincontrolflowstatements" => current with { InsertSpaceAfterKeywordsInControlFlowStatements = Boolean() },
                "insertspaceafterfunctionkeywordforanonymousfunctions" => current with { InsertSpaceAfterFunctionKeywordForAnonymousFunctions = Boolean() },
                "insertspaceafteropeningandbeforeclosingnonemptyparenthesis" => current with { InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesis = Boolean() },
                "insertspaceafteropeningandbeforeclosingnonemptybrackets" => current with { InsertSpaceAfterOpeningAndBeforeClosingNonemptyBrackets = Boolean() },
                "insertspaceafteropeningandbeforeclosingnonemptybraces" => current with { InsertSpaceAfterOpeningAndBeforeClosingNonemptyBraces = Boolean() },
                "insertspaceafteropeningandbeforeclosingemptybraces" => current with { InsertSpaceAfterOpeningAndBeforeClosingEmptyBraces = Boolean() },
                "insertspaceafteropeningandbeforeclosingtemplatestringbraces" => current with { InsertSpaceAfterOpeningAndBeforeClosingTemplateStringBraces = Boolean() },
                "insertspaceafteropeningandbeforeclosingjsxexpressionbraces" => current with { InsertSpaceAfterOpeningAndBeforeClosingJsxExpressionBraces = Boolean() },
                "insertspaceaftertypeassertion" => current with { InsertSpaceAfterTypeAssertion = Boolean() },
                "insertspacebeforefunctionparenthesis" => current with { InsertSpaceBeforeFunctionParenthesis = Boolean() },
                "placeopenbraceonnewlineforfunctions" => current with { PlaceOpenBraceOnNewLineForFunctions = Boolean() },
                "placeopenbraceonnewlineforcontrolblocks" => current with { PlaceOpenBraceOnNewLineForControlBlocks = Boolean() },
                "insertspacebeforetypeannotation" => current with { InsertSpaceBeforeTypeAnnotation = Boolean() },
                "indentmultilineobjectliteralbeginningonblankline" => current with { IndentMultiLineObjectLiteralBeginningOnBlankLine = Boolean() },
                "indentswitchcase" => current with { IndentSwitchCase = Boolean() },
                _ => current,
            };
        }
        return current;
    }
}
