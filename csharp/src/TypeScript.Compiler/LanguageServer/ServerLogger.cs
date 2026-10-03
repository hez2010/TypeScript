namespace TypeScript.Compiler.LanguageServer;

internal sealed class ServerLogger(TextWriter errors, Func<bool> initialized, Action<int, Utf8String> send)
{
    private int verbosity = 3;
    internal void SetVerbosity(int value) => Volatile.Write(ref verbosity, value);
    internal bool IsTracing => Volatile.Read(ref verbosity) == 1;
    internal void MapperMessage(Utf8String message) { if (IsTracing) Log(3, message); }

    internal void Log(int type, Utf8String message)
    {
        if (!initialized()) { lock (errors) errors.WriteLine(message.ToString()); return; }
        int level = Volatile.Read(ref verbosity);
        int threshold = type switch { 1 => 5, 2 => 4, 5 => 2, _ => 3 };
        if (level != 0 && level <= threshold) send(type, message);
    }
}
