using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    private sealed partial class Builder
    {
        private readonly Dictionary<ContentMapper, SemaphoreSlim> mapperGates = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ContentMapper, int> mapperFailures = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<ContentMapper> mapperInitializationFailures = new(ReferenceEqualityComparer.Instance);

        private async ValueTask<ParsedSource> ParseMapped(ContentMapper mapper, ParseOptions options, SourceText original,
            ReferenceResolutionMode format, string packageDirectory, string packageType)
        {
            SemaphoreSlim gate;
            lock (mapperGates)
                if (!mapperGates.TryGetValue(mapper, out gate!))
                    mapperGates[mapper] = gate = new(1, 1);
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            var globals = new List<Diagnostic>();
            try
            {
                int failures;
                bool disabled;
                lock (mapperGates)
                {
                    failures = mapperFailures.GetValueOrDefault(mapper);
                    disabled = mapperInitializationFailures.Contains(mapper) || failures >= 5;
                }
                if (!disabled)
                {
                    try
                    {
                        if (mapperProject is null)
                            throw new MapperException(MapperFailure.Project, "Mapper project is unavailable");
                        var mapped = await mapperProject.TransformAndParseAsync(
                            mapper,
                            options,
                            original,
                            cancellation).ConfigureAwait(false);
                        foreach (var extra in mapped.Supplemental)
                            if (fs.FileExists(extra.Syntax.FileName))
                                throw new MapperException(
                                    MapperFailure.Response,
                                    "Supplemental output conflicts with an existing file: " + extra.Syntax.FileName);
                        MappedSourceFile Reuse(MappedSourceFile value)
                        {
                            if (previous?.GetFile(value.Syntax.FileName) is { Mapping: { } old } prior && prior.ParseOptions == options
                                && old.VirtualFileName == value.VirtualFileName && old.TransformIdentity == value.TransformIdentity
                                && old.Original.Bytes.Span.SequenceEqual(original.Bytes.Span) && old.Syntax.Source.Bytes.Span.SequenceEqual(value.Syntax.Source.Bytes.Span))
                            {
                                Interlocked.Increment(ref reused);
                                return value with { Syntax = old.Syntax };
                            }
                            return value;
                        }
                        mapped = new(Reuse(mapped.Canonical), mapped.Supplemental.Select(Reuse).ToArray());
                        var extension = mapped.Canonical.VirtualFileName;
                        if (extension.EndsWith(".mts", StringComparison.Ordinal) || extension.EndsWith(".mjs", StringComparison.Ordinal))
                            format = ReferenceResolutionMode.Import;
                        else if (extension.EndsWith(".cts", StringComparison.Ordinal)
                            || extension.EndsWith(".cjs", StringComparison.Ordinal))
                            format = ReferenceResolutionMode.Require;
                        return new(mapped.Canonical.Syntax, options, format, packageDirectory, packageType, mapped);
                    }
                    catch (Exception error) when (error is MapperException or MappingException)
                    {
                        bool initialize = error is MapperException { Stage: MapperFailure.Initialize };
                        if (initialize)
                        {
                            lock (mapperGates)
                                mapperInitializationFailures.Add(mapper);
                            globals.Add(new(Messages.The_content_mapper_0_could_not_be_initialized, 0, 0, [mapper.Name]));
                        }
                        else
                        {
                            lock (mapperGates)
                                mapperFailures[mapper] = ++failures;
                            if (failures == 5)
                                globals.Add(
                                    new(Messages.The_content_mapper_0_failed_1_times_and_will_not_be_used, 0, 0, [mapper.Name, "5"]));
                        }
                        var empty = await Empty().ConfigureAwait(false);
                        if (!initialize)
                            empty.Canonical.Syntax.ParseDiagnostics = [MappingDiagnostic(mapper, options.FileName, error)];
                        return new(empty.Canonical.Syntax, options, format, packageDirectory, packageType, empty, globals.ToArray());
                    }
                }
                var unavailable = await Empty().ConfigureAwait(false);
                return new(unavailable.Canonical.Syntax, options, format, packageDirectory, packageType, unavailable);
            }
            finally
            {
                gate.Release();
            }

            async ValueTask<MappedSourceFiles> Empty()
            {
                var file = await Parser.ParseSourceFileAsync(
                    options with { ScriptKind = ScriptKind.TS },
                    new SourceText(""),
                    cancellation).ConfigureAwait(false);
                return new(new(file, original, new SpanMap([]), options.FileName + ".ts", ContentMapperHost.Identity(mapper), "", []), []);
            }
        }

        private static Diagnostic MappingDiagnostic(ContentMapper mapper, string file, Exception error)
        {
            DiagnosticMessage message = Messages.The_content_mapper_0_did_not_provide_the_required_position_mappings;
            TextSlice[] args = [mapper.Name];
            if (error is MappingException mapping)
            {
                message = mapping.Kind switch
                {
                    MappingErrorKind.Overlap => Messages.The_content_mapper_0_produced_overlapping_or_out_of_order_position_mappings_near_virtual_offset_1,
                    MappingErrorKind.OutOfBounds => Messages.The_content_mapper_0_produced_a_position_mapping_that_points_outside_the_original_content_original_offset_1,
                    MappingErrorKind.VerbatimMismatch => Messages.The_content_mapper_0_produced_a_verbatim_mapping_that_does_not_match_the_original_content_virtual_offset_1_original_offset_2,
                    MappingErrorKind.Kind => Messages.The_content_mapper_0_produced_a_position_mapping_with_an_invalid_kind_near_virtual_offset_1,
                    MappingErrorKind.Feature => Messages.The_content_mapper_0_produced_invalid_mapping_features_near_original_offset_1,
                    _ => message
                };
                args = mapping.Kind == MappingErrorKind.VerbatimMismatch ?
                    [
                        mapper.Name,
                        TextSlice.Format(mapping.VirtualPosition),
                        TextSlice.Format(mapping.OriginalPosition)
                    ]
                    :
                        [
                            mapper.Name,
                            TextSlice.Format((mapping.Kind is MappingErrorKind.OutOfBounds or MappingErrorKind.Feature
                                ? mapping.OriginalPosition
                                : mapping.VirtualPosition))
                        ];
            }
            else
                message = Messages.The_content_mapper_0_failed_to_transform_this_file;
            return new(message, 0, 0, args) { FileName = file };
        }
    }
}
