using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Mapping;

public sealed record MapperOptionDiagnostic(ContentMapper Mapper, JsonElement[] Path, Utf8String Source, DiagnosticCode Code, Utf8String Message);
public readonly record struct MapperTiming(Utf8String Mapper, Utf8String Operation, long Count, TimeSpan Duration);

/// <summary>Owns lazily started mapper processes and retained project configurations.</summary>
public sealed class ContentMapperHost : IAsyncDisposable
{
    internal sealed class ProcessEntry(ContentMapper mapper)
    {
        internal readonly ContentMapper Mapper = mapper;
        internal Task<MapperProcess>? Process;
        internal int References;
    }

    internal sealed class ProjectEntry(ContentMapper mapper, CompilerOptions options, Utf8String config, Utf8String handle, ProcessEntry process)
    {
        internal readonly ContentMapper Mapper = mapper;
        internal readonly CompilerOptions Options = options;
        internal readonly Utf8String Config = config, Handle = handle;
        internal readonly ProcessEntry Process = process;
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal volatile bool Opened, Closed;
        internal Task? Opening;
        internal Utf8String ConfigIdentity = default;
        internal Utf8String[] WatchedFiles = [];
        internal MapperOptionDiagnostic[] Diagnostics = [];
    }

    internal sealed class ProjectLease(ParsedConfig config, ProjectEntry[] entries)
    {
        internal readonly ParsedConfig Config = config;
        internal readonly ProjectEntry[] Entries = entries;
        internal int References = 1;
    }

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Utf8String, ProcessEntry> processes = new(Utf8StringComparer.Ordinal);
    private readonly Dictionary<ParsedConfig, ProjectLease> projects = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(Utf8String Mapper, Utf8String Operation), (long Count, long Ticks)> timings = [];
    private readonly Action<Utf8String>? log;
    private Utf8String locale;
    private long nextProject;
    private bool closed;
    internal Func<ProcessStartInfo, Process>? StartProcess { get; init; }

    public ContentMapperHost(Utf8String locale = default, Action<Utf8String>? log = null)
    {
        this.locale = locale;
        this.log = log;
    }

    public async ValueTask<ContentMapperProject> GetProjectAsync(ParsedConfig config, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (config.ContentMappers.Length > 0 && config.Options.RunExternalCode != true)
                throw new InvalidOperationException("Content mappers require runExternalCode");
            if (projects.TryGetValue(config, out var existing))
            {
                existing.References++;
                return new(this, existing);
            }
            var options = new CompilerOptions();
            options.Merge(config.Options);
            var entries = new List<ProjectEntry>();
            foreach (var mapper in config.ContentMappers)
            {
                Utf8String identity = Identity(mapper);
                if (!processes.TryGetValue(identity, out var process))
                    processes[identity] = process = new(mapper);
                process.References++;
                entries.Add(new(mapper, options, config.FileName, identity + Utf8Literals.Colon + nextProject++, process));
            }
            var lease = new ProjectLease(config, entries.ToArray());
            projects.Add(config, lease);
            return new(this, lease);
        }
        finally
        {
            gate.Release();
        }
    }

    public static Utf8String Identity(ContentMapper mapper)
    {
        var name = mapper.Name + (mapper.Version.Length == 0 ? Utf8String.Empty : Utf8Literals.At + mapper.Version);
        return mapper.ContributionId.IsEmpty ? name : mapper.ContributionId + " ("u8 + name + ")"u8;
    }

    internal static byte[] DeclaredOptions(ContentMapper mapper, CompilerOptions options)
        => SerializeOptions(options, mapper.CompilerOptions is { ValueKind: JsonValueKind.Array } names
            ? names.EnumerateArray().Select(JsonStrings.GetString) : [], true);

    private static byte[] SerializeOptions(CompilerOptions options, IEnumerable<Utf8String> names, bool declaredOnly)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            var seen = new HashSet<Utf8String>(Utf8StringComparer.Ordinal);
            foreach (Utf8String key in names)
            {
                if (!seen.Add(key))
                    continue;
                if (options.Get(key) is not { } value || value.ValueKind == JsonValueKind.Null)
                    continue;
                var definition = OptionDefinitions.All.FirstOrDefault(d => d.Group == OptionGroup.Compiler && d.Name == key);
                if (definition is null && declaredOnly)
                    continue;
                if (value.ValueKind == JsonValueKind.Number && value.GetDouble() == 0 && definition?.Kind != OptionKind.Number)
                    continue;
                if (value.ValueKind == JsonValueKind.String && JsonStrings.GetString(value).Length == 0)
                    continue;
                writer.WritePropertyName(key);
                if (definition?.Kind == OptionKind.Enum && value.ValueKind == JsonValueKind.String)
                    writer.WriteRawValue(
                        OptionDefinitions.EnumValueJson(definition.ValueIdentity(JsonStrings.GetString(value)) ?? JsonStrings.Raw(value)));
                else if (definition?.ElementKind == OptionKind.Enum && value.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    foreach (var element in value.EnumerateArray())
                    {
                        Utf8String text = JsonStrings.GetString(element);
                        int index = Array.FindIndex(definition.Values, v => v.Equals(text, StringComparison.OrdinalIgnoreCase));
                        if (index < 0)
                            WriteCanonical(writer, element);
                        else
                            writer.WriteRawValue(OptionDefinitions.EnumValueJson(definition.ValueIdentities[index]));
                    }
                    writer.WriteEndArray();
                }
                else
                    WriteCanonical(writer, value);
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        var pending = new Stack<(JsonElement Value, Utf8String? Name, bool Close)>();
        pending.Push((value, null, false));
        while (pending.TryPop(out var item))
        {
            if (item.Close)
            {
                if (item.Value.ValueKind == JsonValueKind.Object)
                    writer.WriteEndObject();
                else
                    writer.WriteEndArray();
                continue;
            }
            if (item.Name is { } name)
                JsonStrings.WriteEncodedName(writer, ProtocolString(name).AsSpan()[1..^1]);
            switch (item.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    pending.Push((item.Value, null, true));
                    foreach (var entry in item.Value.EnumerateObject().Reverse())
                        pending.Push((entry.Value, JsonStrings.GetName(entry), false));
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    pending.Push((item.Value, null, true));
                    for (int i = item.Value.GetArrayLength() - 1; i >= 0; i--)
                        pending.Push((item.Value[i], null, false));
                    break;
                case JsonValueKind.String:
                    writer.WriteRawValue(ProtocolString(JsonStrings.GetString(item.Value)), skipInputValidation: true);
                    break;
                case JsonValueKind.Number:
                    if (item.Value.TryGetInt64(out long integer))
                        writer.WriteNumberValue(integer);
                    else
                        writer.WriteNumberValue(item.Value.GetDouble());
                    break;
                default:
                    item.Value.WriteTo(writer);
                    break;
            }
        }
    }

    private static byte[] ProtocolString(Utf8String value)
    {
        // System.Text.Json escapes supplementary scalars even with its relaxed encoder.
        // Mapper fingerprints use Go's compact UTF-8 JSON spelling, so those bytes must remain literal.
        var text = new Utf8StringBuilder(value.Length + 2).Append((byte)'"');
        for (int i = 0; i < value.Length; i++)
        {
            int c = Wtf8.Decode(value.Span[i..], out int width);
            i += width - 1;
            ReadOnlySpan<byte> escape = c switch
            {
                '"' => "\\\""u8,
                '\\' => "\\\\"u8,
                '\b' => "\\b"u8,
                '\f' => "\\f"u8,
                '\n' => "\\n"u8,
                '\r' => "\\r"u8,
                '\t' => "\\t"u8,
                _ => default
            };
            if (!escape.IsEmpty)
                text.Append(escape);
            else if (c < ' ' || c is >= 0xD800 and <= 0xDFFF)
                text.Append("\\u"u8).Append(Utf8String.Format(c, "x4"));
            else
                text.AppendCodePoint(c);
        }
        return text.Append((byte)'"').WrittenSpan.ToArray();
    }

    internal static Utf8String TransformIdentity(ProjectEntry entry)
    {
        using var stream = new MemoryStream();
        Utf8String identity = Identity(entry.Mapper);
        ReadOnlySpan<byte> rawOptions = entry.Mapper.Options is { } options ? JsonStrings.Raw(options).Span : [];
        stream.Write(identity.Span);
        stream.WriteByte(0);
        stream.Write(rawOptions);
        stream.WriteByte(0);
        stream.Write(DeclaredOptions(entry.Mapper, entry.Options));
        byte[] hash = XxHash128.Hash(stream.GetBuffer().AsSpan(0, (int)stream.Length));
        if (entry.Mapper.DynamicConfig)
        {
            stream.SetLength(0);
            stream.Write(identity.Span);
            stream.WriteByte(0);
            stream.Write(rawOptions);
            stream.WriteByte(0);
            stream.Write(entry.ConfigIdentity.Span);
            stream.WriteByte(0);
            stream.Write(hash);
            hash = XxHash128.Hash(stream.GetBuffer().AsSpan(0, (int)stream.Length));
        }
        Span<byte> hexadecimal = stackalloc byte[hash.Length * 2];
        Convert.TryToHexStringLower(hash, hexadecimal, out _);
        return Utf8String.Concat(identity, ":"u8, hexadecimal);
    }

    private void Record(ContentMapper mapper, Utf8String operation, long started)
    {
        lock (timings)
        {
            var key = (Identity(mapper), operation);
            var value = timings.GetValueOrDefault(key);
            timings[key] = (value.Count + 1, value.Ticks + Stopwatch.GetTimestamp() - started);
        }
    }

    public IReadOnlyList<MapperTiming> Timings()
    {
        lock (timings)
            return timings.OrderBy(e => e.Key.Mapper, Utf8StringComparer.Ordinal).ThenBy(e => e.Key.Operation, Utf8StringComparer.Ordinal)
            .Select(
                e => new MapperTiming(
                    e.Key.Mapper,
                    e.Key.Operation,
                    e.Value.Count,
                    TimeSpan.FromSeconds((double)e.Value.Ticks / Stopwatch.Frequency))).ToArray();
    }

    private async Task<MapperProcess> Start(ProcessEntry entry, Utf8String locale)
    {
        long time = Stopwatch.GetTimestamp();
        try
        {
            return await MapperProcess.Start(entry.Mapper, locale, log, lifetime.Token, StartProcess).ConfigureAwait(false);
        }
        finally
        {
            Record(entry.Mapper, Utf8Literals.Initialize, time);
        }
    }

    internal async ValueTask<MapperProcess> Enter(ProjectEntry entry, CancellationToken cancellation)
    {
        while (true)
        {
            Task<MapperProcess> process;
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(closed || entry.Closed, this);
                process = entry.Process.Process ??= Start(entry.Process, locale);
            }
            finally
            {
                gate.Release();
            }
            await entry.Gate.WaitAsync(cancellation).ConfigureAwait(false);
            if (!ReferenceEquals(process, entry.Process.Process))
            {
                entry.Gate.Release();
                continue;
            }
            try
            {
                ObjectDisposedException.ThrowIf(closed || entry.Closed, this);
                var connection = await process.WaitAsync(cancellation).ConfigureAwait(false);
                if (!entry.Opened)
                {
                    entry.Opening ??= Open();
                    await entry.Opening.WaitAsync(cancellation).ConfigureAwait(false);
                }
                return connection;

                async Task Open()
                {
                    long start = Stopwatch.GetTimestamp();
                    try
                    {
                        var result = await connection.Call(Utf8Literals.OpenProject, writer =>
                        {
                            writer.WriteString("configFileName"u8, entry.Config);
                            writer.WriteString("projectHandle"u8, entry.Handle);
                            if (entry.Mapper.Options is { } options)
                            {
                                writer.WritePropertyName("options"u8);
                                JsonStrings.WriteValue(writer, options);
                            }
                            writer.WritePropertyName("compilerOptions"u8);
                            writer.WriteRawValue(SerializeOptions(entry.Options, entry.Options.Values.Keys, false));
                        }, lifetime.Token).ConfigureAwait(false);
                        Utf8String identity = result.TryGetProperty("configIdentity"u8, out var rawIdentity)
                            ? JsonStrings.GetString(rawIdentity)
                            : Utf8String.Empty;
                        Utf8String[] watched = result.TryGetProperty("watchedFiles"u8, out var files)
                            ? files.EnumerateArray().Select(JsonStrings.GetString).ToArray()
                            : [];
                        if (entry.Mapper.DynamicConfig && identity.Length == 0)
                            throw new InvalidDataException("Dynamic mapper omitted configIdentity");
                        if (!entry.Mapper.DynamicConfig && (identity.Length != 0 || watched.Length != 0))
                            throw new InvalidDataException("Static mapper returned dynamic configuration");
                        if (watched.Any(f => !CompilerPath.IsAbsolute(f)))
                            throw new InvalidDataException("Mapper watch dependencies must be absolute paths");
                        var diagnostics = new List<MapperOptionDiagnostic>();
                        if (result.TryGetProperty("optionDiagnostics"u8, out var errors))
                            foreach (var diagnostic in errors.EnumerateArray())
                            {
                                JsonElement[] path = diagnostic.GetProperty("path"u8).EnumerateArray().Select(p => p.Clone()).ToArray();
                                if (path.Any(
                                    p => p.ValueKind != JsonValueKind.String
                                        && (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out int index) || index < 0)))
                                    throw new InvalidDataException("Invalid mapper option diagnostic path");
                                diagnostics.Add(
                                    new(
                                        entry.Mapper,
                                        path,
                                        connection.DiagnosticSource,
                                        (DiagnosticCode)diagnostic.GetProperty("code"u8).GetInt32(),
                                        JsonStrings.GetString(diagnostic.GetProperty("messageText"u8))));
                            }
                        entry.ConfigIdentity = identity;
                        entry.WatchedFiles = watched;
                        entry.Diagnostics = diagnostics.ToArray();
                        entry.Opened = true;
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or JsonException or InvalidOperationException
                        or KeyNotFoundException
                        || e is OperationCanceledException && !lifetime.IsCancellationRequested)
                    {
                        throw new MapperException(MapperFailure.Project, Utf8Literals.MapperOpenProjectFailed, e);
                    }
                    finally
                    {
                        Record(entry.Mapper, Utf8Literals.OpenProject, start);
                    }
                }
            }
            catch
            {
                if (entry.Opening is { IsFaulted: true } or { IsCanceled: true })
                    entry.Opening = null;
                entry.Gate.Release();
                throw;
            }
        }
    }

    internal async ValueTask<(MapperResult Result, Utf8String Identity)> Transform(
        ProjectEntry entry,
        Utf8String fileName,
        SourceText content,
        CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var connection = await Enter(entry, linked.Token).ConfigureAwait(false);
        long start = Stopwatch.GetTimestamp();
        try
        {
            JsonElement raw;
            try
            {
                raw = await connection.Call(Utf8Literals.Transform, writer =>
                {
                    writer.WriteString("fileName"u8, fileName);
                    writer.WritePropertyName("content"u8);
                    JsonStrings.WriteString(writer, content.Text.Span);
                    writer.WriteString("projectHandle"u8, entry.Handle);
                }, linked.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException
                || e is OperationCanceledException && !linked.IsCancellationRequested)
            {
                throw new MapperException(MapperFailure.Request, Utf8Literals.MapperTransformRequestFailed, e);
            }
            try
            {
                return (MapperOutputDecoder.Decode(
                    raw,
                    content,
                    connection.DiagnosticSource), TransformIdentity(entry));
            }
            catch (MappingException)
            {
                throw;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or InvalidOperationException
                or KeyNotFoundException or OverflowException or FormatException)
            {
                throw new MapperException(MapperFailure.Response, Utf8Literals.MapperReturnedAnInvalidTransform, e);
            }
        }
        finally
        {
            Record(entry.Mapper, Utf8Literals.Transform, start);
            entry.Gate.Release();
        }
    }

    private async ValueTask CloseEntry(ProjectEntry entry, CancellationToken cancellation)
    {
        if (entry.Opening is { } opening)
        {
            try
            {
                await opening.WaitAsync(cancellation).ConfigureAwait(false);
            }
            catch (MapperException) { }
            entry.Opening = null;
        }
        if (!entry.Opened)
            return;
        entry.Opened = false;
        if (entry.Process.Process is { IsCompletedSuccessfully: true } process && process.Result.IsAlive)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                await process.Result.Call(
                    Utf8Literals.CloseProject,
                    w => w.WriteString("projectHandle"u8, entry.Handle),
                    cancellation).ConfigureAwait(false);
            }
            finally
            {
                Record(entry.Mapper, Utf8Literals.CloseProject, start);
            }
        }
    }

    internal async ValueTask Refresh(ProjectLease lease, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (closed)
                return;
            foreach (var entry in lease.Entries)
            {
                await entry.Gate.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    await CloseEntry(entry, cancellation).ConfigureAwait(false);
                }
                finally
                {
                    entry.Gate.Release();
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask Release(ProjectLease lease)
    {
        var failures = new List<Exception>();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (--lease.References != 0)
                return;
            projects.Remove(lease.Config);
            foreach (var entry in lease.Entries)
            {
                entry.Closed = true;
                await entry.Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    try
                    {
                        if (!closed)
                            await CloseEntry(entry, lifetime.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                        failures.Add(e);
                    }
                    finally
                    {
                        if (--entry.Process.References == 0)
                        {
                            processes.Remove(Identity(entry.Mapper));
                            await DisposeProcess(entry.Process).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    entry.Gate.Release();
                }
            }
            if (failures.Count != 0)
                throw new AggregateException(failures);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async ValueTask DisposeProcess(ProcessEntry entry)
    {
        if (entry.Process is not { } task)
            return;
        try
        {
            var process = await task.ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
        }
        catch (MapperException) { }
        catch (OperationCanceledException) { }
        entry.Process = null;
    }

    public async ValueTask SetLocaleAsync(Utf8String locale, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        var held = new List<ProjectEntry>();
        try
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (this.locale == locale)
                return;
            foreach (var entry in projects.Values.SelectMany(p => p.Entries))
            {
                await entry.Gate.WaitAsync(cancellation).ConfigureAwait(false);
                held.Add(entry);
                entry.Opened = false;
            }
            foreach (var process in processes.Values)
                await DisposeProcess(process).ConfigureAwait(false);
            foreach (var entry in held)
            {
                if (entry.Opening is { } opening)
                {
                    try
                    {
                        await opening.ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is MapperException or OperationCanceledException) { }
                }
                entry.Opening = null;
                entry.Opened = false;
            }
            this.locale = locale;
        }
        finally
        {
            foreach (var entry in held)
                entry.Gate.Release();
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed)
                return;
            closed = true;
            foreach (var entry in projects.Values.SelectMany(p => p.Entries))
                entry.Closed = true;
            foreach (var process in processes.Values)
                await DisposeProcess(process).ConfigureAwait(false);
            foreach (var entry in projects.Values.SelectMany(p => p.Entries))
                if (entry.Opening is { } opening)
                {
                    try
                    {
                        await opening.ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is MapperException or OperationCanceledException) { }
                }
            processes.Clear();
            projects.Clear();
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed class ContentMapperProject : IAsyncDisposable
{
    private readonly ContentMapperHost host;
    private readonly ContentMapperHost.ProjectLease lease;
    private int disposed;

    internal ContentMapperProject(ContentMapperHost host, ContentMapperHost.ProjectLease lease)
    {
        this.host = host;
        this.lease = lease;
    }

    private ContentMapperHost.ProjectEntry Entry(ContentMapper mapper)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return lease.Entries.FirstOrDefault(e => ReferenceEquals(
            e.Mapper,
            mapper)) ?? throw new ArgumentException("Mapper is not in this project", nameof(mapper));
    }

    public async ValueTask<Utf8String> IdentityAsync(ContentMapper mapper, CancellationToken cancellation = default)
    {
        var entry = Entry(mapper);
        if (!mapper.DynamicConfig)
            return ContentMapperHost.TransformIdentity(entry);
        await host.Enter(entry, cancellation).ConfigureAwait(false);
        try
        {
            return ContentMapperHost.TransformIdentity(entry);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<Utf8String>> IdentitiesAsync(CancellationToken cancellation = default)
    {
        var values = new List<Utf8String>();
        foreach (var entry in lease.Entries)
            values.Add(await IdentityAsync(entry.Mapper, cancellation).ConfigureAwait(false));
        return values;
    }

    public async ValueTask<IReadOnlyList<Utf8String>> WatchedFilesAsync(CancellationToken cancellation = default)
    {
        var files = new SortedSet<Utf8String>(Utf8StringComparer.Ordinal);
        foreach (var entry in lease.Entries)
            if (entry.Mapper.DynamicConfig)
            {
                await host.Enter(entry, cancellation).ConfigureAwait(false);
                try
                {
                    files.UnionWith(entry.WatchedFiles);
                }
                finally
                {
                    entry.Gate.Release();
                }
            }
        return files.ToArray();
    }

    public IReadOnlyList<MapperOptionDiagnostic> Diagnostics => lease.Entries.Where(e => e.Opened).SelectMany(e => e.Diagnostics).ToArray();

    public ValueTask RefreshAsync(CancellationToken cancellation = default) => host.Refresh(lease, cancellation);

    public async ValueTask<MapperResult> TransformAsync(
        ContentMapper mapper,
        Utf8String fileName,
        SourceText content,
        CancellationToken cancellation = default) =>
            (await host.Transform(Entry(mapper), fileName, content, cancellation).ConfigureAwait(false)).Result;

    public async ValueTask<MappedSourceFiles> TransformAndParseAsync(
        ContentMapper mapper,
        ParseOptions options,
        SourceText original,
        CancellationToken cancellation = default)
    {
        var transformed = await host.Transform(Entry(mapper), options.FileName, original, cancellation).ConfigureAwait(false);
        return await MapperOutputDecoder.Parse(
            transformed.Result,
            options,
            original,
            ContentMapperHost.Identity(mapper),
            transformed.Identity,
            cancellation).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            await host.Release(lease).ConfigureAwait(false);
    }
}
