using System.Diagnostics;
using System.Text;
using TypeScript.Compiler.Execution;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Text;

// Startup accounting for the fixed per-process cost: with TSHARP_STARTUP_TRACE set, report how
// much of the process lifetime is spent inside Main. The harness measures the whole process, so
// the difference isolates runtime/AOT image initialisation from our own startup work.
var trace = Environment.GetEnvironmentVariable("TSHARP_STARTUP_TRACE") is { Length: > 0 };
var mainStart = Stopwatch.GetTimestamp();
var processStart = Environment.TickCount64;

Console.OutputEncoding = new UTF8Encoding(false);
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    if (args.Length > 0 && args[0] == "--api") return await ApiCommand.RunAsync(args[1..], cancellation.Token);
    if (args.Length > 0 && args[0] == "--lsp") return await LspCommand.RunAsync(args[1..], cancellation.Token);
    await using var command = new CompilerCommand(new LibraryFileSystem(new PhysicalFileSystem()),
        Utf8String.FromString(Environment.CurrentDirectory), Console.Out, outputIsTerminal: !Console.IsOutputRedirected)
    { TerminalWidth = Console.IsOutputRedirected ? 0 : Console.WindowWidth };
    var status = await command.ExecuteAsync(args.Select(Utf8String.FromString).ToArray(), cancellation.Token);
    if (command.Watches is not null) await command.RunWatchAsync(cancellation.Token);
    return (int)status;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
finally
{
    Console.CancelKeyPress -= cancel;
    if (trace)
        Console.Error.WriteLine($"startup: in-main {Stopwatch.GetElapsedTime(mainStart).TotalMilliseconds:F1} ms, in-process {Environment.TickCount64 - processStart} ms");
}
