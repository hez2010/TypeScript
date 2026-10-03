using System.Buffers.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.Api;

internal sealed class ApiOpenState(Utf8StringComparer comparer)
{
    internal HashSet<Utf8String> Projects { get; } = new(comparer);
    internal HashSet<Utf8String> Files { get; } = new(comparer);
    internal ApiOpenState Clone()
    {
        var result = new ApiOpenState(comparer); result.Projects.UnionWith(Projects); result.Files.UnionWith(Files); return result;
    }
}

/// <summary>Registry scopes follow snapshots for symbols and projects for checker-local identities.</summary>
internal sealed partial class ApiSnapshotData(ProjectWorkspaceSnapshot snapshot, IAsyncDisposable ownership,
    ApiOpenState opens, IFileSystem? fileSystem)
{
    private readonly object sync = new();
    private readonly Dictionary<ulong, (Symbol Symbol, Utf8String Project)> symbols = [];
    private readonly Dictionary<Utf8String, ProjectRegistry> projects = [];
    private readonly Dictionary<SourceFileNode, AstNodeIndexTable> nodeTables = [];
    internal ProjectWorkspaceSnapshot Snapshot { get; } = snapshot;
    internal IAsyncDisposable Ownership { get; } = ownership;
    internal ApiOpenState Opens { get; } = opens;
    internal IFileSystem? FileSystem { get; } = fileSystem;
    internal int References = 1;

    private sealed class ProjectRegistry
    {
        internal readonly Dictionary<uint, Type> Types = [];
        internal readonly Dictionary<ulong, Signature> Signatures = [];
    }
    internal ProjectSnapshot Project(Utf8String id) => Snapshot.GetProject(id) ?? throw new ApiException($"project {id} not found");
    internal CompilerProgram Program(Utf8String id) => Project(id).Program ?? throw new ApiException("project has no program");
    internal (ulong Id, Utf8String Project) Register(Symbol symbol, Utf8String project)
    {
        if (project.IsEmpty) throw new ArgumentException("A symbol requires a canonical project", nameof(project));
        lock (sync)
        {
            ulong id = (ulong)symbol.Id;
            if (!symbols.TryGetValue(id, out var existing)) symbols.Add(id, existing = (symbol, project));
            else if (!ReferenceEquals(existing.Symbol, symbol)) throw new InvalidOperationException("Duplicate symbol identity");
            return (id, existing.Project);
        }
    }
    private ProjectRegistry Registry(Utf8String id)
    {
        if (id.IsEmpty) throw new ApiException("empty project ID");
        if (!projects.TryGetValue(id, out var registry)) projects.Add(id, registry = new());
        return registry;
    }
    internal uint Register(Type type, Utf8String project)
    {
        lock (sync)
        {
            var types = Registry(project).Types;
            if (types.TryGetValue(type.Id, out var existing) && !ReferenceEquals(type, existing))
                throw new InvalidOperationException("Duplicate type identity");
            types[type.Id] = type; return type.Id;
        }
    }
    internal uint Register(Signature signature, Utf8String project)
    {
        lock (sync)
        {
            var signatures = Registry(project).Signatures;
            if (signatures.TryGetValue(signature.Id, out var existing) && !ReferenceEquals(signature, existing))
                throw new InvalidOperationException("Duplicate signature identity");
            signatures[signature.Id] = signature; return signature.Id;
        }
    }
    internal (Symbol Symbol, Utf8String Project) Symbol(ulong id)
    {
        if (id == 0) throw new ApiException("empty symbol handle");
        lock (sync) return symbols.TryGetValue(id, out var symbol) ? symbol : throw new ApiException($"symbol handle {id} not found in snapshot registry");
    }
    internal Type Type(Utf8String project, uint id)
    {
        if (id == 0) throw new ApiException("empty type handle");
        if (project.IsEmpty) throw new ApiException($"empty project ID for type handle {id}");
        lock (sync)
        {
            if (!projects.TryGetValue(project, out var registry))
                throw new ApiException($"type handle {id} not found (no registry for project {project})");
            return registry.Types.TryGetValue(id, out var type) ? type
                : throw new ApiException($"type handle {id} not found in project registry");
        }
    }
    internal Signature Signature(Utf8String project, ulong id)
    {
        if (id == 0) throw new ApiException("empty signature handle");
        if (project.IsEmpty) throw new ApiException($"empty project ID for signature handle {id}");
        lock (sync)
        {
            if (!projects.TryGetValue(project, out var registry))
                throw new ApiException($"signature handle {id} not found (no registry for project {project})");
            return registry.Signatures.TryGetValue(id, out var signature) ? signature
                : throw new ApiException($"signature handle {id} not found in project registry");
        }
    }
    internal async ValueTask<AstNodeIndexTable> NodeTableAsync(SourceFileNode source, CancellationToken cancellation)
    {
        lock (sync) if (nodeTables.TryGetValue(source, out var existing)) return existing;
        var table = await AstEncoder.GetNodeIndexTableAsync(source, cancellation).ConfigureAwait(false);
        lock (sync) nodeTables[source] = table;
        return table;
    }
    internal async ValueTask<Utf8String> NodeHandleAsync(SyntaxNode node, CancellationToken cancellation)
    {
        SyntaxNode source = node;
        while (source.Parent is not null) source = source.Parent;
        if (source is not SourceFileNode file) throw new ApiException("node has no source file");
        uint index = (await NodeTableAsync(file, cancellation).ConfigureAwait(false)).GetIndex(node);
        if (index == 0) throw new ApiException("node is not in the source file index");
        return Utf8String.Format(index) + "."u8 + Utf8String.Format((uint)node.Kind) + "."u8 + Snapshot.Host.Path(file.FileName);
    }
    internal async ValueTask<SyntaxNode> ResolveNodeAsync(CompilerProgram program, Utf8String handle, CancellationToken cancellation)
    {
        int first = handle.IndexOf((byte)'.');
        int second = first < 0 ? -1 : handle[(first + 1)..].IndexOf((byte)'.');
        if (first < 0 || second < 0 || !Utf8Parser.TryParse(handle.Span[..first], out uint index, out int consumed) || consumed != first)
            throw new ApiException($"invalid node handle \"{handle}\"");
        var path = handle[(first + second + 2)..];
        var file = program.GetFileByPath(path)?.Syntax;
        // Kind is informational in the API handle; only the index and canonical path select a node.
        var node = file is null ? null : (await NodeTableAsync(file, cancellation).ConfigureAwait(false)).GetNode(index);
        return node ?? throw new ApiException($"node handle \"{handle}\" could not be resolved (file may not be loaded or handle may be stale)");
    }
}
