import {
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import { root } from "./common.mjs";
const utf8 = value => `${JSON.stringify(value)}u8`;
const groups = { Compiler: "declscompiler.go", Build: "declsbuild.go", Watch: "declswatch.go", TypeAcquisition: "declstypeacquisition.go" };
const maps = await readFile(path.join(root, "tsc/internal/tsoptions/enummaps.go"), "utf8");
const enumNames = { target: "targetOptionMap", module: "moduleOptionMap", moduleResolution: "moduleResolutionOptionMap", moduleDetection: "moduleDetectionOptionMap", jsx: "jsxOptionMap", newLine: "newLineOptionMap", lib: "LibMap", watchFile: "watchFileEnumMap", watchDirectory: "watchDirectoryEnumMap", fallbackPolling: "fallbackEnumMap" };
const enums = {};
const identities = {};
for (const [name, variable] of Object.entries(enumNames)) {
    const start = maps.indexOf(`var ${variable} = `);
    if (start < 0) throw new Error(`Missing enum ${variable}`);
    const section = maps.slice(start).split(/\r?\n\}\)/)[0];
    enums[name] = [...section.matchAll(/Key:\s*"([^"]+)"/g)].map(m => m[1]);
    identities[name] = [...section.matchAll(/Key:\s*"([^"]+)",\s*Value:\s*([^}\r\n]+)\}/g)].map(m => m[2].trim().replace(/,$/, ""));
    if (identities[name].length !== enums[name].length) throw new Error(`Unmapped enum values ${name}`);
}
const rows = [];
const compilerSchema = new Map();
const elementText = (await readFile(path.join(root, "tsc/internal/tsoptions/commandlineoption.go"), "utf8")).replace(/^\s*\/\/.*$/gm, "");
const deprecated = Object.fromEntries([...elementText.slice(elementText.indexOf("var commandLineOptionDeprecated"))
    .matchAll(/"([^"]+)":\s*collections\.NewSetFromItems\(([^)]+)\)/g)]
    .map(match => [match[1], [...match[2].matchAll(/"([^"]+)"/g)].map(value => value[1])]));
const elements = Object.fromEntries([...elementText.slice(elementText.indexOf("var commandLineOptionElements")).matchAll(/"([^"]+)":\s*\{([^}]+)\}/g)].map(m => [m[1], m[2]]));
for (const [group, file] of Object.entries(groups)) {
    const text = (await readFile(path.join(root, "tsc/internal/tsoptions", file), "utf8")).replace(/^\s*\/\/.*$/gm, "");
    for (const match of text.matchAll(/\bName:\s*"([^"]+)"/g)) {
        const name = match[1], start = text.lastIndexOf("{", match.index);
        if (group === "TypeAcquisition" && name === "typeAcquisition") continue;
        const body = text.slice(start, text.indexOf("}", match.index));
        const rawKind = body.match(/Kind:\s*CommandLineOptionType(\w+)/)?.[1] ?? body.match(/Kind:\s*"(\w+)"/)?.[1];
        const kind = rawKind && rawKind[0].toUpperCase() + rawKind.slice(1);
        if (!kind) throw new Error(`Missing kind ${file}:${name}`);
        const short = body.match(/ShortName:\s*"([^"]+)"/)?.[1] ?? "";
        const flag = key => new RegExp(`${key}:\\s*true`).test(body);
        const vary = ["Boolean", "Enum"].includes(kind) && !flag("IsCommandLineOnly") && (/Affects\w+:\s*true/.test(body) || ["noEmit", "isolatedModules"].includes(name));
        const element = elements[name] ?? "";
        const elementKind = element.match(/Kind:\s*CommandLineOptionType(\w+)/)?.[1] ?? "String";
        const extra = (body + element).match(/extraValidation:\s*extraValidation(\w+)/)?.[1] ?? "None";
        if (group === "Compiler") compilerSchema.set(name, { kind, elementKind });
        const row = `        new(${utf8(name)}, ${utf8(short)}, OptionGroup.${group}, OptionKind.${kind}, ${flag("IsFilePath")}, ${flag("IsTSConfigOnly")}, ${flag("IsCommandLineOnly")}, [${(enums[name] ?? []).map(utf8).join(", ")}], [${(identities[name] ?? []).map(utf8).join(", ")}], ${vary}) { ElementKind = OptionKind.${elementKind}, ElementIsFilePath = ${/IsFilePath:\s*true/.test(element)}, Minimum = ${body.match(/minValue:\s*(\d+)/)?.[1] ?? 0}, AllowConfigDir = ${flag("allowConfigDirTemplateSubstitution") || flag("IsFilePath")}, PreserveFalsy = ${flag("listPreserveFalsyValues")}, Validation = OptionValidation.${extra} },`;
        const withDeprecated = deprecated[name] ? row.replace(" },", `, DeprecatedValues = [${deprecated[name].map(utf8).join(", ")}] },`) : row;
        rows.push(withDeprecated);
        if (group === "Compiler" && match.index < text.indexOf("var optionsForCompiler")) rows.push(withDeprecated.replace("OptionGroup.Compiler", "OptionGroup.Build"));
    }
}
const compilerOptionsGo = await readFile(path.join(root, "tsc/internal/core/compileroptions.go"), "utf8");
const constantsText = compilerOptionsGo + "\n" + await readFile(path.join(root, "tsc/internal/core/watchoptions.go"), "utf8");
const constants = Object.fromEntries([...constantsText.matchAll(/^\s*(\w+)\s+\w+\s*=\s*(\d+)\b/gm)].map(m => ["core." + m[1], m[2]]));
const wire = [...new Set(Object.values(identities).flat().filter(v => v.startsWith("core.")))].sort().map(identity => {
    if (!(identity in constants)) throw Error(`Unmapped option wire value ${identity}`);
    return `        _ when identity == ${utf8(identity)} => ${utf8(constants[identity])},`;
});
const text = ["// <auto-generated />", "namespace TypeScript.Compiler.Configuration;", "public static partial class OptionDefinitions", "{", "    public static IReadOnlyList<OptionDefinition> All { get; } = Array.AsReadOnly<OptionDefinition>([", ...rows, "    ]);", "    public static Utf8String EnumValueJson(Utf8String identity) => identity switch", "    {", ...wire, "        _ => identity,", "    };", "}", ""].join("\n");
const target = path.join(root, "csharp/src/TypeScript.Compiler/Configuration/Options.generated.cs");
await mkdir(path.dirname(target), { recursive: true });
if (process.argv.includes("--check")) {
    if ((await readFile(target, "utf8")).replaceAll("\r\n", "\n") !== text) throw new Error("Stale options schema");
}
else await writeFile(target, text);
const enumTypes = new Set(["ModuleDetectionKind", "ModuleKind", "ModuleResolutionKind", "NewLineKind", "ScriptTarget", "JsxEmit"]);
const goFields = [...compilerOptionsGo.matchAll(/^\s*(\w+)\s+(.+?)\s+`json:"([^,]+),omitzero"/gm)]
    .map(([, property, type, name]) => ({ property, type: type.trim(), name }));
if (goFields.length < 125) throw Error("Incomplete Go CompilerOptions fields");
for (const [name, { kind, elementKind }] of compilerSchema)
    if (!goFields.some(field => field.name === name))
        goFields.push({ name, property: name[0].toUpperCase() + name.slice(1),
            type: kind === "List" && elementKind === "Object" ? "[]JsonElement" : "unsupported" });
const csType = type => type === "Tristate" ? "bool?" : type === "string" ? "Utf8String?"
    : type === "[]string" ? "Utf8String[]?" : type === "*int" ? "int?"
    : type === "*collections.OrderedMap[string, []string]" ? "IReadOnlyList<KeyValuePair<Utf8String, Utf8String[]>>?"
    : type === "[]JsonElement" ? "JsonElement[]?"
    : enumTypes.has(type) ? type : (() => { throw Error(`Unsupported CompilerOptions type ${type}`); })();
const typed = ["// <auto-generated />", "#nullable enable", "using System.Text.Json;", "using TypeScript.Compiler.Text;",
    "namespace TypeScript.Compiler.Configuration;", ""];
for (const type of enumTypes) {
    const members = [...compilerOptionsGo.matchAll(new RegExp(`^\\s*${type}(\\w+)\\s+${type}\\s*=\\s*(\\d+)`, "gm"))];
    if (members.length < 2) throw Error(`Incomplete ${type} enum`);
    typed.push(`public enum ${type}`, "{");
    for (const [, member, value] of members) typed.push(`    ${member} = ${value},`);
    typed.push("}", "");
}
typed.push("public sealed partial class CompilerOptions", "{");
for (const field of goFields)
    typed.push(`    public ${csType(field.type)} ${field.property} { get; private set; }`);
typed.push("", "    private void AssignTyped(Utf8String name, JsonElement value, Utf8String? text)", "    {", "        switch (name)", "        {");
for (const field of goFields) {
    let expression;
    if (field.type === "Tristate") expression = "ToBoolean(value)";
    else if (field.type === "string") expression = "text";
    else if (field.type === "[]string") expression = "ToStrings(value)";
    else if (field.type === "*int") expression = `value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number${field.property}) ? number${field.property} : null`;
    else if (field.type === "*collections.OrderedMap[string, []string]") expression = "ParsePaths(value)";
    else if (field.type === "[]JsonElement") expression = "ToObjects(value)";
    else if (enumTypes.has(field.type)) {
        const names = enums[field.name] ?? [];
        const values = identities[field.name] ?? [];
        if (names.length === 0 || names.length !== values.length) throw Error(`Missing enum map ${field.name}`);
        const alternatives = names.map((name, index) => {
            const value = constants[values[index]];
            if (value === undefined) throw Error(`Missing enum identity ${values[index]}`);
            return `{ } option when option == ${utf8(name)} => (${field.type})${value}`;
        });
        expression = `value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number${field.property}) ? (${field.type})number${field.property} : text switch { ${alternatives.join(", ")}, _ => default }`;
    }
    typed.push(`            case var _ when name == ${utf8(field.name)}: ${field.property} = ${expression}; break;`);
}
typed.push("        }", "    }", "",
    "    private static bool? ToBoolean(JsonElement value) => value.ValueKind switch",
    "    {",
    "        JsonValueKind.True => true,",
    "        JsonValueKind.False => false,",
    "        _ => null",
    "    };",
    "",
    "    private static Utf8String[]? ToStrings(JsonElement value) => value.ValueKind == JsonValueKind.Array",
    "        ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(JsonStrings.GetString).ToArray() : null;",
    "",
    "    private static JsonElement[]? ToObjects(JsonElement value) => value.ValueKind == JsonValueKind.Array",
    "        ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray() : null;",
    "",
    "    internal static IReadOnlyList<KeyValuePair<Utf8String, Utf8String[]>>? ParsePaths(JsonElement value)",
    "    {",
    "        if (value.ValueKind != JsonValueKind.Object) return null;",
    "        var paths = new List<KeyValuePair<Utf8String, Utf8String[]>>();",
    "        foreach (var property in value.EnumerateObject())",
    "        {",
    "            Utf8String name = JsonStrings.GetName(property);",
    "            int index = paths.FindIndex(entry => entry.Key == name);",
    "            var entry = new KeyValuePair<Utf8String, Utf8String[]>(name, ToStrings(property.Value) ?? []);",
    "            if (index >= 0) paths[index] = entry; else paths.Add(entry);",
    "        }",
    "        return paths;",
    "    }",
    "}", "");
const typedText = typed.join("\n");
const typedTarget = path.join(root, "csharp/src/TypeScript.Compiler/Configuration/CompilerOptions.Typed.generated.cs");
if (process.argv.includes("--check")) {
    if ((await readFile(typedTarget, "utf8")).replaceAll("\r\n", "\n") !== typedText)
        throw Error("Stale typed compiler options");
}
else await writeFile(typedTarget, typedText);
console.log(`Generated ${rows.length} option declarations`);
