using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;

namespace TypeScript.Compiler.Emission;

public sealed record TranspileOptions
{
    public CompilerOptions? CompilerOptions { get; init; }
    public Utf8String FileName { get; init; }
    public bool ReportDiagnostics { get; init; }
}

public sealed record TranspileOutput(Utf8String OutputText, IReadOnlyList<Diagnostic> Diagnostics, Utf8String SourceMapText);

/// <summary>Single-file emission in an isolated, in-memory program.</summary>
public static class Transpiler
{
    public static ValueTask<TranspileOutput> TranspileModuleAsync(Utf8String input, TranspileOptions? options = null,
        CancellationToken cancellation = default) => TranspileAsync(input, options ?? new(), false, cancellation);

    public static ValueTask<TranspileOutput> TranspileDeclarationAsync(Utf8String input, TranspileOptions? options = null,
        CancellationToken cancellation = default) => TranspileAsync(input, options ?? new(), true, cancellation);

    private static async ValueTask<TranspileOutput> TranspileAsync(Utf8String input, TranspileOptions settings, bool declaration,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var options = new CompilerOptions();
        if (settings.CompilerOptions is { } supplied)
            foreach (var (name, value) in supplied.Values)
                if (!IgnoredOptions.Contains(name)) options.Set(name, value);
        if (options.VerbatimModuleSyntax != true) options.SetRaw("isolatedModules"u8, "true"u8);
        foreach (Utf8String name in new Utf8String[] { "noCheck"u8, "noResolve"u8, "suppressOutputPathCheck"u8, "allowNonTsExtensions"u8 })
            options.SetRaw(name, "true"u8);
        options.SetRaw("declaration"u8, declaration ? "true"u8 : "false"u8);
        options.SetRaw("isolatedDeclarations"u8, declaration ? "true"u8 : "false"u8);
        options.SetRaw("noLib"u8, declaration ? "false"u8 : "true"u8);
        if (declaration) options.SetRaw("emitDeclarationOnly"u8, "true"u8);
        else options.SetRaw("declarationMap"u8, "false"u8);
        Utf8String fileName = CompilerPath.Resolve("/"u8, settings.FileName.Length != 0 ? settings.FileName
            : options.Jsx != JsxEmit.None ? "module.tsx"u8 : "module.ts"u8);
        var files = new Dictionary<Utf8String, byte[]> { [fileName] = input.Span.ToArray() };
        if (declaration) files[CompilerPath.Combine("/lib"u8, CompilerProgram.DefaultLibrary(options))] = BarebonesLibrary.Span.ToArray();
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/"u8,
            new(Utf8String.Empty, options, [fileName], [], [], []), concurrency: 1,
            defaultLibraryDirectory: "/lib"u8, cancellation: cancellation, skipModuleResolution: true);
        List<Diagnostic> diagnostics = [];
        if (settings.ReportDiagnostics)
        {
            var source = program.GetFile(fileName)!.Syntax;
            diagnostics.AddRange(source.ParseDiagnostics.Concat(source.JSDiagnostics).Select(item => item with { FileName = fileName }));
            diagnostics.AddRange(program.Configuration.Diagnostics);
            diagnostics.AddRange(program.Diagnostics);
        }
        Utf8String? outputText = null, sourceMapText = null;
        var result = await program.EmitAsync(new()
        {
            Only = declaration ? EmitOnly.Declarations : EmitOnly.All,
            Force = declaration,
            WriteFile = (path, text, _, _) =>
            {
                if (path.EndsWith(".map"u8, StringComparison.Ordinal))
                {
                    if (sourceMapText is not null) throw new InvalidOperationException("Unexpected multiple source map outputs");
                    sourceMapText = text;
                }
                else
                {
                    if (outputText is not null) throw new InvalidOperationException("Unexpected multiple outputs");
                    outputText = text;
                }
                return ValueTask.CompletedTask;
            }
        }, cancellation);
        diagnostics.AddRange(result.Diagnostics);
        return new(outputText ?? throw new InvalidOperationException("Output generation failed"), diagnostics.ToArray(), sourceMapText ?? Utf8String.Empty);
    }

    private static readonly HashSet<Utf8String> IgnoredOptions =
    [
        "incremental"u8, "declaration"u8, "emitDeclarationOnly"u8, "noEmit"u8, "lib"u8, "outFile"u8, "composite"u8,
        "tsBuildInfoFile"u8, "paths"u8, "rootDirs"u8, "types"u8, "allowImportingTsExtensions"u8, "noEmitOnError"u8, "declarationDir"u8
    ];

    private static Utf8String BarebonesLibrary => """
        interface Boolean {}
        interface Function {}
        interface CallableFunction {}
        interface NewableFunction {}
        interface IArguments {}
        interface Number {}
        interface Object {}
        interface RegExp {}
        interface String {}
        interface Array<T> { length: number; [n: number]: T; }
        interface SymbolConstructor {
            (desc?: string | number): symbol;
            for(name: string): symbol;
            readonly toStringTag: symbol;
        }
        declare var Symbol: SymbolConstructor;
        interface Symbol {
            readonly [Symbol.toStringTag]: string;
        }
        """u8;
}
