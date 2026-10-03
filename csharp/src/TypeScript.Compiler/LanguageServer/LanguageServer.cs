using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.LanguageServer;

public sealed record LanguageServerOptions
{
    public required Utf8String CurrentDirectory { get; init; }
    public Utf8String DefaultLibraryDirectory { get; init; }
    public Utf8String TypingsLocation { get; init; }
    public CompilerOptions? InferredCompilerOptions { get; init; }
    public bool LeaveOpen { get; init; }
    public TextWriter ErrorWriter { get; init; } = TextWriter.Null;
    public TimeSpan ProgressDelay { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process>? StartMapperProcess { get; init; }
    internal Watching.IWatchBackend? WatchBackend { get; init; }
}

public static class LanguageServer
{
    public static async Task RunAsync(IFileSystem fileSystem, Stream input, Stream output, LanguageServerOptions options, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CurrentDirectory.IsEmpty) throw new ArgumentException("CurrentDirectory is required", nameof(options));
        await using var session = new LanguageServerSession(fileSystem, options);
        await using var connection = new RpcConnection(input, output, session, new()
        {
            PreserveMessageOrder = true, LeaveOpen = options.LeaveOpen,
            ValidateCancellation = parameters => LspProtocol.ValidateParams("$/cancelRequest"u8, parameters),
        });
        session.Connect(connection);
        await connection.RunAsync(cancellation).ConfigureAwait(false);
    }
}
