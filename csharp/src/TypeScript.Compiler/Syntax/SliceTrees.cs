using TypeScript.Compiler.Storage;

namespace TypeScript.Compiler.Syntax;

public static class SliceTrees
{
    public static SliceFile<TTarget> Clone<TSource, TTarget>(SliceFile<TSource> source, TTarget target, CancellationToken cancellation = default) where TSource : INodeStore where TTarget : INodeStore
    {
        var copies = new Dictionary<NodeId, NodeId>();
        var stack = new Stack<(NodeId Node, bool Ready)>();
        stack.Push((source.Root, false));
        while (stack.TryPop(out var frame))
        {
            cancellation.ThrowIfCancellationRequested();
            if (copies.ContainsKey(frame.Node)) continue;
            if (frame.Ready) { copies.Add(frame.Node, SliceSchema.Copy(source.Store, target, frame.Node, copies)); continue; }
            stack.Push((frame.Node, true));
            for (int i = SliceSchema.ChildSlots(source.Store, frame.Node) - 1; i >= 0; i--)
            {
                NodeId child = SliceSchema.ChildAt(source.Store, frame.Node, i);
                if (!child.IsNull) stack.Push((child, false));
            }
        }
        var result = new SliceFile<TTarget>(source.Name, (byte[])source.Text.Clone(), target) { Root = copies[source.Root], ExternalModuleIndicator = source.ExternalModuleIndicator.IsNull ? default : copies[source.ExternalModuleIndicator] };
        result.Diagnostics.AddRange(source.Diagnostics);
        foreach (NodeId import in source.Imports) result.Imports.Add(copies[import]);
        return result;
    }
}
