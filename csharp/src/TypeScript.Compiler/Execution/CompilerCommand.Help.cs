using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Incremental;

namespace TypeScript.Compiler.Execution;

public sealed partial class CompilerCommand
{
    private string Localize(DiagnosticMessage message, params Utf8String[] arguments) => message.Format(options.Locale, arguments).ToString();
    private void PrintVersion() => output.Write(Localize(Messages.Version_0, BuildInfo.CompilerVersion) + "\n");
    private bool HelpColors => Pretty(new());
    private string Bold(string value) => HelpColors ? "\u001b[1m" + value + "\u001b[22m" : value;
    private string White(string value) => HelpColors ? "\u001b[97m" + value + "\u001b[39m" : value;
    private string Blue(string value) => !HelpColors ? value
        : environment("OS")?.Contains("windows", StringComparison.OrdinalIgnoreCase) == true && string.IsNullOrEmpty(environment("WT_SESSION")) && environment("TERM_PROGRAM") != "vscode"
            ? White(value) : "\u001b[94m" + value + "\u001b[39m";
    private string BlueBackground(string value) => !HelpColors ? value
        : environment("COLORTERM") == "truecolor" || environment("TERM") == "xterm-256color"
            ? "\u001b[48;5;68m" + value + "\u001b[39;49m" : "\u001b[44m" + value + "\u001b[39;49m";
    private int terminalWidth = 80;
    public int TerminalWidth { get => terminalWidth; init => terminalWidth = Math.Max(0, value); }

    private void Header(string message)
    {
        if (terminalWidth >= System.Text.Encoding.UTF8.GetByteCount(message) + 5)
        {
            int left = Math.Min(120, terminalWidth) - 5;
            output.Write(message.PadRight(left) + BlueBackground("     ") + "\n");
            output.Write(new string(' ', left) + BlueBackground(White("  TS ")) + "\n");
        }
        else output.Write(message + "\n\n");
    }

    private void PrintHelp(bool build)
    {
        Header(Localize(Messages.X_tsc_Colon_The_TypeScript_Compiler) + " - " + Localize(Messages.Version_0, BuildInfo.CompilerVersion));
        var buildOnly = OptionDefinitions.All.Where(option => option.Group == OptionGroup.Build && option.Name != "build"u8);
        string buildDescription = Localize(Messages.Using_build_b_will_make_tsc_behave_more_like_a_build_orchestrator_than_a_compiler_This_is_used_to_trigger_building_composite_projects_which_you_can_learn_more_about_at_0, "https://aka.ms/tsc-composite-builds"u8);
        if (build) { HelpSection(Messages.BUILD_OPTIONS, buildOnly, before: buildDescription); return; }
        var compiler = OptionDefinitions.All.Where(option => option.Group == OptionGroup.Compiler || option.Name == "build"u8).ToArray();
        string after = Localize(Messages.You_can_learn_about_all_of_the_compiler_options_at_0, "https://aka.ms/tsc"u8);
        if (options.All == true)
        {
            HelpSection(Messages.ALL_COMPILER_OPTIONS, compiler.OrderBy(option => option.Name.ToLowerInvariant(), Utf8StringComparer.Ordinal), true, after: after);
            HelpSection(Messages.WATCH_OPTIONS, OptionDefinitions.All.Where(option => option.Group == OptionGroup.Watch),
                before: Localize(Messages.Including_watch_w_will_start_watching_the_current_project_for_the_file_changes_Once_set_you_can_config_watch_mode_with_Colon));
            HelpSection(Messages.BUILD_OPTIONS, buildOnly.Where(option => OptionDefinitions.Find(option.Name) is null), before: buildDescription);
            return;
        }
        output.Write(Bold(Localize(Messages.COMMON_COMMANDS)) + "\n\n");
        Example(["tsc"], Messages.Compiles_the_current_project_tsconfig_json_in_the_working_directory);
        Example(["tsc app.ts util.ts"], Messages.Ignoring_tsconfig_json_compiles_the_specified_files_with_default_compiler_options);
        Example(["tsc -b"], Messages.Build_a_composite_project_in_the_working_directory);
        Example(["tsc --init"], Messages.Creates_a_tsconfig_json_with_the_recommended_settings_in_the_working_directory);
        Example(["tsc -p ./path/to/tsconfig.json"], Messages.Compiles_the_TypeScript_project_located_at_the_specified_path);
        Example(["tsc --help --all"], Messages.An_expanded_version_of_this_information_showing_all_possible_compiler_options);
        Example(["tsc --noEmit", "tsc --target esnext"], Messages.Compiles_the_current_project_with_additional_settings);
        bool Command(OptionDefinition option) => option.IsCommandLineOnly || option.Category == Messages.Command_line_Options;
        HelpSection(Messages.COMMAND_LINE_FLAGS, compiler.Where(option => option.SimplifiedHelp && Command(option)));
        HelpSection(Messages.COMMON_COMPILER_OPTIONS, compiler.Where(option => option.SimplifiedHelp && !Command(option)), after: after);

        void Example(string[] examples, DiagnosticMessage description)
        {
            foreach (var example in examples) output.Write("  " + Blue(example) + "\n");
            output.Write("  " + Localize(description) + "\n\n");
        }
    }

    private void HelpSection(DiagnosticMessage heading, IEnumerable<OptionDefinition> definitions, bool categories = false, string? before = null, string? after = null)
    {
        output.Write(Bold(Localize(heading)) + "\n\n");
        if (before is not null) output.Write(before + "\n\n");
        if (categories)
        {
            foreach (var group in definitions.Where(option => option.Category is not null).GroupBy(option => Localize(option.Category!)))
            { output.Write("### " + group.Key + "\n\n"); HelpGroup(group.ToArray()); }
        }
        else HelpGroup(definitions.ToArray());
        if (after is not null) output.Write(after + "\n\n");
    }

    private static string DisplayName(OptionDefinition option) => "--" + option.Name + (option.ShortName.IsEmpty ? "" : ", -" + option.ShortName);
    private void HelpGroup(OptionDefinition[] definitions)
    {
        int left = definitions.Select(option => DisplayName(option).Length).DefaultIfEmpty().Max() + 2, right = left + 2;
        foreach (var option in definitions)
        {
            string name = DisplayName(option), description = option.Description is null ? "" : Localize(option.Description);
            var kind = option.Kind;
            string? candidate = kind == OptionKind.Object ? null : kind == OptionKind.List && option.ElementKind == OptionKind.Object ? "" : kind is OptionKind.Enum || option.ElementKind == OptionKind.Enum
                ? string.Join(", ", option.Values.Select((value, index) => (value, identity: option.ValueIdentities[index]))
                    .Where(pair => !option.DeprecatedValues.Contains(pair.value)).GroupBy(pair => pair.identity).Select(group => string.Join("/", group.Select(pair => pair.value.ToString()))))
                : (kind == OptionKind.List ? option.ElementKind : kind).ToString().ToLowerInvariant();
            string type = Localize(kind == OptionKind.List ? Messages.X_one_or_more_Colon : kind == OptionKind.Enum ? Messages.X_one_of_Colon : Messages.X_type_Colon);
            string defaultValue = option.DefaultDescription is null ? option.DefaultValue.ToString() : Localize(option.DefaultDescription);
            if (option.Kind == OptionKind.Enum && option.DefaultDescription is null && !option.EnumDefaultValue && defaultValue != "undefined")
                defaultValue = "";
            bool additional = option.Category != Messages.Command_line_Options && !(candidate == "string" && option.DefaultValue.ToString() is "undefined" or "false" or "n/a" && option.DefaultDescription is null);
            if (terminalWidth >= 80)
            {
                PrettyRow(name, description, left, right, true); output.Write('\n');
                if (additional)
                {
                    if (candidate is not null) { PrettyRow(type, candidate, left, right, false); output.Write('\n'); }
                    if (defaultValue.Length != 0) { PrettyRow(Localize(Messages.X_default_Colon), defaultValue, left, right, false); output.Write('\n'); }
                }
                output.Write('\n');
            }
            else
            {
                output.Write(Blue(name) + "\n" + description + "\n");
                if (additional)
                {
                    if (candidate is not null) output.Write(type + " " + candidate);
                    if (defaultValue.Length != 0) output.Write((candidate is null ? "" : "\n") + Localize(Messages.X_default_Colon) + " " + defaultValue);
                    output.Write('\n');
                }
                output.Write('\n');
            }
        }
        if (definitions.Length == 0) output.Write('\n');
    }

    private void PrettyRow(string leftText, string rightText, int left, int right, bool color)
    {
        var bytes = Utf8String.FromString(rightText);
        bool first = true;
        while (!bytes.IsEmpty)
        {
            string prefix = first ? leftText.PadLeft(left).PadRight(right) : new string(' ', right);
            if (first && color) prefix = Blue(prefix);
            int length = Math.Min(Math.Max(1, terminalWidth - right), bytes.Length);
            output.Write(prefix + bytes[..length].ToString() + "\n"); bytes = bytes[length..]; first = false;
        }
    }
}
