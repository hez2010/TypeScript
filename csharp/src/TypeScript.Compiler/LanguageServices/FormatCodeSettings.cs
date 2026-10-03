namespace TypeScript.Compiler.LanguageServices;

public enum IndentStyle { None, Block, Smart }
public enum SemicolonPreference { Ignore, Insert, Remove }

/// <summary>Formatting preferences. Null preserves the unspecified state of a boolean preference.</summary>
public sealed partial record FormatCodeSettings
{
    public int BaseIndentSize { get; init; }
    public int IndentSize { get; init; } = 4;
    public int TabSize { get; init; } = 4;
    public Utf8String NewLineCharacter { get; init; } = "\n"u8;
    public bool? ConvertTabsToSpaces { get; init; } = true;
    public IndentStyle IndentStyle { get; init; } = IndentStyle.Smart;
    public bool? TrimTrailingWhitespace { get; init; } = true;
    public bool? InsertSpaceAfterCommaDelimiter { get; init; } = true;
    public bool? InsertSpaceAfterSemicolonInForStatements { get; init; } = true;
    public bool? InsertSpaceBeforeAndAfterBinaryOperators { get; init; } = true;
    public bool? InsertSpaceAfterConstructor { get; init; } = false;
    public bool? InsertSpaceAfterKeywordsInControlFlowStatements { get; init; } = true;
    public bool? InsertSpaceAfterFunctionKeywordForAnonymousFunctions { get; init; } = false;
    public bool? InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesis { get; init; } = false;
    public bool? InsertSpaceAfterOpeningAndBeforeClosingNonemptyBrackets { get; init; } = false;
    public bool? InsertSpaceAfterOpeningAndBeforeClosingNonemptyBraces { get; init; } = true;
    public bool? InsertSpaceAfterOpeningAndBeforeClosingEmptyBraces { get; init; }
    public bool? InsertSpaceAfterOpeningAndBeforeClosingTemplateStringBraces { get; init; } = false;
    public bool? InsertSpaceAfterOpeningAndBeforeClosingJsxExpressionBraces { get; init; } = false;
    public bool? InsertSpaceAfterTypeAssertion { get; init; }
    public bool? InsertSpaceBeforeFunctionParenthesis { get; init; } = false;
    public bool? PlaceOpenBraceOnNewLineForFunctions { get; init; } = false;
    public bool? PlaceOpenBraceOnNewLineForControlBlocks { get; init; } = false;
    public bool? InsertSpaceBeforeTypeAnnotation { get; init; }
    public bool? IndentMultiLineObjectLiteralBeginningOnBlankLine { get; init; }
    public SemicolonPreference Semicolons { get; init; }
    public bool? IndentSwitchCase { get; init; } = true;
}
