using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Emission;

internal static class ScriptTransformer
{
    internal static async ValueTask<SourceFileNode> TransformAsync(SourceFileNode source, EmitContext context,
        CompilerProgram program, Checker checker, CancellationToken cancellation)
    {
        var options = program.Configuration.Options;
        int target = options.EmitTargetYear;
        async ValueTask Apply(SyntaxRewriter transformer) => source = (SourceFileNode)(await transformer.VisitAsync(source))!;
        if (options.EmitDecoratorMetadata == true) await Apply(new MetadataTransformer(context, options, checker, cancellation));
        await Apply(new TypeEraser(context, options, cancellation));
        if (options.VerbatimModuleSyntax != true && source.ScriptKind is not (ScriptKind.JS or ScriptKind.JSX))
            await Apply(new ImportElision(context, options, checker, cancellation));
        await Apply(new RuntimeSyntaxTransformer(context, options, checker, cancellation));
        if (options.ExperimentalDecorators == true) await Apply(new LegacyDecoratorsTransformer(context, options, checker, cancellation));
        if (options.Jsx is JsxEmit.React or JsxEmit.ReactJSX or JsxEmit.ReactJSXDev && source.ScriptKind is ScriptKind.TSX or ScriptKind.JSX)
            await Apply(new JsxTransformer(context, options, checker, cancellation));
        if (target != int.MaxValue) await Apply(new UsingTransformer(context, cancellation));
        await Apply(new EsDecoratorsTransformer(context, options, cancellation));
        await Apply(new ClassFieldsTransformer(context, options, checker, cancellation));
        if (target < 2021) await Apply(new ExpressionLowering(context, ExpressionTransform.LogicalAssignment, cancellation));
        if (target < 2020)
        {
            await Apply(new ExpressionLowering(context, ExpressionTransform.NullishCoalescing, cancellation));
            await Apply(new OptionalChainTransformer(context, cancellation));
        }
        if (target < 2019) await Apply(new ExpressionLowering(context, ExpressionTransform.OptionalCatch, cancellation));
        if (target < 2018)
        {
            await Apply(new ObjectRestSpreadTransformer(context, cancellation));
            await Apply(new ForAwaitTransformer(context, cancellation));
            await Apply(new TaggedTemplateTransformer(context, cancellation));
        }
        if (target < 2017) await Apply(new AsyncTransformer(context, cancellation));
        if (target < 2016) await Apply(new ExpressionLowering(context, ExpressionTransform.Exponentiation, cancellation));
        await Apply(new UseStrictTransformer(context, options, program.EmitModuleFormat, cancellation));
        await Apply(new ModuleTransformer(context, options, checker, program.EmitModuleFormat, cancellation));
        if (options.IsolatedModules != true && options.VerbatimModuleSyntax != true)
            await Apply(new ConstEnumInliner(context, options, checker, cancellation));
        foreach (var helper in context.ReadHelpers()) context.AddHelper(source, helper);
        return source;
    }
}
