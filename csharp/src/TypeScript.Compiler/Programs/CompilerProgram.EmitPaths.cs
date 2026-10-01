using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Programs;

public sealed record EmitOutputPaths(Utf8String JavaScript, Utf8String SourceMap, Utf8String Declaration, Utf8String DeclarationMap);

public sealed partial class CompilerProgram
{
    public EmitOutputPaths GetOutputPaths(SourceFileNode source, bool forceDeclarations = false, bool forceJavaScript = false, bool forceDeclarationMap = false)
    {
        var options = Configuration.Options;
        bool json = source.ScriptKind == ScriptKind.JSON;
        var ownPath = SourceInOutputDirectory(source.FileName, options.OutDir);
        ownPath = RemoveKnownExtension(ownPath) + JavaScriptExtension(source.FileName, options.Jsx);
        var javaScript = GetFile(source.FileName)?.Mapping is null && (forceJavaScript || options.EmitDeclarationOnly != true)
            && !(json && CompilerPath.Relative(CompilerPath.Resolve(CurrentDirectory, source.FileName), CompilerPath.Resolve(CurrentDirectory, ownPath), UseCaseSensitiveFileNames).Length == 0)
                ? ownPath : Utf8String.Empty;
        var declaration = forceDeclarations || (options.Declaration == true || options.Composite == true) && !json
            ? DeclarationOutputPath(source.FileName) : Utf8String.Empty;
        return new(javaScript, javaScript.Length != 0 && !json && options.SourceMap == true && options.InlineSourceMap != true ? javaScript + ".map"u8 : Utf8String.Empty,
            declaration, declaration.Length != 0 && options.DeclarationMap == true && (forceDeclarationMap || options.Declaration == true || options.Composite == true)
                ? declaration + ".map"u8 : Utf8String.Empty);
    }

    internal Utf8String DeclarationOutputPath(Utf8String fileName)
    {
        var path = SourceInOutputDirectory(fileName, Configuration.Options.DeclarationDir ?? Configuration.Options.OutDir);
        Utf8String mapperExtension = Utf8String.Empty;
        foreach (var mapper in Configuration.ContentMappers)
            foreach (var extension in mapper.Extensions)
                if (extension.Length > mapperExtension.Length && path.EndsWith(extension, StringComparison.Ordinal)) mapperExtension = extension;
        if (mapperExtension.Length != 0) return path[..^mapperExtension.Length] + ".d"u8 + mapperExtension + ".ts"u8;
        var stem = RemoveKnownExtension(path);
        if (stem == path && CompilerPath.BaseName(path).LastIndexOf((byte)'.') is >= 0 and var dot)
        {
            int start = path.Length - CompilerPath.BaseName(path).Length + dot;
            return path[..start] + ".d"u8 + path[start..] + ".ts"u8;
        }
        Utf8String suffix = path.EndsWith(".mts"u8, StringComparison.Ordinal) || path.EndsWith(".mjs"u8, StringComparison.Ordinal) ? ".d.mts"u8
            : path.EndsWith(".cts"u8, StringComparison.Ordinal) || path.EndsWith(".cjs"u8, StringComparison.Ordinal) ? ".d.cts"u8 : ".d.ts"u8;
        return stem + suffix;
    }

    private Utf8String SourceInOutputDirectory(Utf8String source, Utf8String? directory)
    {
        if (directory is not { Length: > 0 } outputDirectory) return source;
        var path = CompilerPath.Resolve(CurrentDirectory, source);
        var common = CompilerPath.EnsureTrailingSeparator(CommonSourceDirectory);
        if (path.StartsWith(common, UseCaseSensitiveFileNames ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) path = path[common.Length..];
        return CompilerPath.Combine(outputDirectory, path);
    }

    private static Utf8String RemoveKnownExtension(Utf8String path)
    {
        var extension = ModuleResolver.Extension(path);
        return extension == ".ts"u8 || extension == ".tsx"u8 || extension == ".mts"u8 || extension == ".cts"u8
            || extension == ".d.ts"u8 || extension == ".d.mts"u8 || extension == ".d.cts"u8 || extension == ".js"u8
            || extension == ".jsx"u8 || extension == ".mjs"u8 || extension == ".cjs"u8 || extension == ".json"u8
            ? path[..^extension.Length] : path;
    }

    private static Utf8String JavaScriptExtension(Utf8String path, JsxEmit jsx) => path.EndsWith(".json"u8, StringComparison.Ordinal) ? ".json"u8
        : jsx == JsxEmit.Preserve && (path.EndsWith(".tsx"u8, StringComparison.Ordinal) || path.EndsWith(".jsx"u8, StringComparison.Ordinal)) ? ".jsx"u8
        : path.EndsWith(".mts"u8, StringComparison.Ordinal) || path.EndsWith(".mjs"u8, StringComparison.Ordinal) ? ".mjs"u8
        : path.EndsWith(".cts"u8, StringComparison.Ordinal) || path.EndsWith(".cjs"u8, StringComparison.Ordinal) ? ".cjs"u8 : ".js"u8;

    internal SourceFileNode? SourceFromReference(SourceFileNode origin, FileReference reference)
    {
        var path = CompilerPath.Resolve(CompilerPath.DirectoryName(origin.FileName), reference.FileName);
        bool allowNonTs = Configuration.Options.AllowNonTsExtensions == true;
        var extensions = new List<Utf8String> { ".ts"u8, ".tsx"u8, ".d.ts"u8 };
        if (Configuration.Options.AllowJs == true || Configuration.Options.CheckJs == true) extensions.AddRange([".js"u8, ".jsx"u8]);
        if (CompilerPath.BaseName(path).Contains("."u8, StringComparison.Ordinal))
        {
            bool supported = allowNonTs || extensions.Concat([".mts"u8, ".d.mts"u8, ".cts"u8, ".d.cts"u8])
                .Concat(Configuration.Options.AllowJs == true || Configuration.Options.CheckJs == true ? [".mjs"u8, ".cjs"u8] : [])
                .Concat(Configuration.Options.ResolveJsonModule == true ? [".json"u8] : [])
                .Concat(Configuration.ContentMappers.SelectMany(mapper => mapper.Extensions))
                .Any(extension => path.EndsWith(extension, UseCaseSensitiveFileNames ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
            return supported ? GetFile(path)?.Syntax : null;
        }
        if (allowNonTs && GetFile(path) is { } direct) return direct.Syntax;
        foreach (var extension in extensions)
            if (GetFile(path + extension) is { } file) return file.Syntax;
        return null;
    }
}
