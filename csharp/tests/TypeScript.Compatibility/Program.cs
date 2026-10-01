using System.Globalization;
using System.Text.Json;
using TypeScript.Compiler.Experiments;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Storage;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class Program
{
    public static int Main(string[] commandLine)
    {
        Utf8String[] args = commandLine.Select(Utf8String.FromString).ToArray();
        try
        {
            if (args.Length == 1 && args[0] == "--native-command-safety"u8)
            {
                Console.WriteLine($"Native command safety: {WatchTests.CommandSafetyAsync().GetAwaiter().GetResult()} assertions; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--scripted-build-lines"u8)
            {
                ScriptedBuildTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--watch-lines"u8)
            {
                WatchTransitionTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--watch-safety"u8)
            {
                Console.WriteLine($"Watch safety: {WatchTests.SafetyAsync().GetAwaiter().GetResult()} assertions");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--native-watch-safety"u8)
            {
                Console.WriteLine($"Native watch safety: {WatchTests.NativeSafetyAsync().GetAwaiter().GetResult()} assertions; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--build-lines"u8)
            {
                BuildTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--incremental-lines"u8)
            {
                IncrementalTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--incremental-safety"u8)
            {
                Console.WriteLine($"Incremental safety: {IncrementalTests.SafetyAsync().GetAwaiter().GetResult()} assertions");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--build-info-lines"u8)
            {
                BuildInfoTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--build-info-safety"u8)
            {
                Console.WriteLine($"Build info safety: {BuildInfoTests.Safety()} assertions");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--transpile-lines"u8)
            {
                TranspileTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--transpile-safety"u8)
            {
                Console.WriteLine($"Transpile safety: {TranspileTests.Safety().GetAwaiter().GetResult()} assertions");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--program-emit-lines"u8)
            {
                ProgramEmissionTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--program-emit-safety"u8)
            {
                Console.WriteLine($"Program emission safety: {ProgramEmissionTests.Safety().GetAwaiter().GetResult()} assertions");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--declaration-safety"u8)
            {
                Console.WriteLine($"Declaration emit: {DeclarationEmissionTests.Safety().GetAwaiter().GetResult()} assertions; depth 20000");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--declaration-lines"u8)
            {
                DeclarationEmissionTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-workload-lines"u8)
            {
                CheckerWorkloadTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--printer-lines"u8)
            {
                EmissionTests.PrinterLines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--emit-foundations-lines"u8)
            {
                EmissionTests.FoundationLines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--emit-foundations-safety"u8)
            {
                Console.WriteLine($"Emit foundations: {EmissionTests.FoundationSafety().GetAwaiter().GetResult()} assertions; depth 20000");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--module-specifiers-lines"u8)
            {
                ModuleSpecifierTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--module-specifiers-safety"u8)
            {
                Console.WriteLine($"{ModuleSpecifierTests.Safety()} module naming/path/extension/cancellation assertions");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--module-program-specifiers-safety"u8)
            {
                Console.WriteLine(
                    $"Program module specifier safety: {ModuleSpecifierProgramTests.Safety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--diagnostic-node-printer-safety"u8)
            {
                Console.WriteLine($"Diagnostic node printer safety: {DiagnosticNodePrinterTests.Safety()} assertions passed; depth 20000");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--computed-symbols-safety"u8)
            {
                Console.WriteLine(
                    $"Computed symbol safety: {DiagnosticNodePrinterTests.ComputedSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--module-generation-safety"u8)
            {
                Console.WriteLine($"Module generation safety: {ModuleSpecifierGenerationTests.Safety()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--module-node-specifiers-safety"u8)
            {
                Console.WriteLine($"Node module specifier safety: {ModuleSpecifierTests.NodeModuleSafety()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--module-package-specifiers-safety"u8)
            {
                Console.WriteLine($"{ModuleSpecifierTests.PackageSafety()} package-map/output-path/cancellation assertions; depth 20000");
                return 0;
            }
            if (args is [_, var reusedBlobDirectory, var matchedText] && args[0] == "--checker-corpus-lines"u8 && matchedText == "--reuse-syntax"u8)
            {
                CheckerCorpusTests.Lines(reusedBlobDirectory, true);
                return 0;
            }
            if (args is [_, var blobDirectory] && args[0] == "--checker-corpus-lines"u8)
            {
                CheckerCorpusTests.Lines(blobDirectory);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-emit-syntax-safety"u8)
            {
                Console.WriteLine($"Emit syntax safety: {CheckerEmitSyntaxTests.Safety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-node-builder-safety"u8)
            {
                Console.WriteLine(
                    $"Node builder tracking safety: {CheckerNodeBuilderTests.Safety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-return-syntax-safety"u8)
            {
                Console.WriteLine(
                    $"Return recovery safety: {CheckerEmitSyntaxTests.ReturnSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-module-grammar-safety"u8)
            {
                Console.WriteLine(
                    $"Module grammar safety: {CheckerProgramTests.ModuleGrammarSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-program-relations-safety"u8)
            {
                Console.WriteLine(
                    $"Program relation safety: {CheckerProgramTests.ProgramRelationsSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-mapped-exports-safety"u8)
            {
                Console.WriteLine(
                    $"Mapped/export safety: {CheckerProgramTests.MappedExportSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-private-declarations-safety"u8)
            {
                Console.WriteLine(
                    $"Private/declaration safety: {CheckerProgramTests.PrivateDeclarationSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-diagnostic-values-safety"u8)
            {
                Console.WriteLine(
                    $"Diagnostic value safety: {CheckerProgramTests.DiagnosticValueSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-declaration-grammar-safety"u8)
            {
                Console.WriteLine(
                    $"Declaration grammar safety: {CheckerProgramTests.DeclarationGrammarSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-relation-context-safety"u8)
            {
                Console.WriteLine(
                    $"Relation context safety: {CheckerProgramTests.RelationContextSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-module-context-safety"u8)
            {
                Console.WriteLine(
                    $"Module context safety: {CheckerProgramTests.ModuleContextSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-declaration-block-safety"u8)
            {
                Console.WriteLine(
                    $"Declaration block safety: {CheckerProgramTests.DeclarationBlockSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-pool-safety"u8)
            {
                Console.WriteLine($"Checker pool safety: {CheckerPoolTests.Safety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-pool-cancellation"u8)
            {
                Console.WriteLine(
                    $"Checker pool cancellation: {CheckerPoolTests.CancellationSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args is [_, var poolInput, var poolOutput] && args[0] == "--checker-pool-partitions"u8)
            {
                CheckerPoolTests.Partitions(poolInput, poolOutput);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-diagnostic-identity-safety"u8)
            {
                Console.WriteLine(
                    $"Diagnostic identity safety: {CheckerProgramTests.DiagnosticIdentitySafety().GetAwaiter().GetResult()} assertions passed; chain depth 20000");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-recovery-safety"u8)
            {
                Console.WriteLine(
                    $"Syntax recovery safety: {CheckerEmitSyntaxTests.RecoverySafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-emit-links-safety"u8)
            {
                Console.WriteLine(
                    $"Emit linking safety: {CheckerEmitQueryTests.LinkedSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-emit-queries-safety"u8)
            {
                Console.WriteLine($"Emit query safety: {CheckerEmitQueryTests.Safety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-signature-declarations-safety"u8)
            {
                Console.WriteLine(
                    $"Signature declaration safety: {CheckerTypeSyntaxTests.DeclarationSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-syntax-names-safety"u8)
            {
                Console.WriteLine(
                    $"Type syntax names safety: {CheckerTypeSyntaxTests.NamesSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-syntax-options-safety"u8)
            {
                Console.WriteLine(
                    $"Type syntax options safety: {CheckerTypeSyntaxTests.OptionsSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-symbol-type-arguments-safety"u8)
            {
                Console.WriteLine(
                    $"Symbol type arguments safety: {CheckerSymbolDisplayTests.TypeArgumentsSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-signature-syntax-safety"u8)
            {
                Console.WriteLine(
                    $"Signature syntax safety: {CheckerTypeSyntaxTests.SignatureSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-conditional-syntax-safety"u8)
            {
                Console.WriteLine(
                    $"Conditional/property syntax safety: {CheckerTypeSyntaxTests.ConditionalSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-type-syntax-safety"u8)
            {
                Console.WriteLine($"Type syntax core safety: {CheckerTypeSyntaxTests.Safety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-type-renderer-safety"u8)
            {
                Console.WriteLine(
                    $"Diagnostic renderer safety: {CheckerTypeSyntaxTests.DiagnosticSafety().GetAwaiter().GetResult()} assertions passed; array depth 5000");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-display-formats-safety"u8)
            {
                Console.WriteLine(
                    $"Display format safety: {CheckerDisplayTests.FormatSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-symbol-type-nodes-safety"u8)
            {
                Console.WriteLine(
                    $"Symbol type node safety: {CheckerSymbolTypeNodeTests.Safety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-symbol-formats-safety"u8)
            {
                Console.WriteLine(
                    $"Symbol format mode safety: {CheckerSymbolDisplayTests.FormatSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-symbol-display-safety"u8)
            {
                Console.WriteLine(
                    $"Symbol display/accessibility safety: {CheckerSymbolDisplayTests.Safety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-access-safety"u8)
            {
                CheckerAccessTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-identifiers-safety"u8)
            {
                CheckerIdentifierTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-flow-safety"u8)
            {
                CheckerFlowTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-references-safety"u8)
            {
                CheckerReferenceTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-binary-safety"u8)
            {
                CheckerBinaryTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-expressions-safety"u8)
            {
                CheckerExpressionTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-inference-safety"u8)
            {
                CheckerInferenceTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-conditional-safety"u8)
            {
                CheckerConditionalTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-generic-relations-safety"u8)
            {
                CheckerGenericRelationTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-indexing-safety"u8)
            {
                CheckerIndexTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-assignability-safety"u8)
            {
                CheckerAssignabilityTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-relations-safety"u8)
            {
                CheckerRelationTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-relation-diagnostics-safety"u8)
            {
                Console.WriteLine(
                    $"Relation diagnostic safety: {CheckerRelationTests.DiagnosticSafety().GetAwaiter().GetResult()} assertions passed");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-signatures-safety"u8)
            {
                CheckerSignatureTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-properties-safety"u8)
            {
                CheckerPropertyTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-values-safety"u8)
            {
                CheckerSymbolTypeTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-members-safety"u8)
            {
                CheckerMemberTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-type-nodes-safety"u8)
            {
                CheckerTypeNodeTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-alias-safety"u8)
            {
                CheckerAliasTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-query-safety"u8)
            {
                Console.WriteLine(
                    $"{CheckerQueryTests.Safety().GetAwaiter().GetResult()} type-location query ownership/cancellation assertions; depth 20000");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-symbol-query-safety"u8)
            {
                Console.WriteLine(
                    $"{CheckerQueryTests.SymbolSafety().GetAwaiter().GetResult()} symbol-location query identity/ownership/cancellation assertions");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-scope-query-safety"u8)
            {
                Console.WriteLine(
                    $"{CheckerQueryTests.ScopeSafety().GetAwaiter().GetResult()} scope/symbol-type query identity/cancellation assertions");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-context-query-safety"u8)
            {
                Console.WriteLine(
                    $"{CheckerContextQueryTests.Safety().GetAwaiter().GetResult()} context/signature query cache/cancellation assertions");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-visibility-safety"u8)
            {
                Console.WriteLine(
                    $"{CheckerVisibilityTests.Safety().GetAwaiter().GetResult()} declaration visibility/alias/rollback assertions; depth 20000");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-symbol-chains-safety"u8)
            {
                Console.WriteLine(
                    $"{CheckerSymbolChainTests.Safety().GetAwaiter().GetResult()} accessible-chain identity/qualification/rollback assertions; depth 20000");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-accessibility-safety"u8)
            {
                Console.WriteLine(
                    $"{CheckerAccessibilityTests.Safety().GetAwaiter().GetResult()} accessibility/container/entity-visibility/rollback assertions");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-program-safety"u8)
            {
                CheckerProgramTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-program-lines"u8)
            {
                CheckerProgramTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-mapped-members-safety"u8)
            {
                CheckerMappedMemberTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-mapped-safety"u8)
            {
                CheckerMappedTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-objects-safety"u8)
            {
                CheckerObjectTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-instantiation-safety"u8)
            {
                CheckerInstantiationTests.Safety();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-constraints-safety"u8)
            {
                CheckerConstraintTests.Safety();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-algebra-safety"u8)
            {
                CheckerAlgebraTests.Safety();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-names-safety"u8)
            {
                CheckerNameTests.Safety();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-symbols-lines"u8)
            {
                CheckerSymbolTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-names-lines"u8)
            {
                CheckerNameTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-state"u8)
            {
                CheckerStateTests.Safety();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--checker-types-lines"u8)
            {
                CheckerTypeTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--mapper-codec-lines"u8)
            {
                MapperCodecTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--program-safety"u8)
            {
                ProgramGraphTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is [_, var mapperRepository] && args[0] == "--content-mappers"u8)
            {
                ContentMapperTests.Run(mapperRepository).GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--mapping-lines"u8)
            {
                MappingTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--program-lines"u8)
            {
                ProgramGraphTests.Lines().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--binding-lines"u8)
            {
                BindingTests.Lines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--resolution-lines"u8)
            {
                ResolutionTests.Lines();
                return 0;
            }
            if (args is [_, var syntaxRepository] && args[0] == "--javascript-syntax"u8)
            {
                JavaScriptSyntaxTests.Run(syntaxRepository);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--parser-single-worker"u8)
            {
                ParserSafetyTests.RunSingleWorkerDocumentation();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--modules"u8)
            {
                ModuleTests.Run();
                return 0;
            }
            if (args is [_, var hostRepository] && args[0] == "--hosts"u8)
            {
                HostTests.Run(hostRepository);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--host-lines"u8)
            {
                HostTests.RunLines();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--parser-safety"u8)
            {
                ParserSafetyTests.Run();
                return 0;
            }
            if (args is [_, var repository] && args[0] == "--foundations"u8)
            {
                FoundationTests.Run(repository);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--scan-lines"u8)
            {
                SyntaxTests.ScanLines();
                return 0;
            }
            if (args is [_, var profileInputs, var profileDirectory] && args[0] == "--profile-pipeline"u8)
            {
                ProfileExperiments.Run(profileInputs, profileDirectory);
                return 0;
            }
            if (args is [_, var fixturePath, var resultsPath] && (args[0] == "--pipeline"u8 || args[0] == "--pipeline-benchmark"u8))
            {
                PipelineExperiments.Run(fixturePath, resultsPath, args[0] == "--pipeline-benchmark"u8);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--startup"u8)
            {
                Console.WriteLine("TypeScript C# NativeAOT experiment; compiler not implemented");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--native-check"u8)
            {
                if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
                    throw new InvalidOperationException("Expected the published NativeAOT runtime");
                Console.WriteLine(
                    $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; Vector128={System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}; Vector256={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}; Vector512={System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated}");
                return 0;
            }
            if (args is [_, var seconds] && args[0] == "--profile-workload"u8)
            {
                Experiments.ProfileWorkload(int.Parse(seconds, CultureInfo.InvariantCulture));
                return 0;
            }
            if (!(args is [_, _, _] && (args[0] == "--verify"u8 || args[0] == "--benchmark"u8)))
                throw new ArgumentException(
                    "Usage: --verify|--benchmark <input.json> <Go-output.json>, --startup, --profile-workload <seconds>");
            using JsonDocument input = JsonDocument.Parse(File.ReadAllBytes(args[1].ToString()));
            using JsonDocument expected = JsonDocument.Parse(File.ReadAllBytes(args[2].ToString()));
            Verify(
                input.RootElement,
                expected.RootElement,
                Utf8String.FromString(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2].ToString()))!, "semantic-differences.json")));
            if (args[0] == "--benchmark"u8)
                Experiments.Benchmark(input.RootElement, expected.RootElement);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Verify(JsonElement input, JsonElement expected, Utf8String differencesPath)
    {
        int checks = 0;
        void Check(bool condition, Utf8String message)
        {
            checks++;
            if (!condition)
                throw new InvalidDataException(message.ToString());
        }
        var expectedTexts = expected.GetProperty("texts"u8);
        int textIndex = 0;
        foreach (JsonElement textElement in input.GetProperty("texts"u8).EnumerateArray())
        {
            byte[] bytes = textElement.GetBytesFromBase64();
            JsonElement wanted = expectedTexts[textIndex];
            Utf8String decoded = new(bytes);
            int scalarLines = 0;
            foreach (byte value in bytes)
                if (value == (byte)'\n')
                    scalarLines++;
            Check(bytes.AsSpan().Count((byte)'\n') == scalarLines, Utf8String.ConcatMany("BCL SIMD/scalar count "u8, Utf8String.Format(textIndex)));
            int[] units = wanted.GetProperty("units"u8).EnumerateArray().Select(value => value.GetInt32()).ToArray();
            var actualUnits = new List<int>();
            for (int at = 0; at < decoded.Length;)
            {
                int point = Wtf8.Decode(decoded.Span[at..], out int width);
                at += width;
                if (point > 0xFFFF)
                {
                    actualUnits.Add(0xD800 + (point - 0x10000 >> 10));
                    actualUnits.Add(0xDC00 + (point - 0x10000 & 0x3FF));
                }
                else
                    actualUnits.Add(point);
            }
            Check(actualUnits.SequenceEqual(units), Utf8String.ConcatMany("WTF-8 text "u8, Utf8String.Format(textIndex)));
            Check(
                Wtf8.CombineSurrogatePairs(bytes).AsSpan().SequenceEqual(wanted.GetProperty("combined"u8).GetBytesFromBase64()),
                Utf8String.ConcatMany("Concatenation "u8, Utf8String.Format(textIndex)));
            textIndex++;
        }
        int numberIndex = 0;
        int permittedPowerDifferences = 0;
        var differences = new List<(int Index, Utf8String Go, Utf8String Candidate, Utf8String Node)>();
        foreach (JsonElement pair in input.GetProperty("numbers"u8).EnumerateArray())
        {
            double x = BitConverter.UInt64BitsToDouble(
                ulong.Parse(JsonStrings.GetString(pair[0])!, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            double y = BitConverter.UInt64BitsToDouble(
                ulong.Parse(JsonStrings.GetString(pair[1])!, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            double[] results = NumberOperations.Evaluate(x, y);
            JsonElement wanted = expected.GetProperty("numbers"u8)[numberIndex];
            for (int i = 0; i < results.Length; i++)
            {
                Utf8String bits = double.IsNaN(results[i])
                    ? "nan"u8
                    : Utf8String.Format(BitConverter.DoubleToUInt64Bits(results[i]), "x16");
                bool equal = bits == TypeScript.Compiler.Configuration.JsonStrings.GetString(wanted[i]);
                if (!equal && i == 8)
                {
                    // Named policy: ECMAScript implementation-approximated power.
                    // Compare against the independent Node/V8 result. Keep zero,
                    // infinities, NaN, and sign exact; allow <=4 ULP only for
                    // finite nonzero same-sign powers. This is a regression guard,
                    // not a proof or a spec-mandated error bound.
                    Utf8String jsBits = TypeScript.Compiler.Configuration.JsonStrings.GetString(input.GetProperty("powers"u8)[numberIndex])!;
                    equal = bits == jsBits || NumberOperations.ApproximatePower(results[i], jsBits);
                    if (equal)
                    {
                        permittedPowerDifferences++;
                        differences.Add((numberIndex, JsonStrings.GetString(wanted[i])!, bits, jsBits));
                    }
                }
                Check(equal, Utf8String.ConcatMany("Numeric pair "u8, Utf8String.Format(numberIndex), ", operation "u8, Utf8String.Format(i), ": "u8, Utf8String.Format(x), ", "u8, Utf8String.Format(y), "; "u8, bits, " != "u8, JsonStrings.GetString(wanted[i])));
            }
            numberIndex++;
        }
        var types = new List<TypeAtom[]>();
        foreach (JsonElement expression in input.GetProperty("types"u8).EnumerateArray())
            types.Add(TypeRelations.Parse(JsonStrings.GetString(expression), types));
        int relationIndex = 0;
        foreach (TypeAtom[] source in types)
            foreach (TypeAtom[] target in types)
            {
                bool actual = TypeRelations.Assignable<StrictAssignment>(source, target);
                Check(
                    actual == expected.GetProperty("relations"u8)[relationIndex].GetBoolean(),
                    Utf8String.ConcatMany("Type relation "u8, Utf8String.Format(relationIndex / types.Count), " -> "u8, Utf8String.Format(relationIndex % types.Count)));
                Check(actual == TypeRelations.AssignableConcrete(source, target), Utf8String.ConcatMany("Generic/concrete relation "u8, Utf8String.Format(relationIndex)));
                relationIndex++;
            }
        int nodeCount = 0;
        int fileIndex = 0;
        foreach (JsonElement file in expected.GetProperty("files"u8).EnumerateArray())
        {
            byte[] wire = file.GetProperty("wire"u8).GetBytesFromBase64();
            var packet = new AstPacket(wire);
            Check(packet.Reencode().AsSpan().SequenceEqual(wire), Utf8String.ConcatMany("AST packet "u8, JsonStrings.GetString(file.GetProperty("name"u8))));
            Check(
                packet.HasContentHash(input.GetProperty("files"u8)[fileIndex++].GetProperty("text"u8).GetBytesFromBase64()),
                "BCL xxh3-128 content hash / byte order"u8);
            nodeCount += packet.NodeCount;
            var arena = new Arena<NodeRecord>(31);
            for (int i = 0; i < packet.NodeCount; i++)
            {
                Handle<NodeRecord> handle = arena.Add(packet.GetNode(i));
                Check(arena[handle] == packet.GetNode(i), Utf8String.ConcatMany("Arena node "u8, Utf8String.Format(i)));
            }
        }
        Check(Experiments.CheckOwnershipAndStack(), "Ownership, identity, and stack safety"u8);
        // Repeated decoding must remain stable across collections.
        for (int repetition = 0; repetition < 5; repetition++)
        {
            GC.Collect();
            foreach (JsonElement file in expected.GetProperty("files"u8).EnumerateArray())
            {
                byte[] bytes = file.GetProperty("wire"u8).GetBytesFromBase64();
                Check(new AstPacket(bytes).Reencode().AsSpan().SequenceEqual(bytes), Utf8String.ConcatMany("Packet reload "u8, Utf8String.Format(repetition)));
            }
        }
        Check(Experiments.CheckNativeInterop(), "Native process interop"u8);
        Check(Experiments.CheckFileSystem(), "Filesystem byte and UTF-16 path roundtrip"u8);
        byte[] firstPacket = expected.GetProperty("files"u8)[0].GetProperty("wire"u8).GetBytesFromBase64();
        foreach (int length in new[] { 0, 1, 43, firstPacket.Length - 1 })
        {
            bool rejected = false;
            try
            {
                _ = new AstPacket(firstPacket.AsSpan(0, length));
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Check(rejected, Utf8String.ConcatMany("Truncated packet accepted at "u8, Utf8String.Format(length)));
        }
        byte[] damaged = (byte[])firstPacket.Clone();
        damaged.AsSpan(24, 4).Fill(255);
        bool invalidOffsetRejected = false;
        try
        {
            _ = new AstPacket(damaged);
        }
        catch (InvalidDataException)
        {
            invalidOffsetRejected = true;
        }
        Check(invalidOffsetRejected, "Invalid section accepted"u8);
        using (var stream = File.Create(differencesPath.ToString()))
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("policy"u8, "ecmascript-power-approximation"u8);
            writer.WriteStartArray("differences"u8);
            foreach (var difference in differences)
            {
                writer.WriteStartObject();
                writer.WriteNumber("pairIndex"u8, difference.Index);
                writer.WriteString("goBits"u8, difference.Go);
                writer.WriteString("candidateBits"u8, difference.Candidate);
                writer.WriteString("nodeBits"u8, difference.Node);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        Console.WriteLine(
            $"Verified {checks} assertions: {textIndex} texts, {numberIndex} number pairs, {relationIndex} type relations, {nodeCount} AST records; {permittedPowerDifferences} permitted power rounding differences.");
    }
}
