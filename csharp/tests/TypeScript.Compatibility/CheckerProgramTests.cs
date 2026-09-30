using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Numerics;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerProgramTests
{
    internal static async Task<int> DeclarationBlockSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Declaration block assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("allowUnreachableCode"u8, "false"u8);
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode(
                "interface Object{}interface Function{}abstract class A{a=1}class B{b=1}declare const C:typeof A|typeof B;new C();class Static{public static{}}if(true)type T=string;if(true)interface I{}type Accessor={get value(){return 0}};"),
            ["/project/unreachable.ts"u8] = Wtf8.Encode("function f(){return;const first=1;const second=2;}"),
            ["/project/recovery.ts"u8] = Wtf8.Encode("const object={'missing'};")
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        var checker = await program.CreateCheckerAsync();
        var main = program.GetFile("/project/main.ts"u8)!.Syntax;
        var snapshot = main.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        await checker.CheckProgramAsync();
        var diagnostics = checker.DetailedDiagnosticsForProgramFile(main);
        Check(diagnostics.Count(d => d.Code == DiagnosticCode.CannotCreateAnInstanceOfAnAbstractClass) == 1);
        Check(diagnostics.Count(d => d.Code == DiagnosticCode.ModifiersCannotAppearHere) == 1);
        Check(
            diagnostics.Where(
                d => d.Code == DiagnosticCode.X0DeclarationsCanOnlyBeDeclaredInsideABlock).SelectMany(d => d.Arguments).Order().SequenceEqual(
                    [
                        "interface"u8,
                        "type"u8
                    ]));
        var accessor = diagnostics.Single(d => d.Code == DiagnosticCode.AnImplementationCannotBeDeclaredInAmbientContexts);
        Check(main.Source.Text[accessor.Start..(accessor.Start + accessor.Length)] == "{return 0}"u8);
        var unreachable = program.GetFile("/project/unreachable.ts"u8)!.Syntax;
        var range = checker.DetailedDiagnosticsForProgramFile(unreachable).Single(d => d.Code == DiagnosticCode.UnreachableCodeDetected);
        Check(unreachable.Source.Text[range.Start..(range.Start + range.Length)] == "const first=1;const second=2;"u8);
        var recovery = program.GetFile("/project/recovery.ts"u8)!.Syntax;
        Check(
            recovery.ParseDiagnostics.Count != 0
                && checker.DetailedDiagnosticsForProgramFile(recovery).All(
                    d => d.Code != DiagnosticCode.NoValueExistsInScopeForTheShorthandProperty0EitherDeclareOneOrProvideAnInitializer));
        await checker.CheckProgramAsync();
        Check(checker.DetailedDiagnosticsForProgramFile(main).SequenceEqual(diagnostics, DiagnosticEqualityComparer.Instance));
        Check(snapshot.All(n => n.Node.Parent == n.Parent && n.Node.Pos == n.Pos && n.Node.End == n.End && n.Node.Flags == n.Flags));
        return checks;
    }

    internal static async Task<int> ModuleContextSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Module context assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("module"u8, "\"nodenext\""u8);
        options.SetRaw("moduleDetection"u8, "\"legacy\""u8);
        options.SetRaw("rewriteRelativeImportExtensions"u8, "true"u8);
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode("export {};{import Missing=require('not-found');export=Missing;}"),
            ["/project/rewrite.ts"u8] = Wtf8.Encode("import {value} from './folder.ts';value;"),
            ["/project/folder.ts/index.ts"u8] = Wtf8.Encode("export const value=1;"),
            ["/project/paths.ts"u8] = Wtf8.Encode(
                "declare module './relative'{}declare module '.\\\\relative'{}declare module 'q:/absolute'{}"),
            ["/project/augmentation.ts"u8] = Wtf8.Encode(
                "export {};namespace N{export interface I{}}declare module './target'{import I=N.I;interface Item{value:I}}"),
            ["/project/target.ts"u8] = Wtf8.Encode("export interface Item{}"),
            ["/project/indexes.ts"u8] = Wtf8.Encode("interface A{[key:string|symbol]:number;[key:string|symbol]:number}"),
            ["/project/recovery.ts"u8] = Wtf8.Encode("module {unknown;}")
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        var checker = await program.CreateCheckerAsync();
        var snapshot = program.SourceFiles.SelectMany(f => f.Syntax.DescendantsAndSelf())
            .Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        await checker.CheckProgramAsync();
        IReadOnlyList<Diagnostic> Diagnostics(Utf8String name) =>
            checker.DetailedDiagnosticsForProgramFile(program.GetFile(Utf8String.Copy("/project/"u8) + name)!.Syntax);
        var main = Diagnostics("main.ts"u8);
        Check(main.Any(d => d.Code == DiagnosticCode.AnExportAssignmentMustBeAtTheTopLevelOfAFileOrModuleDeclaration && d.Length == 6));
        Check(main.Any(d => d.Code == DiagnosticCode.AnImportDeclarationCanOnlyBeUsedAtTheTopLevelOfANamespaceOrModule && d.Length == 6));
        Check(
            main.Any(
                d => d.Code == DiagnosticCode.CannotFindModule0OrItsCorrespondingTypeDeclarations
                    && d.Arguments.SequenceEqual([Utf8String.Copy("not-found"u8)])));
        Check(Diagnostics("paths.ts"u8).Count(d => d.Code == DiagnosticCode.AmbientModuleDeclarationCannotSpecifyRelativeModuleName) == 3);
        Check(
            Diagnostics("augmentation.ts"u8).All(
                d => d.Code != DiagnosticCode.ImportsAreNotPermittedInModuleAugmentationsConsiderMovingThemToTheEnclosingExternalModule));
        Check(
            Diagnostics("rewrite.ts"u8).Single(
                d => d.Code == DiagnosticCode.ThisRelativeImportPathIsUnsafeToRewriteBecauseItLooksLikeAFileNameButActuallyResolvesTo0).Arguments.SequenceEqual([Utf8String.Copy("./folder.ts/index.ts"u8)]));
        var duplicate = Diagnostics("indexes.ts"u8).Where(d => d.Code == DiagnosticCode.DuplicateIndexSignatureForType0).ToArray();
        Check(duplicate.Length == 4);
        Check(duplicate.Count(d => d.Arguments.SequenceEqual([Utf8String.Copy("string"u8)])) == 2
            && duplicate.Count(d => d.Arguments.SequenceEqual([Utf8String.Copy("symbol"u8)])) == 2);
        Check(
            Diagnostics("recovery.ts"u8).Any(
                d => d.Code == DiagnosticCode.CannotFindName0DoYouNeedToInstallTypeDefinitionsForNodeTryNpmISaveDevTypesSlashnodeAndThenAddNodeToTheTypesFieldInYourTsconfig)
                && Diagnostics("recovery.ts"u8).All(
                    d => d.Code != DiagnosticCode.ANamespaceDeclarationShouldNotBeDeclaredUsingTheModuleKeywordPleaseUseTheNamespaceKeywordInstead));
        await checker.CheckProgramAsync();
        Check(Diagnostics("main.ts"u8).SequenceEqual(main, DiagnosticEqualityComparer.Instance));
        Check(snapshot.All(n => n.Node.Parent == n.Parent && n.Node.Pos == n.Pos && n.Node.End == n.End && n.Node.Flags == n.Flags));
        return checks;
    }

    internal static async Task<int> RelationContextSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Relation context assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/project/globals.d.ts"u8] = Wtf8.Encode(
                "interface Object{}interface Function{readonly length:number}interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}"),
            ["/project/main.ts"u8] = Wtf8.Encode(
                "interface Shape{value:number}let shape:Shape={vaule:1};const part={value:2};const dupe={value:1,...part};const result:()=>number=()=>'';class Base{get value(){return 1}}class Derived extends Base{value=1}interface Merge<T>{}interface Merge<T,U>{}function circ<T extends T>(){}abstract class Mod{static private x:number;abstract static m():void;}"),
            ["/project/recovery.ts"u8] = Wtf8.Encode("enum E{#x}")
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var before = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var checker = await program.CreateCheckerAsync();
        await checker.CheckProgramAsync();
        var diagnostics = checker.DetailedDiagnosticsForProgramFile(source);
        Check(
            diagnostics.Single(
                d => d.Code == DiagnosticCode.ObjectLiteralMayOnlySpecifyKnownPropertiesBut0DoesNotExistInType1DidYouMeanToWrite2).Arguments.SequenceEqual(
                    [
                        Utf8String.Copy("vaule"u8),
                        Utf8String.Copy("Shape"u8),
                        Utf8String.Copy("value"u8)
                    ]));
        var spread = diagnostics.Single(d => d.Code == DiagnosticCode.X0IsSpecifiedMoreThanOnceSoThisUsageWillBeOverwritten);
        Check(spread.Arguments.SequenceEqual([Utf8String.Copy("value"u8)]));
        Check(spread.RelatedInformation is [var note] && note.Code == DiagnosticCode.ThisSpreadAlwaysOverwritesThisProperty);
        var arrow = diagnostics.Single(d => d.Code == DiagnosticCode.Type0IsNotAssignableToType1);
        Check(arrow.Arguments.SequenceEqual([Utf8String.Copy("string"u8), Utf8String.Copy("number"u8)]));
        Check(
            arrow.RelatedInformation is [var returnNote]
                && returnNote.Code == DiagnosticCode.TheExpectedTypeComesFromTheReturnTypeOfThisSignature);
        Check(
            diagnostics.Single(
                d => d.Code == DiagnosticCode.X0IsDefinedAsAnAccessorInClass1ButIsOverriddenHereIn2AsAnInstanceProperty).Arguments.SequenceEqual(
                    [
                        Utf8String.Copy("value"u8),
                        Utf8String.Copy("Base"u8),
                        Utf8String.Copy("Derived"u8)
                    ]));
        Check(diagnostics.Count(d => d.Code == DiagnosticCode.AllDeclarationsOf0MustHaveIdenticalTypeParameters) == 2);
        Check(
            diagnostics.Where(
                d => d.Code == DiagnosticCode.AllDeclarationsOf0MustHaveIdenticalTypeParameters).All(d => d.Arguments.SequenceEqual([Utf8String.Copy("Merge"u8)])));
        Check(diagnostics.Single(d => d.Code == DiagnosticCode.TypeParameter0HasACircularConstraint).Arguments.SequenceEqual([Utf8String.Copy("T"u8)]));
        Check(
            diagnostics.Single(
                d => d.Code == DiagnosticCode.X0ModifierMustPrecede1Modifier).Arguments.SequenceEqual([Utf8String.Copy("private"u8), Utf8String.Copy("static"u8)]));
        Check(
            diagnostics.Single(
                d => d.Code == DiagnosticCode.X0ModifierCannotBeUsedWith1Modifier).Arguments.SequenceEqual([Utf8String.Copy("static"u8), Utf8String.Copy("abstract"u8)]));
        var recovery = program.GetFile("/project/recovery.ts"u8)!.Syntax;
        Check(
            checker.DetailedDiagnosticsForProgramFile(recovery).Any(
                d => d.Code == DiagnosticCode.AnEnumMemberCannotBeNamedWithAPrivateIdentifier));
        await checker.CheckProgramAsync();
        Check(checker.DetailedDiagnosticsForProgramFile(source).SequenceEqual(diagnostics, DiagnosticEqualityComparer.Instance));
        Check(before.All(n => n.Node.Parent == n.Parent && n.Node.Pos == n.Pos && n.Node.End == n.End && n.Node.Flags == n.Flags));
        return checks;
    }

    internal static async Task<int> DeclarationGrammarSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Declaration grammar assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("allowJs"u8, "true"u8);
        options.SetRaw("checkJs"u8, "true"u8);
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/project/globals.d.ts"u8] = Wtf8.Encode(
                "interface Object{}interface Function{readonly length:number}interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}"),
            ["/project/main.ts"u8] = Wtf8.Encode(
                "interface A extends MissingA extends MissingB{}class C implements MissingI implements MissingJ{}type Private={#value:number;#method():void};declare function* gen():void;declare const tuple:[number,...string[]];declare function takes(first:number,...rest:string[]):void;takes(...tuple);const object={get value(){return 1}};object.value();function overload(x:string):void;function overload(x:number){}"),
            ["/project/types.ts"u8] = Wtf8.Encode("export interface Data{}"),
            ["/project/import.js"u8] = Wtf8.Encode("import {Data} from './types';export {Data};"),
            ["/project/recovery.ts"u8] = Wtf8.Encode(".missing;catch(error){error;}finally{}")
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var before = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var checker = await program.CreateCheckerAsync();
        await checker.CheckProgramAsync();
        var diagnostics = checker.DetailedDiagnosticsForProgramFile(source);
        Check(diagnostics.Where(d => d.Code == DiagnosticCode.CannotFindName0).SelectMany(d => d.Arguments).Order()
            .SequenceEqual(new Utf8String[] { "MissingA"u8, "MissingI"u8 }));
        Check(diagnostics.Count(d => d.Code == DiagnosticCode.PrivateIdentifiersAreNotAllowedOutsideClassBodies) == 2);
        Check(diagnostics.Any(d => d.Code == DiagnosticCode.GeneratorsAreNotAllowedInAnAmbientContext));
        Check(diagnostics.All(d => d.Code != DiagnosticCode.ArgumentOfType0IsNotAssignableToParameterOfType1));
        Check(
            diagnostics.Single(
                d => d.Code == DiagnosticCode.ThisExpressionIsNotCallableBecauseItIsAGetAccessorDidYouMeanToUseItWithout).MessageChain is [var child]
                && child.Code == DiagnosticCode.Type0HasNoCallSignatures);
        Check(
            diagnostics.Single(
                d => d.Code == DiagnosticCode.ThisOverloadSignatureIsNotCompatibleWithItsImplementationSignature).RelatedInformation is [var implementation]
                && implementation.Code == DiagnosticCode.TheImplementationSignatureIsDeclaredHere);
        var js = checker.DetailedDiagnosticsForProgramFile(program.GetFile("/project/import.js"u8)!.Syntax);
        Check(
            js.Count(d => d.Code == DiagnosticCode.X0IsATypeAndCannotBeImportedInJavaScriptFilesUse1InAJSDocTypeAnnotation) == 1
                && js.Count(d => d.Code == DiagnosticCode.TypesCannotAppearInExportDeclarationsInJavaScriptFiles) == 1);
        Check(
            js.Single(
                d => d.Code == DiagnosticCode.X0IsATypeAndCannotBeImportedInJavaScriptFilesUse1InAJSDocTypeAnnotation).Arguments.SequenceEqual(
                    [
                        Utf8String.Copy("Data"u8),
                        Utf8String.Copy("import(\"./types\").Data"u8)
                    ]));
        var recovery = program.GetFile("/project/recovery.ts"u8)!.Syntax;
        Check(recovery.ParseDiagnostics.Count != 0);
        Check(recovery.Statements!.OfType<TryStatementNode>().Single().CatchClause is not null);
        Check(checker.DetailedDiagnosticsForProgramFile(recovery).Count(d => d.Code == DiagnosticCode.CannotFindName0) == 1);
        await checker.CheckProgramAsync();
        Check(checker.DetailedDiagnosticsForProgramFile(source).SequenceEqual(diagnostics, DiagnosticEqualityComparer.Instance));
        Check(before.All(n => n.Node.Parent == n.Parent && n.Node.Pos == n.Pos && n.Node.End == n.End && n.Node.Flags == n.Flags));
        return checks;
    }

    internal static async Task<int> DiagnosticValueSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Diagnostic value assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        options.SetRaw("noUnusedLocals"u8, "true"u8);
        options.SetRaw("noUnusedParameters"u8, "true"u8);
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/project/globals.d.ts"u8] = Wtf8.Encode(
                "interface Object{}interface Function{}interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}"),
            ["/project/main.ts"u8] = Wtf8.Encode(
                "export class Base{p=1}export class Derived extends Base{p=''}export class Needs{value:number}let x:number;x;var duplicate:number;var duplicate:string;type Box<T>={value:T};let missing:Box;class Generic<T>{}let generic:Generic;let maybe:{p:number}|undefined;maybe.p;null.x;let implicit:{p};export function outer(unused:number){const local=1;return 2;}type Recursive=Recursive;")
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        Check(source.ParseDiagnostics.Count == 0);
        var nodes = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var checker = await program.CreateCheckerAsync();
        await checker.CheckProgramAsync();
        var diagnostics = checker.DetailedDiagnosticsForProgramFile(source);
        Check(
            diagnostics.Single(
                d => d.Code == DiagnosticCode.Property0HasNoInitializerAndIsNotDefinitelyAssignedInTheConstructor).Arguments.SequenceEqual([Utf8String.Copy("value"u8)]));
        Check(diagnostics.Single(d => d.Code == DiagnosticCode.Variable0IsUsedBeforeBeingAssigned).Arguments.SequenceEqual([Utf8String.Copy("x"u8)]));
        Check(diagnostics.Single(d => d.Code == DiagnosticCode.X0IsPossiblyUndefined).Arguments.SequenceEqual([Utf8String.Copy("maybe"u8)]));
        Check(diagnostics.Single(d => d.Code == DiagnosticCode.TheValue0CannotBeUsedHere).Arguments.SequenceEqual([Utf8String.Copy("null"u8)]));
        Check(diagnostics.Single(d => d.Code == DiagnosticCode.Member0ImplicitlyHasAn1Type).Arguments.SequenceEqual([Utf8String.Copy("p"u8), Utf8String.Copy("any"u8)]));
        Check(
            diagnostics.Where(
                d => d.Code == DiagnosticCode.GenericType0Requires1TypeArgumentS).Select(d => Utf8String.Join((byte)'|', d.Arguments)).Order()
            .SequenceEqual(new Utf8String[] { "Box|1|1"u8, "Generic<T>|1|1"u8 }));
        Check(
            diagnostics.Single(d => d.Code == DiagnosticCode.TypeAlias0CircularlyReferencesItself).Arguments.SequenceEqual([Utf8String.Copy("Recursive"u8)]));
        var conflict = diagnostics.Single(
            d => d.Code == DiagnosticCode.SubsequentVariableDeclarationsMustHaveTheSameTypeVariable0MustBeOfType1ButHereHasType2);
        Check(conflict.Arguments.SequenceEqual([Utf8String.Copy("duplicate"u8), Utf8String.Copy("number"u8), Utf8String.Copy("string"u8)]));
        Check(
            conflict.RelatedInformation is [var previous]
                && previous.Code == DiagnosticCode.X0WasAlsoDeclaredHere
                && previous.Arguments.SequenceEqual([Utf8String.Copy("duplicate"u8)]));
        var inherited = diagnostics.Single(d => d.Code == DiagnosticCode.Property0InType1IsNotAssignableToTheSamePropertyInBaseType2);
        Check(inherited.Arguments.SequenceEqual([Utf8String.Copy("p"u8), Utf8String.Copy("Derived"u8), Utf8String.Copy("Base"u8)]));
        Check(
            inherited.MessageChain is [var reason]
                && reason.Code == DiagnosticCode.Type0IsNotAssignableToType1
                && reason.Arguments.SequenceEqual([Utf8String.Copy("string"u8), Utf8String.Copy("number"u8)]));
        Check(diagnostics.Any(d => d.Code == DiagnosticCode.X0IsDeclaredButItsValueIsNeverRead && d.Arguments.SequenceEqual([Utf8String.Copy("unused"u8)])));
        Check(diagnostics.Any(d => d.Code == DiagnosticCode.X0IsDeclaredButItsValueIsNeverRead && d.Arguments.SequenceEqual([Utf8String.Copy("local"u8)])));
        await checker.CheckProgramAsync();
        Check(checker.DetailedDiagnosticsForProgramFile(source).SequenceEqual(diagnostics, DiagnosticEqualityComparer.Instance));
        Check(nodes.All(n => n.Node.Parent == n.Parent && n.Node.Pos == n.Pos && n.Node.End == n.End && n.Node.Flags == n.Flags));
        return checks;
    }

    internal static async Task<int> PrivateDeclarationSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Private/declaration assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        options.SetRaw("allowJs"u8, "true"u8);
        options.SetRaw("checkJs"u8, "true"u8);
        options.SetRaw("module"u8, "\"commonjs\""u8);
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/project/globals.d.ts"u8] = Wtf8.Encode(
                "interface Object{}interface Function{}interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}"),
            ["/project/main.ts"u8] = Wtf8.Encode(
                "class C{static #x=1;test(c:C){return c.#x;}}type Bad=intrinsic;type Nested={p:this};let restricted:{private p:number};const obj={is():this is C{return true;}};class Overload{'a'():void;'b'() {}};declare const key:symbol;class Dynamic{[key]():void;};"),
            ["/project/ambient.d.ts"u8] = Wtf8.Encode("namespace A{declare namespace B{}}var first:number;var second:number;"),
            ["/project/module.js"u8] = Wtf8.Encode("function run(){}function hidden(){}module.exports=run;module.exports.hidden=hidden;"),
            ["/project/use.js"u8] = Wtf8.Encode("const {hidden}=require('./module');hidden();"),
            ["/project/exports.ts"u8] = Wtf8.Encode("const local=1;export {local as renamed};"),
            ["/project/import.ts"u8] = Wtf8.Encode("import {local} from './exports';")
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        Check(source.ParseDiagnostics.Count == 0);
        var before = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var checker = await program.CreateCheckerAsync();
        await checker.CheckProgramAsync();
        var diagnostics = checker.DetailedDiagnosticsForProgramFile(source);
        foreach (DiagnosticCode code in new[]
        {
            DiagnosticCode.Property0DoesNotExistOnType1,
            DiagnosticCode.TheIntrinsicKeywordCanOnlyBeUsedToDeclareCompilerProvidedIntrinsicTypes,
            DiagnosticCode.AThisTypeIsAvailableOnlyInANonStaticMemberOfAClassOrInterface,
            DiagnosticCode.X0ModifierCannotAppearOnATypeMember,
            DiagnosticCode.FunctionImplementationNameMustBe0
        })
            Check(diagnostics.Count(d => d.Code == code) == 1);
        Check(diagnostics.All(d => d.Code != DiagnosticCode.FunctionImplementationIsMissingOrNotImmediatelyFollowingTheDeclaration));
        Check(diagnostics.Single(d => d.Code == DiagnosticCode.FunctionImplementationNameMustBe0).Arguments.SequenceEqual([Utf8String.Copy("'a'"u8)]));
        var ambient = checker.DetailedDiagnosticsForProgramFile(program.GetFile("/project/ambient.d.ts"u8)!.Syntax);
        Check(ambient.Count(d => d.Code == DiagnosticCode.TopLevelDeclarationsInDTsFilesMustStartWithEitherADeclareOrExportModifier) == 1);
        Check(ambient.Count(d => d.Code == DiagnosticCode.ADeclareModifierCannotBeUsedInAnAlreadyAmbientContext) == 1);
        var aliases = checker.DetailedDiagnosticsForProgramFile(program.GetFile("/project/use.js"u8)!.Syntax);
        Check(aliases.Count == 1 && aliases[0].Code == DiagnosticCode.Module0HasNoExportedMember1);
        Check(aliases[0].Arguments.SequenceEqual([Utf8String.Copy("\"./module\""u8), Utf8String.Copy("hidden"u8)]));
        var renamed = checker.DetailedDiagnosticsForProgramFile(program.GetFile("/project/import.ts"u8)!.Syntax).Single(
            d => d.Code == DiagnosticCode.Module0Declares1LocallyButItIsExportedAs2);
        Check(renamed.Arguments.SequenceEqual([Utf8String.Copy("\"./exports\""u8), Utf8String.Copy("local"u8), Utf8String.Copy("renamed"u8)]));
        Check(renamed.RelatedInformation.Count == 1 && renamed.RelatedInformation[0].Code == DiagnosticCode.X0IsDeclaredHere);
        await checker.CheckProgramAsync();
        Check(checker.DetailedDiagnosticsForProgramFile(source).Count == diagnostics.Count);
        Check(before.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        return checks;
    }

    internal static async Task<int> MappedExportSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Mapped/export assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        options.SetRaw("module"u8, "\"commonjs\""u8);
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/project/globals.d.ts"u8] = Wtf8.Encode(
                "declare var globalValue:number;interface Object{constructor:Function}interface Function{readonly length:number}interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}"),
            ["/project/main.ts"u8] = Wtf8.Encode(
                "export {globalValue};export const __esModule=1;type Dup<T,T>=T;interface Defaults<A=string,B>{}type Late<A=B,B=string>=A;type Bad<T>={[P in T]:number};type Rename<T>={[P in keyof T as {}]:T[P]};type Wrong=infer X;type Template<T>=`${T}`;type Extra<T>={[P in keyof T]:T[P];extra():void};class C{private constructor(){}}const blocked=new C();let fn:()=>void=blocked.constructor;interface Init{p:number=1}type InitType={p:number=1};class Param{constructor(public constructor:string){}}")
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        Check(source.ParseDiagnostics.Count == 0);
        var before = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var checker = await program.CreateCheckerAsync();
        await checker.CheckProgramAsync();
        var diagnostics = checker.DetailedDiagnosticsForProgramFile(source);
        foreach (DiagnosticCode code in new[]
        {
            DiagnosticCode.CannotExport0OnlyLocalDeclarationsCanBeExportedFromAModule,
            DiagnosticCode.IdentifierExpectedEsModuleIsReservedAsAnExportedMarkerWhenTransformingECMAScriptModules,
            DiagnosticCode.DuplicateIdentifier0,
            DiagnosticCode.RequiredTypeParametersMayNotFollowOptionalTypeParameters,
            DiagnosticCode.TypeParameterDefaultsCanOnlyReferencePreviouslyDeclaredTypeParameters,
            DiagnosticCode.XInferDeclarationsAreOnlyPermittedInTheExtendsClauseOfAConditionalType,
            DiagnosticCode.AMappedTypeMayNotDeclarePropertiesOrMethods,
            DiagnosticCode.ConstructorOfClass0IsPrivateAndOnlyAccessibleWithinTheClassDeclaration,
            DiagnosticCode.AnInterfacePropertyCannotHaveAnInitializer,
            DiagnosticCode.ATypeLiteralPropertyCannotHaveAnInitializer,
            DiagnosticCode.XConstructorCannotBeUsedAsAParameterPropertyName
        })
            Check(diagnostics.Any(d => d.Code == code));
        Check(diagnostics.All(d => d.Code != DiagnosticCode.FunctionImplementationIsMissingOrNotImmediatelyFollowingTheDeclaration));
        var blocked = source.DescendantsAndSelf().OfType<VariableDeclarationNode>().Single(n => (n.Name is IdentifierNode { Text: { Span: var matchedText } } && matchedText.SequenceEqual("blocked"u8)));
        Check((await checker.GetTypeAtLocationAsync(blocked.Name!)).Symbol?.Name == "C"u8);
        Check(diagnostics.Count(d => d.Code == DiagnosticCode.Type0IsNotAssignableToType1) >= 3);
        await checker.CheckProgramAsync();
        Check(checker.DetailedDiagnosticsForProgramFile(source).Count == diagnostics.Count);
        Check(before.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        return checks;
    }

    internal static async Task<int> ProgramRelationsSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Program relation assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        options.SetRaw("jsx"u8, "\"preserve\""u8);
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/project/a.ts"u8] = Wtf8.Encode("/// <reference path='./missing.ts' />\nexport const a=1;"),
            ["/project/b.ts"u8] = Wtf8.Encode("/// <reference path='./missing.ts' />\nexport const b=1;"),
            ["/project/ignore.ts"u8] = Wtf8.Encode("// @ts-ignore\n/// <reference path='./ignored.ts' />\nexport {};"),
            ["/project/expect.ts"u8] = Wtf8.Encode("// @ts-expect-error\n/// <reference path='./expected.ts' />\nexport {};"),
            ["/project/self.ts"u8] = Wtf8.Encode("/// <reference path='./self.ts' />\nexport {};"),
            ["/project/slices.ts"u8] = Wtf8.Encode("""
                type Prefix = `\uD83D${string}`;
                type Suffix = `${string}\uDE00`;
                const badPrefix: Prefix = "\uD83D\uDE00";
                const badSuffix: Suffix = "\uD83D\uDE00";
                const lonePrefix: Prefix = "\uD83D";
                const loneSuffix: Suffix = "\uDE00";
                type Middle<T> = T extends `${infer A}\uD83D${infer B}` ? false : true;
                const middle: Middle<"\uD83D\uDE00"> = true;
                declare const empty: "";
                const emptyAnd = empty && 1;
                """),
            ["/project/weak.ts"u8] = Wtf8.Encode(
                "interface Options{timeout?:number}const unrelated={other:1};const value:Options=unrelated;const fn:Options=()=>({timeout:1});"),
            ["/project/main.tsx"u8] = Wtf8.Encode(
                "namespace JSX{export interface Element{}export interface IntrinsicElements{div:{}}export interface ElementChildrenAttribute{children:{}}}declare function View(p:{children:(value:number)=>number}):JSX.Element;const element=<View>{value=>value+1}</View>;declare class Hidden{private method(value);}")
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.tsx"u8)!.Syntax;
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        await checker.CheckProgramAsync();
        IReadOnlyList<Diagnostic> Errors(Utf8String name) =>
            checker.DetailedDiagnosticsForProgramFile(program.GetFile(Utf8String.Copy("/project/"u8) + name)!.Syntax);
        Check(
            Errors("a.ts"u8).Count(d => d.Code == DiagnosticCode.File0NotFound) == 1
                && Errors("b.ts"u8).Count(d => d.Code == DiagnosticCode.File0NotFound) == 1);
        Check(Errors("a.ts"u8).Single(d => d.Code == DiagnosticCode.File0NotFound).Arguments.SequenceEqual([Utf8String.Copy("./missing.ts"u8)]));
        Check(
            program.IncludeDiagnostics.Count(
                d => d.Code == DiagnosticCode.File0NotFound && d.Arguments.SequenceEqual([Utf8String.Copy("./missing.ts"u8)])) == 2);
        Check(Errors("ignore.ts"u8).Count == 0);
        Check(Errors("expect.ts"u8).Select(d => d.Code).SequenceEqual([DiagnosticCode.UnusedTsExpectErrorDirective]));
        Check(Errors("self.ts"u8).Select(d => d.Code).SequenceEqual([DiagnosticCode.AFileCannotHaveAReferenceToItself]));
        Check(Errors("slices.ts"u8).Count == 2
            && Errors("slices.ts"u8).All(d => d.Code == DiagnosticCode.Type0IsNotAssignableToType1));
        var emptyAnd = program.GetFile("/project/slices.ts"u8)!.Syntax.DescendantsAndSelf().OfType<VariableDeclarationNode>()
            .Single(node => (node.Name is IdentifierNode { Text.Span: var matchedText2 } && matchedText2.SequenceEqual("emptyAnd"u8)));
        Check(await checker.GetTypeAtLocationAsync(emptyAnd.Name!) is LiteralType { Value: Utf8String { IsEmpty: true } });
        Check(Errors("weak.ts"u8).Any(d => d.Code == DiagnosticCode.Type0HasNoPropertiesInCommonWithType1));
        Check(
            Errors("weak.ts"u8).Single(
                d => d.Code == DiagnosticCode.ValueOfType0HasNoPropertiesInCommonWithType1DidYouMeanToCallIt).RelatedInformation.Any(
                    d => d.Code == DiagnosticCode.DidYouMeanToCallThisExpression));
        Check(Errors("main.tsx"u8).All(d => d.Code != DiagnosticCode.Parameter0ImplicitlyHasAn1Type));
        var arrowParameter = source.DescendantsAndSelf().OfType<ArrowFunctionNode>().Single().Parameters![0];
        Check(await checker.GetTypeAtLocationAsync(((ParameterDeclarationNode)arrowParameter).Name!) == checker.Context.NumberType);
        Check(snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        await checker.CheckProgramAsync();
        Check(
            Errors("a.ts"u8).Count(d => d.Code == DiagnosticCode.File0NotFound) == 1
                && Errors("weak.ts"u8).Count(
                    d => d.Code == DiagnosticCode.ValueOfType0HasNoPropertiesInCommonWithType1DidYouMeanToCallIt) == 1);
        return checks;
    }

    internal static async Task<int> DiagnosticIdentitySafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Diagnostic identity assertion {checks + 1}");
            checks++;
        }
        var comparer = DiagnosticEqualityComparer.Instance;
        Diagnostic Make(Utf8String? argument = null) =>
            new(Messages.Duplicate_identifier_0, 1, 4, [argument ?? Utf8String.Copy("name"u8)]) { FileName = "/project/main.ts"u8 };
        var first = Make();
        var copy = Make();
        Check(comparer.Equals(first, copy) && comparer.GetHashCode(first) == comparer.GetHashCode(copy));
        Check(!comparer.Equals(first, Make("other"u8)));
        Check(!comparer.Equals(first, copy with { Start = 2 }));
        Check(!comparer.Equals(first, copy with { FileName = "/project/other.ts"u8 }));
        Check(comparer.Equals(first, copy with { Source = ""u8 }));
        Check(!comparer.Equals(first, copy with { Source = "other"u8 }));
        var related = first with { RelatedInformation = [Make("related"u8)] };
        Check(comparer.Equals(related, copy with { RelatedInformation = [Make("related"u8)] }));
        Check(!comparer.Equals(related, copy with { RelatedInformation = [Make("different"u8)] }));
        Check(!comparer.Equals(first with { RelatedInformation = [Make("one"u8), Make("two"u8)] },
            copy with { RelatedInformation = [Make("two"u8), Make("one"u8)] }));
        var chained = first with { MessageChain = [Make("child"u8)] };
        Check(comparer.Equals(chained, copy with { MessageChain = [Make("child"u8) with { Start = 8, FileName = null }] }));
        Check(!comparer.Equals(chained, copy with { MessageChain = [Make("different"u8)] }));
        Diagnostic deep = Make(), sameDeep = Make(), otherDeep = Make("different"u8);
        for (int i = 0; i < 20_000; i++)
        {
            deep = Make() with { MessageChain = [deep] };
            sameDeep = Make() with { MessageChain = [sameDeep] };
            otherDeep = Make() with { MessageChain = [otherDeep] };
        }
        Check(comparer.Equals(deep, sameDeep) && !comparer.Equals(deep, otherDeep));
        Check(new[] { first, copy, Make("other"u8), related }.Distinct(comparer).Count() == 3);
        Check(DiagnosticEqualityComparer.WithoutRelatedInformation.Equals(first, related));
        var early = Make("early"u8) with { Start = 2 };
        var late = Make("late"u8) with { Start = 9 };
        var unsorted = first with { RelatedInformation = [late, early] };
        var duplicates = DiagnosticCollection.SortAndDeduplicate([unsorted, copy with { RelatedInformation = [early] }]);
        Check(duplicates is [var combined] && combined.RelatedInformation.SequenceEqual([early, late], comparer));
        Check(unsorted.RelatedInformation.SequenceEqual([late, early], comparer));
        Check(ReferenceEquals(DiagnosticCollection.SortAndDeduplicate([unsorted])[0], unsorted));
        Check(DiagnosticCollection.SortAndDeduplicate([first, Make("other"u8), related]).Length == 2);
        Check(DiagnosticCollection.SortAndDeduplicate([deep, sameDeep]).Length == 1);
        Check(DiagnosticCollection.Compare(deep, sameDeep) == 0 && DiagnosticCollection.Compare(deep, otherDeep) > 0);
        Check(DiagnosticCollection.Compare(deep, first) < 0);
        Check(DiagnosticCollection.Compare(Make("\uE000"u8), Make("\U00010000"u8)) < 0);
        Check(DiagnosticCollection.Compare(Make(Utf8String.Copy([0xED, 0xA0, 0x80])), Make("\uFFFD"u8)) < 0);
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode(
                "class B{x!:number;y!:number}interface I extends B{[key:string]:string}class C{p:number;p:string}interface O{m(x:number):void;m?(x:string):void}"),
            ["/project/recursive.ts"u8] = Wtf8.Encode("const recursive = () => 42 satisfies typeof recursive;"),
            ["/project/recovery.ts"u8] = Wtf8.Encode("const f: () => { return 1; };")
        }),
            "/project"u8,
            new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8, "/project/recursive.ts"u8, "/project/recovery.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var recovery = program.GetFile("/project/recovery.ts"u8)!.Syntax;
        Check(
            recovery.ParseDiagnostics.Count != 0
                && recovery.DescendantsAndSelf().OfType<TypeLiteralNode>().All(n => n.Members?.Count == 0));
        await checker.CheckProgramAsync();
        var diagnostics = checker.DetailedDiagnosticsForProgramFile(source);
        Check(diagnostics.Count(d => d.Code == DiagnosticCode.DuplicateIdentifier0) == 2);
        var properties = diagnostics.Where(d => d.Code == DiagnosticCode.Property0OfType1IsNotAssignableTo2IndexType3).ToArray();
        Check(properties.Length == 2 && properties.Select(d => d.Arguments[0]).Order().SequenceEqual([Utf8String.Copy("x"u8), Utf8String.Copy("y"u8)]));
        Check(properties[0].Start == properties[1].Start && properties[0].Length == properties[1].Length);
        Check(diagnostics.Count(d => d.Code == DiagnosticCode.OverloadSignaturesMustAllBeOptionalOrRequired) == 1);
        Check(checker.DetailedDiagnosticsForProgramFile(recovery).All(d => d.Code != DiagnosticCode.Member0ImplicitlyHasAn1Type));
        Check(checker.DetailedDiagnosticsForProgramFile(program.GetFile("/project/recursive.ts"u8)!.Syntax)
            .Where(
                d => d.Code == DiagnosticCode.Type0DoesNotSatisfyTheExpectedType1).Single().Arguments.SequenceEqual(
                    [
                        Utf8String.Copy("number"u8),
                        Utf8String.Copy("() => any"u8)
                    ]));
        await checker.CheckProgramAsync();
        Check(checker.DetailedDiagnosticsForProgramFile(source).Count == diagnostics.Count);
        var grouped = checker.GroupDiagnosticsByFile();
        foreach (var file in program.SourceFiles)
            Check(checker.DetailedDiagnosticsForProgramFile(file.Syntax, grouped[file.Syntax])
                .SequenceEqual(checker.DetailedDiagnosticsForProgramFile(file.Syntax), comparer));
        int previousCount = grouped[source].Count();
        checker.TrackDiagnostic(source, Make("late"u8));
        var afterAppend = checker.GroupDiagnosticsByFile();
        Check(afterAppend[source].Count() == previousCount + 1 && grouped[source].Count() == previousCount);
        checker.MissingAwaitHints.Add(source);
        var afterNote = checker.GroupDiagnosticsByFile();
        Check(afterNote[source].Single(d => d.Arguments.SequenceEqual([Utf8String.Copy("late"u8)])).RelatedInformation.Count == 1);
        Check(afterAppend[source].Single(d => d.Arguments.SequenceEqual([Utf8String.Copy("late"u8)])).RelatedInformation.Count == 0);
        checker.TrackDiagnostic(null, Make("global"u8) with { FileName = null });
        Check(checker.GroupDiagnosticsByFile()[null].Any(d => d.Arguments.SequenceEqual([Utf8String.Copy("global"u8)])));
        return checks;
    }

    internal static async Task<int> ModuleGrammarSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Module grammar assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("module"u8, "\"commonjs\""u8);
        options.SetRaw("importHelpers"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode(
                "import {'quoted' as first,'' as second} from './dep';export {default as chosen} from 'fs';const value=1;const object={value!};let let=1;function scoped(){var shadow=0;{let shadow=1;var shadow=2;}}type Bad=[x?:number,y:string];"),
            ["/project/dep.ts"u8] = Wtf8.Encode("const a=1,b=2;export {a as 'quoted',b as ''};"),
            ["/project/globals.d.ts"u8] = Wtf8.Encode("declare module 'fs';declare module 'tslib'{export {};}")
        }),
            "/project"u8,
            new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8, "/project/dep.ts"u8, "/project/globals.d.ts"u8], [], [], []));
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        Check(source.ParseDiagnostics.Count == 0);
        var shorthand = source.DescendantsAndSelf().OfType<ShorthandPropertyAssignmentNode>().Single();
        Check(shorthand.PostfixToken?.Kind == SyntaxKind.ExclamationToken && shorthand.Type is null);
        var before = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var checker = await program.CreateCheckerAsync();
        await checker.CheckProgramAsync();
        var diagnostics = checker.DetailedDiagnosticsForFile(source);
        Check(
            diagnostics.Count(
                d => d.Code == DiagnosticCode.ThisSyntaxRequiresAnImportedHelperNamed1WhichDoesNotExistIn0ConsiderUpgradingYourVersionOf0) == 1
            && diagnostics.Single(
                d => d.Code == DiagnosticCode.ThisSyntaxRequiresAnImportedHelperNamed1WhichDoesNotExistIn0ConsiderUpgradingYourVersionOf0).Arguments.SequenceEqual(
                    [
                        Utf8String.Copy("tslib"u8),
                        Utf8String.Copy("__importDefault"u8)
                    ]));
        Check(diagnostics.Any(d => d.Code == DiagnosticCode.XLetIsNotAllowedToBeUsedAsANameInLetOrConstDeclarations));
        Check(
            diagnostics.Any(
                d => d.Code == DiagnosticCode.CannotInitializeOuterScopedVariable0InTheSameScopeAsBlockScopedDeclaration1
                    && d.Arguments.SequenceEqual([Utf8String.Copy("shadow"u8), Utf8String.Copy("shadow"u8)])));
        Check(diagnostics.Count(d => d.Code == DiagnosticCode.ADefiniteAssignmentAssertionIsNotPermittedInThisContext) == 1);
        Check(diagnostics.Count(d => d.Code == DiagnosticCode.ARequiredElementCannotFollowAnOptionalElement) == 1);
        Check(diagnostics.All(d => d.Code != DiagnosticCode.Module0HasNoExportedMember1));
        var imports = source.DescendantsAndSelf().OfType<ImportSpecifierNode>().ToArray();
        Check(await checker.GetTypeAtLocationAsync(imports[0].Name!) is LiteralType { Value: double first } && first == 1);
        Check(await checker.GetTypeAtLocationAsync(imports[1].Name!) is LiteralType { Value: double second } && second == 2);
        await checker.CheckProgramAsync();
        Check(checker.DetailedDiagnosticsForFile(source).Count == diagnostics.Count);
        Check(before.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        return checks;
    }

    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Checker program assertion {checks + 1}");
            checks++;
        }
        static async ValueTask<CompilerProgram> Build(
            Dictionary<Utf8String, Utf8String> sources,
            CompilerProgram? previous = null,
            CompilerOptions? configuredOptions = null)
        {
            var files = sources.ToDictionary(p => p.Key, p => p.Value.Span.ToArray());
            var options = configuredOptions ?? new CompilerOptions();
            options.SetRaw("noLib"u8, "true"u8);
            return await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
                new("/project/tsconfig.json"u8, options, sources.Keys.ToArray(), [], [], []), previous, concurrency: 4);
        }
        var sources = new Dictionary<Utf8String, Utf8String>
        {
            ["/project/a.ts"u8] = "interface I<T> { a: T } namespace N { export interface A {} }"u8,
            ["/project/b.ts"u8] = "interface I<T> { b: T; self: this } namespace N { export interface B {} }"u8
        };
        var program = await Build(sources);
        var original = program.SourceFiles[0].Binding.Locals["I"u8];
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var host = new CheckerEnvironment(context, links);
        var environment = await CheckerSymbols.CreateAsync(program, links, host);
        var merged = environment.Globals["I"u8];
        Check(merged != original && merged.Declarations.Length == 2 && original.Declarations.Length == 1);
        Check(merged.Members.ContainsKey("a"u8) && merged.Members.ContainsKey("b"u8) && !original.Members.ContainsKey("b"u8));
        Check(ReferenceEquals(environment.Globals["globalThis"u8].Exports["I"u8], merged));
        var declaration = program.SourceFiles[1].Syntax.DescendantsAndSelf().OfType<InterfaceDeclarationNode>().First();
        Check(environment.Declaration(declaration) == merged);
        var type = await host.Scopes.ClassOrInterfaceAsync(merged);
        Check(type.ThisType is { IsThisType: true } && type.ThisType.Constraint == type && type.Target == type);
        Check(type.AllTypeParameters.Count == 2 && type.ResolvedTypeArguments!.Count == 1);
        Check(await host.Scopes.ClassOrInterfaceAsync(merged) == type);
        var updated = await Build(sources, program);
        Check(updated.ReusedSourceFiles == 2 && ReferenceEquals(updated.SourceFiles[0].Binding, program.SourceFiles[0].Binding));
        var otherContext = new TypeContext(true, true);
        var otherLinks = new CheckerLinks();
        var otherHost = new CheckerEnvironment(otherContext, otherLinks);
        var otherEnvironment = await CheckerSymbols.CreateAsync(updated, otherLinks, otherHost);
        Check(otherEnvironment.Globals["I"u8] != merged && original.Declarations.Length == 1);
        Check((await otherHost.Scopes.ClassOrInterfaceAsync(otherEnvironment.Globals["I"u8])).Context == otherContext);
        Check(links.Values.Get(environment.UndefinedSymbol).ResolvedType == context.UndefinedWideningType);
        Check(host.Globals.AnyArrayType == context.EmptyObjectType && host.Globals.AutoArrayType != context.EmptyObjectType);

        var cancelledLinks = new CheckerLinks();
        var cancelledHost = new CheckerEnvironment(new(true, true), cancelledLinks);
        using var cancellation = new CancellationTokenSource();
        cancelledHost.BeforeGlobalTypes = cancellation.Cancel;
        try
        {
            await CheckerSymbols.CreateAsync(program, cancelledLinks, cancelledHost, cancellation.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(original.Declarations.Length == 1 && !original.Members.ContainsKey("b"u8));
        var recoveredLinks = new CheckerLinks();
        var recoveredHost = new CheckerEnvironment(new(true, true), recoveredLinks);
        var recovered = await CheckerSymbols.CreateAsync(program, recoveredLinks, recoveredHost);
        Check(recovered.Globals["I"u8].Declarations.Length == 2);

        var retryProgram = await Build(
            new() { ["/project/rollback.ts"u8] = "interface Finished {} interface Stop {} interface Root extends Finished, Stop {}"u8 });
        var retryContext = new TypeContext();
        var retryLinks = new CheckerLinks();
        var retryHost = new CheckerEnvironment(retryContext, retryLinks);
        var retryEnvironment = await CheckerSymbols.CreateAsync(retryProgram, retryLinks, retryHost);
        int resolutions = 0;
        retryHost.BeforeResolveType = () =>
        {
            if (++resolutions == 2)
                throw new OperationCanceledException();
        };
        try
        {
            await retryHost.Scopes.ClassOrInterfaceAsync(retryEnvironment.Globals["Root"u8]);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(retryLinks.DeclaredTypes.Get(retryEnvironment.Globals["Root"u8]).DeclaredType is null);
        Check(retryLinks.DeclaredTypes.Get(retryEnvironment.Globals["Finished"u8]).DeclaredType is null);
        retryHost.BeforeResolveType = null;
        Check((await retryHost.Scopes.ClassOrInterfaceAsync(retryEnvironment.Globals["Root"u8])).ThisType is null);

        var contextualProgram = await Build(new() { ["/project/contextual.ts"u8] = "function outer<T>() { const f = value => value; }"u8 });
        var contextualContext = new TypeContext();
        var contextualLinks = new CheckerLinks();
        var contextualHost = new CheckerEnvironment(contextualContext, contextualLinks);
        var contextualEnvironment = await CheckerSymbols.CreateAsync(contextualProgram, contextualLinks, contextualHost);
        var arrow = contextualProgram.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<ArrowFunctionNode>().Single();
        var parameter = contextualContext.NewTypeParameter(new(SymbolFlags.TypeParameter, "Contextual"u8));
        var signature = contextualContext.NewSignature(0, arrow, [parameter], null, [], contextualContext.UnknownType, null, 0);
        contextualHost.ContextualSignatures[arrow] = signature;
        var scope = await contextualHost.Scopes.OuterAsync(arrow.Body!);
        Check(scope.Count == 2 && scope[1] == parameter && scope[0].Symbol?.Name == "T"u8);

        const int depth = 20_000;
        var source = new Utf8StringBuilder();
        for (int i = 0; i < depth - 1; i++)
            source.Append("interface I"u8).Append(i).Append(" extends I"u8).Append(i + 1).Append(" {}\n"u8);
        source.Append("interface I"u8).Append(depth - 1).Append(" { self: this }"u8);
        var deepProgram = await Build(new() { ["/project/deep.ts"u8] = source.ToUtf8String() });
        var deepContext = new TypeContext();
        var deepLinks = new CheckerLinks();
        var deepHost = new CheckerEnvironment(deepContext, deepLinks);
        var deepEnvironment = await CheckerSymbols.CreateAsync(deepProgram, deepLinks, deepHost);
        Check((await deepHost.Scopes.ClassOrInterfaceAsync(deepEnvironment.Globals["I0"u8])).ThisType is not null);
        Check(deepLinks.DeclaredTypes.Count == depth);
        SyntaxNode nested = deepProgram.SourceFiles[0].Syntax;
        for (int i = 0; i < depth; i++)
            nested = new BlockNode { Parent = nested };
        Check(deepEnvironment.Binding(nested) == deepProgram.SourceFiles[0].Binding);
        Check((await deepHost.Scopes.OuterAsync(nested)).Count == 0);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            await deepHost.Scopes.OuterAsync(nested, cancellation: stop.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        var queryProgram = await Build(new() { ["/project/query.ts"u8] = "const value = 1;"u8 });
        var queryNode = queryProgram.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<NumericLiteralNode>().Single();
        var firstChecker = await queryProgram.CreateCheckerAsync();
        var secondChecker = await queryProgram.CreateCheckerAsync();
        var firstType = await firstChecker.GetExpressionTypeAsync(queryNode);
        var secondType = await secondChecker.GetExpressionTypeAsync(queryNode);
        Check(firstType is LiteralType { Value: 1d } && secondType is LiteralType { Value: 1d });
        Check(firstType.Context != secondType.Context && firstType != secondType);
        Check(await firstChecker.GetExpressionTypeAsync(queryNode) == firstType);
        using var queryCancellation = new CancellationTokenSource();
        queryCancellation.Cancel();
        try
        {
            await queryProgram.CreateCheckerAsync(queryCancellation.Token);
            throw new InvalidOperationException("Cancelled checker initialization completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        try
        {
            await firstChecker.GetExpressionTypeAsync(new NumericLiteralNode { Text = "1"u8 });
            throw new InvalidOperationException("Foreign syntax accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        using var enteredQuery = new ManualResetEventSlim();
        using var releaseQuery = new ManualResetEventSlim();
        firstChecker.BeforeExpressionFinish = () =>
        {
            enteredQuery.Set();
            if (!releaseQuery.Wait(TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Checker query was not released");
        };
        var activeQuery = Task.Run(async () => await firstChecker.GetExpressionTypeAsync(queryNode));
        try
        {
            Check(enteredQuery.Wait(TimeSpan.FromSeconds(30)));
            using var queuedCancellation = new CancellationTokenSource();
            var queuedQuery = firstChecker.GetExpressionTypeAsync(queryNode, queuedCancellation.Token);
            Check(!queuedQuery.IsCompleted);
            queuedCancellation.Cancel();
            try
            {
                await queuedQuery;
                throw new InvalidOperationException("Queued query cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        finally
        {
            releaseQuery.Set();
        }
        Check(await activeQuery == firstType);
        firstChecker.BeforeExpressionFinish = null;
        Check(await firstChecker.GetExpressionTypeAsync(queryNode) == firstType);
        var libraryOptions = new CompilerOptions();
        libraryOptions.SetRaw("strict"u8, "true"u8);
        libraryOptions.SetRaw("lib"u8, "[\"es5\"]"u8);
        var libraryProgram = await CompilerProgram.CreateAsync(new LibraryFileSystem(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/library.ts"u8] = Wtf8.Encode("const values = [1,2,3].map(value => value + 1);")
        })), "/project"u8, new("/project/tsconfig.json"u8, libraryOptions, ["/project/library.ts"u8], [], [], []));
        Check(libraryProgram.SourceFiles.Any(f => f.Library));
        var libraryChecker = await libraryProgram.CreateCheckerAsync();
        var mapped = libraryProgram.GetFile("/project/library.ts"u8)!.Syntax.DescendantsAndSelf().OfType<VariableDeclarationNode>().Single().Initializer!;
        var mappedType = await libraryChecker.GetExpressionTypeAsync(mapped);
        Check(mappedType is TypeReference reference && reference.Target == libraryChecker.ArrayTarget(false)
            && (await libraryChecker.TypeArgumentsAsync(reference, default)).Single() == libraryChecker.Context.NumberType);
        Check(libraryChecker.Diagnostics.Count == 0 && libraryChecker.Environment.Diagnostics.Count == 0);
        var semanticProgram = await Build(new()
        {
            ["/project/check.ts"u8] = "const f=()=>{void absent;return 1;}; let value:number='bad';"u8
        });
        var semanticChecker = await semanticProgram.CreateCheckerAsync();
        await semanticChecker.CheckProgramAsync();
        Check(
            semanticChecker.CheckedFileCount == 1
                && semanticChecker.Diagnostics.Contains(DiagnosticCode.Type0IsNotAssignableToType1)
                && semanticChecker.Environment.Diagnostics.Contains(DiagnosticCode.CannotFindName0));
        Check(semanticChecker.CurrentSourceNode is null && semanticChecker.Instantiation.Engine.Depth == 0);
        int diagnosticCount = semanticChecker.Diagnostics.Count + semanticChecker.Environment.Diagnostics.Count;
        semanticChecker.BeforeSourceElement = _ => throw new InvalidOperationException("Completed source was checked again");
        await semanticChecker.CheckProgramAsync();
        Check(semanticChecker.Diagnostics.Count + semanticChecker.Environment.Diagnostics.Count == diagnosticCount);
        var cancelledChecker = await semanticProgram.CreateCheckerAsync();
        using var sourceCancellation = new CancellationTokenSource();
        cancelledChecker.BeforeSourceElement = _ => sourceCancellation.Cancel();
        try
        {
            await cancelledChecker.CheckProgramAsync(sourceCancellation.Token);
            throw new InvalidOperationException("Source cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(cancelledChecker.CheckedFileCount == 0 && cancelledChecker.CurrentSourceNode is null);
        bool invalidated = false;
        try
        {
            await cancelledChecker.GetExpressionTypeAsync(
                semanticProgram.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<NumericLiteralNode>().Single());
        }
        catch (InvalidOperationException)
        {
            invalidated = true;
        }
        Check(invalidated);
        var recoveredChecker = await semanticProgram.CreateCheckerAsync();
        await recoveredChecker.CheckProgramAsync();
        Check(recoveredChecker.CheckedFileCount == 1 && recoveredChecker.Diagnostics.SequenceEqual(semanticChecker.Diagnostics));
        var deepStatements = await Build(new() { ["/project/statements.ts"u8] = Utf8String.Concat(new Utf8String('{', depth), "1;"u8, new Utf8String('}', depth)) });
        var statementChecker = await deepStatements.CreateCheckerAsync();
        await statementChecker.CheckProgramAsync();
        Check(
            statementChecker.CheckedFileCount == 1
                && statementChecker.Diagnostics.Count == 0
                && statementChecker.CurrentSourceNode is null);
        var moduleProgram = await Build(new()
        {
            ["/project/dep.ts"u8] = "export const bad=absent; export const value=1;"u8,
            ["/project/main.ts"u8] = "import {bad,value} from './dep';bad;const text:string=value;"u8
        });
        var moduleChecker = await moduleProgram.CreateCheckerAsync();
        var mainFile = moduleProgram.GetFile("/project/main.ts"u8)!.Syntax;
        var depFile = moduleProgram.GetFile("/project/dep.ts"u8)!.Syntax;
        await moduleChecker.CheckSourceFileAsync(mainFile);
        Check(moduleChecker.DiagnosticCodesForFile(mainFile).SequenceEqual([DiagnosticCode.Type0IsNotAssignableToType1]));
        Check(moduleChecker.DiagnosticCodesForFile(depFile).SequenceEqual([DiagnosticCode.CannotFindName0]));
        await moduleChecker.CheckSourceFileAsync(depFile);
        Check(moduleChecker.DiagnosticCodesForFile(depFile).SequenceEqual([DiagnosticCode.CannotFindName0]));
        Check(moduleChecker.CheckedFileCount == 2);
        var moduleDiagnostics = moduleChecker.DiagnosticCodesForFile(mainFile).ToArray();
        await moduleChecker.CheckProgramAsync();
        Check(moduleChecker.DiagnosticCodesForFile(mainFile).SequenceEqual(moduleDiagnostics));
        var independentModuleChecker = await moduleProgram.CreateCheckerAsync();
        await independentModuleChecker.CheckProgramAsync();
        Check(independentModuleChecker.DiagnosticCodesForFile(mainFile).SequenceEqual(moduleDiagnostics)
            && independentModuleChecker.DiagnosticCodesForFile(depFile).SequenceEqual([DiagnosticCode.CannotFindName0]));
        var finalOptions = new CompilerOptions();
        finalOptions.SetRaw("strict"u8, "true"u8);
        finalOptions.SetRaw("noUnusedLocals"u8, "true"u8);
        finalOptions.SetRaw("noUnusedParameters"u8, "true"u8);
        var finalProgram = await Build(new()
        {
            ["/project/dep.ts"u8] = "export const bad={}.absent;"u8,
            ["/project/main.ts"u8] = "import {bad} from './dep';export function f<T>(unused:number){const value={};value.missing;let orphan=1;return bad;}"u8
        }, configuredOptions: finalOptions);
        var finalChecker = await finalProgram.CreateCheckerAsync();
        var finalMain = finalProgram.GetFile("/project/main.ts"u8)!.Syntax;
        var finalDep = finalProgram.GetFile("/project/dep.ts"u8)!.Syntax;
        await finalChecker.CheckSourceFileAsync(finalMain);
        Check(
            finalChecker.DiagnosticCodesForFile(finalMain).SequenceEqual(
                [
                        DiagnosticCode.Property0DoesNotExistOnType1,
                        DiagnosticCode.X0IsDeclaredButItsValueIsNeverRead,
                        DiagnosticCode.X0IsDeclaredButItsValueIsNeverRead,
                        DiagnosticCode.X0IsDeclaredButNeverUsed
                    ]));
        Check(finalChecker.DeferredMissingProperties.Count == 1 && finalChecker.DiagnosticCodesForFile(finalDep).Count == 0);
        await finalChecker.CheckSourceFileAsync(finalDep);
        Check(finalChecker.DiagnosticCodesForFile(finalDep).SequenceEqual([DiagnosticCode.Property0DoesNotExistOnType1]));
        Check(finalChecker.DeferredMissingProperties.Count == 0 && finalChecker.CheckedFileCount == 2);
        await finalChecker.CheckProgramAsync();
        Check(
            finalChecker.DiagnosticCodesForFile(finalMain).SequenceEqual(
                [
                        DiagnosticCode.Property0DoesNotExistOnType1,
                        DiagnosticCode.X0IsDeclaredButItsValueIsNeverRead,
                        DiagnosticCode.X0IsDeclaredButItsValueIsNeverRead,
                        DiagnosticCode.X0IsDeclaredButNeverUsed
                    ]));
        var finalIndependent = await finalProgram.CreateCheckerAsync();
        await finalIndependent.CheckProgramAsync();
        Check(finalIndependent.DiagnosticCodesForFile(finalMain).SequenceEqual(finalChecker.DiagnosticCodesForFile(finalMain))
            && finalIndependent.DiagnosticCodesForFile(finalDep).SequenceEqual([DiagnosticCode.Property0DoesNotExistOnType1]));
        var directiveProgram = await Build(new()
        {
            ["/project/directives.ts"u8] = "// 日本語 😀\n// @ts-ignore\nlet first:number='bad';\n// @ts-expect-error\n\n// comment\nlet second:number='bad';\n// @ts-expect-error\nlet unused=1;\n// @ts-ignore\n/* barrier */\nlet barrier:number='bad';"u8
        });
        var directiveChecker = await directiveProgram.CreateCheckerAsync();
        await directiveChecker.CheckProgramAsync();
        var directiveFile = directiveProgram.SourceFiles[0].Syntax;
        Check(
            directiveChecker.DiagnosticCodesForFile(directiveFile).SequenceEqual(
                [
                        DiagnosticCode.Type0IsNotAssignableToType1,
                        DiagnosticCode.Type0IsNotAssignableToType1,
                        DiagnosticCode.Type0IsNotAssignableToType1
                    ]));
        Check(
            directiveChecker.DiagnosticCodesForProgramFile(directiveFile).SequenceEqual(
                [DiagnosticCode.Type0IsNotAssignableToType1, DiagnosticCode.UnusedTsExpectErrorDirective]));
        Check(directiveFile.CommentDirectives.Count(d => d.ExpectError) == 2);
        var duplicateProgram = await Build(new() { ["/project/duplicate.ts"u8] = "const duplicate=1;const duplicate=2;"u8 });
        var duplicateChecker = await duplicateProgram.CreateCheckerAsync();
        await duplicateChecker.CheckProgramAsync();
        Check(
            duplicateChecker.DiagnosticCodesForProgramFile(duplicateProgram.SourceFiles[0].Syntax).SequenceEqual(
                [DiagnosticCode.CannotRedeclareBlockScopedVariable0, DiagnosticCode.CannotRedeclareBlockScopedVariable0]));
        var noCheckOptions = new CompilerOptions();
        noCheckOptions.SetRaw("noCheck"u8, "true"u8);
        var noCheckProgram = await Build(new() { ["/project/unchecked.ts"u8] = "absent;"u8 }, configuredOptions: noCheckOptions);
        var noCheckChecker = await noCheckProgram.CreateCheckerAsync();
        await noCheckChecker.CheckProgramAsync();
        Check(
            noCheckChecker.CheckedFileCount == 0
                && noCheckChecker.DiagnosticCodesForProgramFile(noCheckProgram.SourceFiles[0].Syntax).Count == 0);
        var noCheckDirectiveProgram = await Build(new() { ["/project/unchecked.ts"u8] = "// @ts-nocheck\nabsent;"u8 });
        var noCheckDirectiveChecker = await noCheckDirectiveProgram.CreateCheckerAsync();
        await noCheckDirectiveChecker.CheckProgramAsync();
        Check(
            noCheckDirectiveChecker.CheckedFileCount == 0
                && noCheckDirectiveChecker.DiagnosticCodesForProgramFile(noCheckDirectiveProgram.SourceFiles[0].Syntax).Count == 0);
        var loopProgram = await Build(new()
        {
            ["/project/loop.ts"u8] = "type Candidate={mode:'a';output:unknown}|{mode:'b'};export function run():never{let lastCandidate:Candidate|null=null;while(true){const candidate:Candidate={mode:'a',output:lastCandidate} as const;lastCandidate=candidate;}}"u8
        });
        var loopChecker = await loopProgram.CreateCheckerAsync();
        // Guard the corpus reproduction that previously recursed without terminating.
        using var loopCancellation = new CancellationTokenSource();
        using var loopFinished = new ManualResetEventSlim();
        var loopGuard = new Thread(() =>
        {
            if (!loopFinished.Wait(TimeSpan.FromSeconds(10)))
                loopCancellation.Cancel();
        })
        { IsBackground = true };
        loopGuard.Start();
        try
        {
            await loopChecker.CheckProgramAsync(loopCancellation.Token);
        }
        finally
        {
            loopFinished.Set();
            loopGuard.Join();
        }
        Check(loopChecker.CheckedFileCount == 1 && loopChecker.DiagnosticCodesForFile(loopProgram.SourceFiles[0].Syntax).Count == 0);
        Check(loopChecker.FlowTypes.ActiveLoopCount == 0);
        checks += await DisposableSafety();
        checks += await ImportSafety();
        checks += await DeclarationSafety();
        checks += await ImportPathSafety();
        checks += await ContextGrammarSafety();
        checks += await DiagnosticDetailsSafety();
        checks += await CheckerDisplayTests.Safety();
        checks += await CheckerQueryTests.Safety();
        checks += await CheckerQueryTests.SymbolSafety();
        checks += await CheckerQueryTests.ScopeSafety();
        checks += await CheckerContextQueryTests.Safety();
        checks += await CheckerVisibilityTests.Safety();
        checks += await CheckerSymbolChainTests.Safety();
        checks += await CheckerAccessibilityTests.Safety();
        Console.WriteLine($"{checks} program/checker ownership assertions; interface and scope depth 20000");
    }

    private static async Task<int> DiagnosticDetailsSafety()
    {
        Utf8String source = """
            // 多字节 😀
            const value = absent;
            // @ts-ignore
            absent;
            // @ts-expect-error
            const valid = 1;
            const named = (x: number) => {
                return x;
            };
            class C { protected constructor() {} }
            const missing = class {};
            const empty = () => {};
            const satisfied = value satisfies number;
            switch (value) { case 1: break; default: break; }
            """u8;
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        { ["/project/main.ts"u8] = source.Span.ToArray() }), "/project"u8,
            new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts"u8)!.Syntax;
        await checker.CheckProgramAsync();
        var diagnostics = checker.DetailedDiagnosticsForProgramFile(file);
        var missing = diagnostics.Single(d => d.Code == DiagnosticCode.CannotFindName0);
        if (!(missing.Arguments is [{ Span: var matchedText3 }] && matchedText3.SequenceEqual("absent"u8)) || missing.FileName != file.FileName
            || missing.Start != source.IndexOf("absent"u8, StringComparison.Ordinal)
            || missing.Length != 6 || missing.Format() != "Cannot find name 'absent'."u8)
            throw new InvalidOperationException("Complete missing-name diagnostic");
        if (diagnostics.Count != 2
            || diagnostics.Single(
                d => d.Code == DiagnosticCode.UnusedTsExpectErrorDirective).Format() != "Unused '@ts-expect-error' directive."u8)
            throw new InvalidOperationException("Diagnostic directive filtering");
        var repeat = checker.DetailedDiagnosticsForProgramFile(file);
        if (!repeat.SequenceEqual(diagnostics))
            throw new InvalidOperationException("Detailed diagnostics changed on repeated read");
        var nodes = file.DescendantsAndSelf().ToArray();
        Utf8String Span(SyntaxNode node)
        {
            var (start, end) = CheckerDiagnostic.ErrorRange(file, node);
            return Utf8String.Format(file.Source.Text[start..end]);
        }
        if (Span(nodes.OfType<VariableDeclarationNode>().First()) != "value"u8
            || Span(nodes.OfType<ReturnStatementNode>().Single()) != "return"u8
            || Span(nodes.OfType<ArrowFunctionNode>().First()) != "(x: number) => {"u8
            || Span(nodes.OfType<ConstructorDeclarationNode>().Single()) != "protected constructor"u8
            || Span(nodes.OfType<ClassExpressionNode>().Single()) != "class"u8
            || Span(nodes.OfType<SatisfiesExpressionNode>().Single()) != "satisfies"u8
            || Span(nodes.OfType<CaseOrDefaultClauseNode>().First()) != "case 1:"u8)
            throw new InvalidOperationException("Checker error ranges");
        Utf8String[] arguments = ["original"u8];
        var owned = CheckerDiagnostic.Create(nodes.OfType<VariableDeclarationNode>().First(), Messages.Cannot_find_name_0, arguments);
        arguments[0] = "changed"u8;
        if (!(owned.Arguments is [{ Span: var matchedText4 }] && matchedText4.SequenceEqual("original"u8)))
            throw new InvalidOperationException("Diagnostic did not retain its arguments");
        var chain = owned with { MessageChain = [owned with { MessageChain = [owned] }] };
        if (chain.Format() != "Cannot find name 'original'.\n  Cannot find name 'original'.\n    Cannot find name 'original'."u8)
            throw new InvalidOperationException("Diagnostic message-chain formatting");
        return 6;
    }

    private static async Task<int> ContextGrammarSafety()
    {
        Utf8String source = """
            import { value } from 'pkg' with { type: 1, active: true };
            import { value as other } from 'pkg' with { type: {} };
            try {} catch ({ message }: any) { const text = message; }
            try {} catch (error: number) {}
            try {} catch (error = 0) {}
            try {} catch (error) { let error; }
            declare let invalid = 1;
            declare const typed: number = 1;
            declare const good = -1;
            declare const bad = 1 + 2;
            declare enum E { A = 0 }
            declare const member = E.A;
            declare const index = E['A'];
            declare const aliasMember = member;
            declare class C { readonly item = E['A']; mutable = 1; readonly bad = aliasMember; }
            """u8;
        Utf8String globals = """
            interface ImportAttributes { [key: string]: string | number | boolean; }
            declare module 'pkg' { export const value: number; }
            """u8;
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("target"u8, "\"esnext\""u8);
        options.SetRaw("module"u8, "\"preserve\""u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        { ["/project/main.ts"u8] = source.Span.ToArray(), ["/project/globals.d.ts"u8] = globals.Span.ToArray() }), "/project"u8,
            new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8, "/project/globals.d.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts"u8)!.Syntax;
        await checker.CheckSourceFileAsync(file);
        var codes = checker.DiagnosticCodesForFile(file);
        if (!codes.SequenceEqual(
            [
                    DiagnosticCode.InitializersAreNotAllowedInAmbientContexts,
                    DiagnosticCode.InitializersAreNotAllowedInAmbientContexts,
                    DiagnosticCode.InitializersAreNotAllowedInAmbientContexts,
                    DiagnosticCode.CatchClauseVariableTypeAnnotationMustBeAnyOrUnknownIfSpecified,
                    DiagnosticCode.CatchClauseVariableCannotHaveAnInitializer,
                    DiagnosticCode.AConstInitializerInAnAmbientContextMustBeAStringOrNumericLiteralOrLiteralEnumReference,
                    DiagnosticCode.AConstInitializerInAnAmbientContextMustBeAStringOrNumericLiteralOrLiteralEnumReference,
                    DiagnosticCode.AConstInitializerInAnAmbientContextMustBeAStringOrNumericLiteralOrLiteralEnumReference,
                    DiagnosticCode.Type0IsNotAssignableToType1,
                    DiagnosticCode.CannotRedeclareIdentifier0InCatchClause,
                    DiagnosticCode.ImportAttributeValuesMustBeStringLiteralExpressions,
                    DiagnosticCode.ImportAttributeValuesMustBeStringLiteralExpressions,
                    DiagnosticCode.ImportAttributeValuesMustBeStringLiteralExpressions
                ]))
            throw new InvalidOperationException($"Context grammar diagnostics: {string.Join(',', codes)}");
        return 1;
    }

    private static async Task<int> ImportPathSafety()
    {
        Utf8String source = """
            import { value } from './dep';
            import { view } from './view';
            import './absent';
            import './data.json';
            import './dep.ts';
            import './script.js';
            import './theme.asset';
            import fs = require('fs');
            import 'fs';
            import untyped from 'untyped';
            """u8;
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("module"u8, "\"node16\""u8);
        options.SetRaw("jsx"u8, "\"preserve\""u8);
        options.SetRaw("strict"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.mts"u8] = source.Span.ToArray(),
            ["/project/globals.d.ts"u8] = Wtf8.Encode("declare module '*.asset' {}"),
            ["/project/dep.ts"u8] = Wtf8.Encode("export const value = 1;"),
            ["/project/dep.mts"u8] = Wtf8.Encode("export const value = 1;"),
            ["/project/view.tsx"u8] = Wtf8.Encode("export const view = 1;"),
            ["/project/script.ts"u8] = Wtf8.Encode("const value = 1;"),
            ["/project/node_modules/untyped/index.js"u8] = Wtf8.Encode("exports.value = 1;"),
            ["/project/node_modules/untyped/package.json"u8] = Wtf8.Encode(
                "{\"name\":\"untyped\",\"version\":\"1.0.0\",\"main\":\"index.js\"}"),
            ["/project/data.json"u8] = Wtf8.Encode("{}")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.mts"u8, "/project/globals.d.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.mts"u8)!.Syntax;
        await checker.CheckSourceFileAsync(file);
        var codes = checker.DiagnosticCodesForFile(file);
        if (!codes.SequenceEqual(
            [
                    DiagnosticCode.CannotFindName0DoYouNeedToInstallTypeDefinitionsForNodeTryNpmISaveDevTypesSlashnodeAndThenAddNodeToTheTypesFieldInYourTsconfig,
                    DiagnosticCode.CannotFindModule0ConsiderUsingResolveJsonModuleToImportModuleWithJsonExtension,
                    DiagnosticCode.RelativeImportPathsNeedExplicitFileExtensionsInECMAScriptImportsWhenModuleResolutionIsNode16OrNodenextConsiderAddingAnExtensionToTheImportPath,
                    DiagnosticCode.RelativeImportPathsNeedExplicitFileExtensionsInECMAScriptImportsWhenModuleResolutionIsNode16OrNodenextDidYouMean0,
                    DiagnosticCode.RelativeImportPathsNeedExplicitFileExtensionsInECMAScriptImportsWhenModuleResolutionIsNode16OrNodenextDidYouMean0,
                    DiagnosticCode.CannotFindModuleOrTypeDeclarationsForSideEffectImportOf0,
                    DiagnosticCode.CouldNotFindADeclarationFileForModule01ImplicitlyHasAnAnyType
                ]))
            throw new InvalidOperationException($"Import path diagnostics: {string.Join(',', codes)}");
        if (checker.SuggestedImportExtension("/project/dep"u8) != ".mjs"u8)
            throw new InvalidOperationException("Import extension priority changed");
        if (checker.SuggestedImportExtension("/project/view"u8) != ".jsx"u8)
            throw new InvalidOperationException("Preserved JSX extension changed");
        return 3;
    }

    private static async Task<int> DeclarationSafety()
    {
        static async ValueTask<CompilerProgram> Build(Dictionary<Utf8String, Utf8String> files, bool noEmit = false)
        {
            var options = new CompilerOptions();
            options.SetRaw("noLib"u8, "true"u8);
            options.SetRaw("target"u8, "\"es2015\""u8);
            options.SetRaw("module"u8, "\"commonjs\""u8);
            options.SetRaw("noEmit"u8, noEmit ? Utf8String.Copy("true"u8) : Utf8String.Copy("false"u8));
            options.SetRaw("allowJs"u8, "true"u8);
            return await CompilerProgram.CreateAsync(new MemoryFileSystem(files.ToDictionary(p => p.Key, p => p.Value.Span.ToArray())),
                "/project"u8, new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        }
        var duplicates = await Build(new()
        {
            ["/project/a.ts"u8] = "class Duplicate {} let repeated: number; enum Choice {}"u8,
            ["/project/b.ts"u8] = "class Duplicate {} let repeated: number; interface Choice {}"u8
        });
        var duplicateChecker = await duplicates.CreateCheckerAsync();
        foreach (var file in duplicates.SourceFiles)
            if (!duplicateChecker.DiagnosticCodesForFile(file.Syntax).SequenceEqual(
                [
                        DiagnosticCode.DuplicateIdentifier0,
                        DiagnosticCode.CannotRedeclareBlockScopedVariable0,
                        DiagnosticCode.EnumDeclarationsCanOnlyMergeWithNamespaceOrOtherEnumDeclarations
                    ]))
                throw new InvalidOperationException("Merge diagnostics lost declaration file attribution");
        var related = duplicates.SourceFiles.SelectMany(file => duplicateChecker.DetailedDiagnosticsForFile(file.Syntax)).ToArray();
        if (related.Length != 6 || related.Any(d => d.RelatedInformation.Count != 1
            || d.FileName == d.RelatedInformation[0].FileName))
            throw new InvalidOperationException("Merge diagnostics lost related declarations");
        var plainJs = await Build(new()
        {
            ["/project/a.js"u8] = "class Duplicate {}"u8,
            ["/project/b.ts"u8] = "class Duplicate {}"u8
        });
        var jsChecker = await plainJs.CreateCheckerAsync();
        if (jsChecker.DiagnosticCodesForFile(plainJs.GetFile("/project/a.js"u8)!.Syntax).Count != 0
            || !jsChecker.DiagnosticCodesForFile(plainJs.GetFile("/project/b.ts"u8)!.Syntax).SequenceEqual([DiagnosticCode.DuplicateIdentifier0]))
            throw new InvalidOperationException("Plain JavaScript merge suppression affected the TypeScript declaration");
        Utf8String source = """
            export {};
            const require = 0;
            const { exports } = { exports: 0 };
            function parameters(require: number, exports: number) { return require + exports; }
            function Reflect() {}
            declare class Base { static value(): number; }
            class Derived extends Base { static result = super.value(); }
            const WeakMap = 0;
            class Private { #value = 0; }
            """u8;
        foreach (bool noEmit in new[] { false, true })
        {
            var names = await Build(new() { ["/project/main.ts"u8] = source }, noEmit);
            var checker = await names.CreateCheckerAsync();
            var file = names.GetFile("/project/main.ts"u8)!.Syntax;
            await checker.CheckSourceFileAsync(file);
            DiagnosticCode[] expected = noEmit
                ? []
                :
                    [
                        DiagnosticCode.DuplicateIdentifier0CompilerReservesName1InTopLevelScopeOfAModule,
                        DiagnosticCode.DuplicateIdentifier0CompilerReservesName1InTopLevelScopeOfAModule,
                        DiagnosticCode.DuplicateIdentifier0CompilerReservesName1WhenEmittingSuperReferencesInStaticInitializers,
                        DiagnosticCode.CompilerReservesName0WhenEmittingPrivateIdentifierDownlevel
                    ];
            var codes = checker.DiagnosticCodesForFile(file);
            if (!codes.SequenceEqual(expected))
                throw new InvalidOperationException($"Declaration collision diagnostics (noEmit={noEmit}): {string.Join(',', codes)}");
        }
        return 6;
    }

    private static async Task<int> ImportSafety()
    {
        Utf8String globals = """
            interface Array<T> { length: number; [n: number]: T; }
            interface Promise<T> { then(onfulfilled: (value: T) => unknown): unknown; }
            declare const Promise: any;
            interface ImportAttributes { [key: string]: string; }
            interface ImportCallOptions { with?: ImportAttributes; }
            declare module '*.asset' { const value: number; export default value; }
            declare module '*.asset' with { type: 'text' } { const value: string; export default value; }
            """u8;
        Utf8String source = """
            import type { Box } from './dep';
            type Typed = import('./dep').Box<number>;
            type Factory = typeof import('./dep').make<string>;
            declare let typed: Typed;
            const number: number = typed.value;
            declare let make: Factory;
            const text: string = make('text');
            const dynamic = import('./dep');
            const promised: Promise<typeof import('./dep')> = dynamic;
            import(123);
            type Invalid = import('./dep');
            import asset from './file.asset' with { type: 'text' };
            const assetText: string = asset;
            type Asset = typeof import('./file.asset', { with: { type: 'text' } });
            declare let projected: Asset;
            const projectedText: string = projected.default;
            import('./file.asset', { with: { type: 1 } });
            """u8;
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        options.SetRaw("module"u8, "\"preserve\""u8);
        options.SetRaw("target"u8, "\"esnext\""u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = source.Span.ToArray(),
            ["/project/globals.d.ts"u8] = globals.Span.ToArray(),
            ["/project/dep.ts"u8] = Wtf8.Encode(
                "export class Box<T> { constructor(public value: T) {} } export function make<T>(value: T) { return value; }")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8, "/project/globals.d.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts"u8)!.Syntax;
        var nodes = file.DescendantsAndSelf().ToArray();
        var parents = nodes.Select(n => n.Parent).ToArray();
        await checker.CheckSourceFileAsync(file);
        var codes = checker.DiagnosticCodesForFile(file);
        if (!codes.SequenceEqual(
            [
                    DiagnosticCode.Module0DoesNotReferToATypeButIsUsedAsATypeHereDidYouMeanTypeofImport0,
                    DiagnosticCode.Type0IsNotAssignableToType1,
                    DiagnosticCode.DynamicImportSSpecifierMustBeOfTypeStringButHereHasType0
                ]))
            throw new InvalidOperationException($"Import diagnostics: {string.Join(',', codes)}");
        if (!nodes.Select(n => n.Parent).SequenceEqual(parents))
            throw new InvalidOperationException("Import checking changed source parents");
        var initializer = nodes.OfType<VariableDeclarationNode>().Single(n => (n.Name is IdentifierNode { Text: { Span: var matchedText5 } } && matchedText5.SequenceEqual("dynamic"u8))).Initializer!;
        var type = await checker.GetExpressionTypeAsync(initializer);
        if (type is not TypeReference reference || reference.Target?.Symbol?.Name != "Promise"u8)
            throw new InvalidOperationException("Dynamic import did not return Promise");
        var arguments = await checker.TypeArgumentsAsync(reference, default);
        if (await checker.Properties.PropertyAsync(arguments.Single(), "make"u8) is null)
            throw new InvalidOperationException("Dynamic import lost module exports");
        return 4;
    }

    private static async Task<int> DisposableSafety()
    {
        Utf8String library = """
            interface Array<T> { length: number; [n: number]: T; }
            interface SymbolConstructor { readonly dispose: unique symbol; readonly asyncDispose: unique symbol; }
            declare const Symbol: SymbolConstructor;
            interface Disposable { [Symbol.dispose](): void; }
            interface AsyncDisposable { [Symbol.asyncDispose](): PromiseLike<void>; }
            interface PromiseLike<T> { then(onfulfilled: (value: T) => unknown): unknown; }
            interface Promise<T> extends PromiseLike<T> { }
            declare const Promise: any;
            """u8;
        Utf8String source = """
            export {};
            function sync() {
                using good = { [Symbol.dispose]() {} };
                using bad = 1;
                switch (0) { case 0: using invalid = null; }
            }
            async function asynchronous() {
                await using good = { [Symbol.asyncDispose]() { return null as any; } };
                await using bad = 1;
            }
            function nonAsync() { await using invalid = null; }
            class C { static { await using invalid = null; } }
            using { x } = { x: null };
            """u8;
        static async ValueTask<(Checker Checker, SourceFileNode File)> Create(
            Utf8String text,
            Utf8String library,
            Utf8String? helpers,
            bool importHelpers)
        {
            var options = new CompilerOptions();
            options.SetRaw("noLib"u8, "true"u8);
            options.SetRaw("strict"u8, "true"u8);
            options.SetRaw("target"u8, "\"es2015\""u8);
            options.SetRaw("module"u8, "\"esnext\""u8);
            options.SetRaw("importHelpers"u8, importHelpers ? Utf8String.Copy("true"u8) : Utf8String.Copy("false"u8));
            var files = new Dictionary<Utf8String, byte[]>
            { ["/project/main.ts"u8] = text.Span.ToArray(), ["/project/globals.d.ts"u8] = library.Span.ToArray() };
            if (helpers is not null)
                files.Add("/project/node_modules/tslib/index.d.ts"u8, helpers.Value.Span.ToArray());
            var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
                new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8, "/project/globals.d.ts"u8], [], [], []));
            return (await program.CreateCheckerAsync(), program.GetFile("/project/main.ts"u8)!.Syntax);
        }
        var (checker, file) = await Create(source, library, null, false);
        await checker.CheckSourceFileAsync(file);
        DiagnosticCode[] expected =
            [
                DiagnosticCode.X0DeclarationsMayNotHaveBindingPatterns,
                DiagnosticCode.XUsingDeclarationsAreNotAllowedInCaseOrDefaultClausesUnlessContainedWithinABlock,
                DiagnosticCode.TheInitializerOfAUsingDeclarationMustBeEitherAnObjectWithASymbolDisposeMethodOrBeNullOrUndefined,
                DiagnosticCode.TheInitializerOfAnAwaitUsingDeclarationMustBeEitherAnObjectWithASymbolAsyncDisposeOrSymbolDisposeMethodOrBeNullOrUndefined,
                DiagnosticCode.XAwaitUsingStatementsAreOnlyAllowedWithinAsyncFunctionsAndAtTheTopLevelsOfModules,
                DiagnosticCode.XAwaitUsingStatementsCannotBeUsedInsideAClassStaticBlock
            ];
        var codes = checker.DiagnosticCodesForFile(file);
        if (!codes.SequenceEqual(expected))
            throw new InvalidOperationException($"Disposable diagnostics: {string.Join(',', codes)}");
        int checks = 1;
        foreach (Utf8String? helpers in new Utf8String?[]
        {
            null,
            "export {};"u8,
            "export declare const __addDisposableResource: any, __disposeResources: any;"u8
        })
        {
            var (helperChecker, helperFile) = await Create("export {}; using first = null; using second = null;"u8, library, helpers, true);
            await helperChecker.CheckSourceFileAsync(helperFile);
            DiagnosticCode[] helperExpected = helpers is null
                ? [DiagnosticCode.ThisSyntaxRequiresAnImportedHelperButModule0CannotBeFound]
                : helpers == "export {};"u8
                    ?
                        [
                            DiagnosticCode.ThisSyntaxRequiresAnImportedHelperNamed1WhichDoesNotExistIn0ConsiderUpgradingYourVersionOf0,
                            DiagnosticCode.ThisSyntaxRequiresAnImportedHelperNamed1WhichDoesNotExistIn0ConsiderUpgradingYourVersionOf0
                        ]
                    : [];
            var helperCodes = helperChecker.DiagnosticCodesForFile(helperFile);
            if (!helperCodes.SequenceEqual(helperExpected))
                throw new InvalidOperationException($"Disposable helper diagnostics: {string.Join(',', helperCodes)}");
            checks++;
        }
        return checks;
    }

    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                Process(input.RootElement, writer).GetAwaiter().GetResult();
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private static async Task Process(JsonElement input, Utf8JsonWriter writer)
    {
        var files = input.GetProperty("files"u8).EnumerateObject().ToDictionary(
            p => JsonStrings.GetName(p),
            p => p.Value.GetBytesFromBase64());
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        if (input.TryGetProperty("options"u8, out var supplied))
            foreach (var property in supplied.EnumerateObject())
                options.Set(JsonStrings.GetName(property), property.Value);
        var roots = input.GetProperty("roots"u8).EnumerateArray().Select(p => JsonStrings.GetString(p)!).ToArray();
        int concurrency = input.GetProperty("concurrency"u8).GetInt32();
        var config = new ParsedConfig("/project/tsconfig.json"u8, options, roots, [], [], []);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8, config, concurrency: concurrency);
        Checker? typeHost = input.TryGetProperty("typeNodes"u8, out var typeOption) && typeOption.GetBoolean()
            ? await program.CreateCheckerAsync() : null;
        var context = typeHost?.Context ?? new TypeContext(options.StrictOption("strictNullChecks"u8),
            options.Boolean("exactOptionalPropertyTypes"u8) ?? false);
        var links = typeHost?.Links ?? new CheckerLinks();
        var host = typeHost?.Environment ?? new CheckerEnvironment(context, links);
        var environment = typeHost?.Symbols ?? await CheckerSymbols.CreateAsync(program, links, host);
        if (input.TryGetProperty("nodeBuilderTracking"u8, out var trackingOption) && trackingOption.GetBoolean())
        {
            await CheckerNodeBuilderTests.WriteAsync(typeHost!, program.GetFile("/project/main.ts"u8)!.Syntax,
                input.GetProperty("typeSyntaxFlags"u8).EnumerateArray().Select(v => (NodeBuilderFlags)v.GetUInt32()).ToArray(), writer,
                input.TryGetProperty("nodeBuilderInternalFlags"u8, out var internalFlags)
                    ? internalFlags.EnumerateArray().Select(v => (NodeBuilderInternalFlags)v.GetUInt32()).ToArray() : null);
            return;
        }
        if (input.TryGetProperty("displayFormats"u8, out var formatOption) && formatOption.GetBoolean())
        {
            await CheckerDisplayTests.FormatsAsync(typeHost!, program.GetFile("/project/main.ts"u8)!.Syntax,
                input.GetProperty("typeFormatFlags"u8).EnumerateArray().Select(v => (TypeFormatFlags)v.GetUInt32()).ToArray(), writer);
            return;
        }
        if (input.TryGetProperty("typeDisplays"u8, out var displayOption) && displayOption.GetBoolean())
        {
            writer.WriteStartObject();
            writer.WriteStartArray("typeDisplays"u8);
            foreach (var declaration in program.GetFile("/project/main.ts"u8)!.Syntax.DescendantsAndSelf().OfType<VariableDeclarationNode>())
                if (declaration.Name is IdentifierNode name
                    && name.Text.Span.StartsWith("show"u8, StringComparison.Ordinal)
                    && declaration.Type is not null)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(name.Text.Span);
                    writer.WriteStringValue(
                        (await typeHost!.TypeDisplay.GetAsync(await typeHost.GetTypeFromTypeNodeAsync(declaration.Type))).Span);
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
            writer.WriteEndObject();
            return;
        }
        if (input.TryGetProperty("semantic"u8, out var semanticOption) && semanticOption.GetBoolean())
        {
            await typeHost!.CheckSourceFileAsync(program.GetFile("/project/main.ts"u8)!.Syntax);
            writer.WriteStartObject();
            writer.WriteStartArray("semanticDiagnostics"u8);
            foreach (int code in typeHost!.DiagnosticCodesForFile(program.GetFile("/project/main.ts"u8)!.Syntax))
                writer.WriteNumberValue(code);
            writer.WriteEndArray();
            if (input.TryGetProperty("semanticDetails"u8, out var detailsOption) && detailsOption.GetBoolean())
            {
                writer.WritePropertyName("semanticDiagnosticDetails"u8);
                CheckerCorpusTests.WriteDiagnostics(
                    writer,
                    typeHost.DetailedDiagnosticsForFile(program.GetFile("/project/main.ts"u8)!.Syntax));
            }
            writer.WriteEndObject();
            return;
        }
        var nodes = program.SourceFiles.SelectMany(file => file.Syntax.DescendantsAndSelf()).ToArray();
        var nodeIds = nodes.Select((node, i) => (node, i)).ToDictionary(p => p.node, p => p.i + 1);
        int Node(SyntaxNode? node) => node is null ? 0 : nodeIds.GetValueOrDefault(node);
        var symbolIds = new Dictionary<Symbol, int>(ReferenceEqualityComparer.Instance);
        var symbols = new List<Symbol>();
        int SymbolId(Symbol? symbol)
        {
            if (symbol is null)
                return 0;
            if (!symbolIds.TryGetValue(symbol, out int id))
            {
                symbolIds.Add(symbol, id = symbols.Count + 1);
                symbols.Add(symbol);
            }
            return id;
        }
        var typeIds = new Dictionary<Type, int>();
        var types = new List<Type>();
        int TypeId(Type? type)
        {
            if (type is null)
                return 0;
            if (!typeIds.TryGetValue(type, out int id))
            {
                typeIds.Add(type, id = types.Count + 1);
                types.Add(type);
            }
            return id;
        }
        var privateOwners = new Dictionary<Utf8String, int>();
        foreach (var node in nodes.Where(SemanticSyntax.ClassLike))
            if (environment.Binding(node)?.Get(node)?.Symbol is { } owner)
                foreach (Utf8String key in owner.Members.Keys.Concat(owner.Exports.Keys))
                    if (key.Span.StartsWith(Symbol.InternalPrefix + "#"u8, StringComparison.Ordinal) && key.Span.IndexOf((byte)'@') is > 0 and var end)
                        privateOwners[key[..end]] = Node(node);
        Utf8String CanonicalName(Utf8String name)
        {
            if (name.Span.StartsWith(Symbol.InternalPrefix + "@"u8, StringComparison.Ordinal))
                foreach (var unique in context.UniqueSymbols)
                    if (unique.Name == name && unique.Symbol?.Declarations.FirstOrDefault() is { } declaration)
                        return Utf8String.Concat(Utf8String.Concat(Symbol.InternalPrefix, Utf8String.Copy("@"u8), unique.Symbol.Name), "@node"u8, Utf8String.Format(Node(declaration)));
            int end = name.Span.IndexOf((byte)'@');
            return end > 0 && privateOwners.TryGetValue(name[..end], out int owner)
                ? Utf8String.Concat(Symbol.InternalPrefix, "#node"u8, Utf8String.Format(owner)) + name[end..] : name;
        }
        void Name(Utf8String text) => writer.WriteBase64StringValue(Symbol.EscapeName(CanonicalName(text)).Span.ToArray());
        void Table(IReadOnlyDictionary<Utf8String, Symbol> table)
        {
            writer.WriteStartArray();
            foreach (var (name, symbol) in table.OrderBy(p => CanonicalName(p.Key), Comparer<Utf8String>.Create(TypeOrder.CompareSymbolNames)))
            {
                writer.WriteStartArray();
                Name(name);
                writer.WriteNumberValue(SymbolId(symbol));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        void TypeIds(IEnumerable<Type>? values)
        {
            if (values is null)
            {
                writer.WriteNullValue();
                return;
            }
            writer.WriteStartArray();
            foreach (var type in values)
                writer.WriteNumberValue(TypeId(type));
            writer.WriteEndArray();
        }
        IReadOnlyList<Type>? OrderedInferences(IReadOnlyList<Type>? values)
        {
            if (values is null)
                return null;
            var result = values.ToArray();
            var groups = new Dictionary<SyntaxNode, List<int>>();
            for (int i = 0; i < values.Count; i++)
            {
                var declaration = values[i].Symbol?.Declarations.FirstOrDefault();
                if (declaration?.Parent is not InferTypeNode infer)
                    continue;
                for (var owner = infer.Parent; owner is not null; owner = owner.Parent)
                    if (owner is ConditionalTypeNode)
                    {
                        if (!groups.TryGetValue(owner, out var positions))
                            groups[owner] = positions = [];
                        positions.Add(i);
                        break;
                    }
            }
            foreach (var positions in groups.Values)
            {
                var parameters = positions.Select(i => values[i]).OrderBy(t => Node(t.Symbol!.Declarations[0])).ToArray();
                for (int i = 0; i < positions.Count; i++)
                    result[positions[i]] = parameters[i];
            }
            return result;
        }
        writer.WriteStartObject();
        writer.WriteStartArray("files"u8);
        foreach (var file in program.SourceFiles)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(file.Syntax.FileName);
            writer.WriteBooleanValue(file.Binding.IsModule);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("globals"u8);
        Table(environment.Globals);
        writer.WriteStartArray("patterns"u8);
        foreach (var pattern in environment.PatternModules)
        {
            writer.WriteStartArray();
            Name(pattern.Pattern);
            writer.WriteNumberValue(SymbolId(pattern.Symbol));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("augmentations"u8);
        Table(environment.PatternAugmentations);
        writer.WritePropertyName("augmentationTargets"u8);
        Table(environment.PatternTargets);
        writer.WriteStartArray("globalTypes"u8);
        foreach (var (name, type) in host.Globals.Types.OrderBy(p => p.Key, Utf8StringComparer.Ordinal))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(name.Span);
            writer.WriteNumberValue(TypeId(type));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("specialTypes"u8);
        foreach (var symbol in new[]
        {
            environment.UndefinedSymbol,
            environment.ArgumentsSymbol,
            environment.UnknownSymbol,
            environment.GlobalThisSymbol
        })
            writer.WriteNumberValue(TypeId(links.Values.Get(symbol).ResolvedType));
        writer.WriteNumberValue(TypeId(host.Globals.AnyArrayType));
        writer.WriteNumberValue(TypeId(host.Globals.AutoArrayType));
        writer.WriteNumberValue(TypeId(host.Globals.AnyReadonlyArrayType));
        writer.WriteEndArray();
        writer.WriteStartArray("declarations"u8);
        foreach (var node in nodes)
            if (environment.Binding(node)?.Get(node)?.Symbol is not null)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(Node(node));
                writer.WriteNumberValue(
                    SymbolId(
                    input.TryGetProperty("references"u8, out var referenceDeclarations) && referenceDeclarations.GetBoolean()
                    ? environment.Merger.GetMergedSymbol(environment.Binding(node)!.Get(node)!.Value.Symbol) : environment.Declaration(node)));
                writer.WriteEndArray();
            }
        writer.WriteEndArray();
        writer.WriteStartArray("classes"u8);
        foreach (var node in nodes.Where(
            n => n.Kind is SyntaxKind.ClassDeclaration or SyntaxKind.ClassExpression or SyntaxKind.InterfaceDeclaration))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Node(node));
            writer.WriteNumberValue(TypeId(await host.Scopes.ClassOrInterfaceAsync(environment.Declaration(node)!)));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("scopes"u8);
        foreach (var node in nodes.Where(n => n.Kind is SyntaxKind.TypeReference or SyntaxKind.ThisType or SyntaxKind.TypeParameter))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Node(node));
            TypeIds(OrderedInferences(await host.Scopes.OuterAsync(node)));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        if (input.TryGetProperty("aliases"u8, out var aliasOption) && aliasOption.GetBoolean())
        {
            writer.WriteStartArray("aliases"u8);
            var seenAliases = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
            foreach (var node in nodes)
            {
                var symbol = environment.Declaration(node);
                if (symbol is null || (symbol.Flags & SymbolFlags.Alias) == 0 || !seenAliases.Add(symbol))
                    continue;
                writer.WriteStartArray();
                writer.WriteNumberValue(SymbolId(symbol));
                writer.WriteNumberValue(SymbolId(await host.Aliases.ResolveAsync(symbol)));
                writer.WriteNumberValue(SymbolId(await host.Aliases.ImmediateAsync(symbol)));
                writer.WriteNumberValue((uint)await host.Aliases.FlagsAsync(symbol));
                writer.WriteNumberValue((uint)await host.Aliases.FlagsAsync(symbol, excludeTypeOnly: true));
                writer.WriteNumberValue((uint)await host.Aliases.FlagsAsync(symbol, excludeLocal: true));
                writer.WriteNumberValue(Node(await host.Aliases.TypeOnlyAsync(symbol)));
                writer.WriteNumberValue(Node(await host.Aliases.TypeOnlyAsync(symbol, SymbolFlags.Value)));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("symbolDisplay"u8, out var symbolDisplayOption) && symbolDisplayOption.GetBoolean())
            await CheckerSymbolDisplayTests.WriteAsync(writer, nodes, typeHost!, SymbolId, Node);
        if (input.TryGetProperty("typeSyntax"u8, out var typeSyntaxOption) && typeSyntaxOption.GetBoolean())
            await CheckerTypeSyntaxTests.WriteAsync(writer, nodes, typeHost!, Node,
                input.TryGetProperty("typeSyntaxFlags"u8, out var syntaxFlags)
                    ? syntaxFlags.EnumerateArray().Select(v => (NodeBuilderFlags)v.GetUInt32()).ToArray() : null);
        if (input.TryGetProperty("signatureSyntax"u8, out var signatureSyntaxOption) && signatureSyntaxOption.GetBoolean())
            await CheckerTypeSyntaxTests.WriteSignaturesAsync(writer, nodes, typeHost!, Node);
        if (input.TryGetProperty("emitQueries"u8, out var emitQueriesOption) && emitQueriesOption.GetBoolean())
            await CheckerEmitQueryTests.WriteAsync(writer, nodes, typeHost!, Node);
        if (input.TryGetProperty("emitReferences"u8, out var emitReferencesOption) && emitReferencesOption.GetBoolean())
            await CheckerEmitQueryTests.WriteReferencesAsync(writer, nodes, typeHost!, Node, SymbolId);
        if (input.TryGetProperty("emitSerialization"u8, out var emitSerializationOption) && emitSerializationOption.GetBoolean())
            await CheckerEmitQueryTests.WriteSerializationAsync(writer, nodes, typeHost!, Node);
        if (input.TryGetProperty("emitLinks"u8, out var emitLinksOption) && emitLinksOption.GetBoolean())
            await CheckerEmitQueryTests.WriteLinksAsync(writer, nodes, typeHost!, Node);
        if (input.TryGetProperty("emitJsx"u8, out var emitJsxOption) && emitJsxOption.GetBoolean())
            await CheckerEmitQueryTests.WriteJsxAsync(writer, nodes, typeHost!, Node);
        if (input.TryGetProperty("emitServices"u8, out var emitServicesOption) && emitServicesOption.GetBoolean())
            await CheckerEmitQueryTests.WriteServicesAsync(writer, nodes, typeHost!, Node);
        if (input.TryGetProperty("emitSyntax"u8, out var emitSyntaxOption) && emitSyntaxOption.GetBoolean())
            await CheckerEmitSyntaxTests.WriteAsync(writer, nodes, typeHost!, Node);
        if (input.TryGetProperty("emitRecovery"u8, out var emitRecoveryOption) && emitRecoveryOption.GetBoolean())
            await CheckerEmitSyntaxTests.WriteRecoveryAsync(writer, nodes, typeHost!, Node);
        if (input.TryGetProperty("symbolTypeNodes"u8, out var symbolTypeNodesOption) && symbolTypeNodesOption.GetBoolean())
            await CheckerSymbolDisplayTests.WriteAsync(writer, nodes, typeHost!, SymbolId, Node, typeNodes: true);
        if (input.TryGetProperty("symbolFormats"u8, out var symbolFormatsOption) && symbolFormatsOption.GetBoolean())
            await CheckerSymbolDisplayTests.WriteAsync(writer, nodes, typeHost!, SymbolId, Node, formats: true,
                formatFlags: input.TryGetProperty("symbolFormatFlags"u8, out var formatFlags)
                    ? formatFlags.EnumerateArray().Select(v => v.GetInt32()).ToArray()
                    : null);
        if (input.TryGetProperty("accessibility"u8, out var accessibilityOption) && accessibilityOption.GetBoolean())
            await CheckerAccessibilityTests.WriteAsync(writer, nodes, typeHost!, SymbolId, Node);
        if (input.TryGetProperty("symbolChains"u8, out var symbolChainOption) && symbolChainOption.GetBoolean())
            await CheckerSymbolChainTests.WriteAsync(writer, nodes, typeHost!, SymbolId, Node);
        if (input.TryGetProperty("declarationVisibility"u8, out var visibilityOption) && visibilityOption.GetBoolean())
            await CheckerVisibilityTests.WriteAsync(writer, nodes, typeHost!, SymbolId, Node);
        if (input.TryGetProperty("contextQueries"u8, out var contextQueryOption) && contextQueryOption.GetBoolean())
            await CheckerContextQueryTests.WriteAsync(writer, nodes, typeHost!, TypeId, SymbolId, Node);
        if (input.TryGetProperty("scopeServices"u8, out var servicesOption) && servicesOption.GetBoolean())
        {
            writer.WriteStartArray("serviceQueries"u8);
            var seenModules = new HashSet<Symbol>();
            var seenAliases = new HashSet<Symbol>();
            void OrderedSymbols(IReadOnlyList<Symbol> source)
            {
                writer.WriteStartArray();
                foreach (var symbol in source.OrderBy(s => CanonicalName(s.Name), Comparer<Utf8String>.Create(TypeOrder.CompareSymbolNames)))
                    writer.WriteNumberValue(SymbolId(symbol));
                writer.WriteEndArray();
            }
            foreach (var node in nodes)
            {
                if (SemanticSyntax.Source(node)?.FileName.StartsWith("/project/main."u8, StringComparison.Ordinal) != true)
                    continue;
                foreach (var meaning in new[]
                {
                    SymbolFlags.Value,
                    SymbolFlags.Type,
                    SymbolFlags.Namespace,
                    SymbolFlags.Alias,
                    SymbolFlags.All
                })
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(0);
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue((uint)meaning);
                    OrderedSymbols(await typeHost!.GetSymbolsInScopeAsync(node, meaning));
                    writer.WriteEndArray();
                }
                var symbol = await typeHost!.GetSymbolAtLocationAsync(node);
                if (symbol is not null)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(1);
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(SymbolId(symbol));
                    writer.WriteNumberValue(TypeId(await typeHost.GetTypeOfSymbolAtLocationAsync(symbol, node)));
                    writer.WriteNumberValue(TypeId(await typeHost.GetTypeOfSymbolAtLocationAsync(symbol, null)));
                    writer.WriteEndArray();
                    if ((symbol.Flags & SymbolFlags.Alias) != 0 && seenAliases.Add(symbol))
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(3);
                        writer.WriteNumberValue(Node(node));
                        writer.WriteNumberValue(SymbolId(symbol));
                        writer.WriteNumberValue(SymbolId(await typeHost.GetAliasedSymbolAsync(symbol)));
                        writer.WriteEndArray();
                    }
                }
                if (QuerySyntax.Declaration(node) && environment.Declaration(node) is { } module
                    && (module.Flags & SymbolFlags.Module) != 0 && seenModules.Add(module))
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(2);
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(SymbolId(module));
                    OrderedSymbols(await typeHost.GetExportsOfModuleAsync(module));
                    writer.WriteEndArray();
                }
                if (node is ExportSpecifierNode or ShorthandPropertyAssignmentNode)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(node is ExportSpecifierNode ? 4 : 5);
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(SymbolId(node is ExportSpecifierNode
                        ? await typeHost.GetExportSpecifierLocalTargetSymbolAsync(node) : await typeHost.GetShorthandAssignmentValueSymbolAsync(node)));
                    writer.WriteEndArray();
                }
                if (DeclarationOrder.ParameterProperty(node) && node is ParameterDeclarationNode { Name: IdentifierNode name } parameter)
                {
                    var pair = await typeHost!.GetSymbolsOfParameterPropertyDeclarationAsync(parameter, name.Text);
                    writer.WriteStartArray();
                    writer.WriteNumberValue(6);
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(SymbolId(pair.Parameter));
                    writer.WriteNumberValue(SymbolId(pair.Property));
                    writer.WriteEndArray();
                }
            }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("symbolLocations"u8, out var symbolLocationsOption) && symbolLocationsOption.GetBoolean())
        {
            writer.WriteStartArray("symbolLocationQueries"u8);
            foreach (var node in nodes)
                if (SemanticSyntax.Source(node)?.FileName.StartsWith("/project/main."u8, StringComparison.Ordinal) == true)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(SymbolId(await typeHost!.GetSymbolAtLocationAsync(node)));
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("documentationSymbols"u8, out var documentationSymbolsOption) && documentationSymbolsOption.GetBoolean())
        {
            writer.WriteStartArray("documentationSymbolQueries"u8);
            foreach (var owner in nodes)
                if (SemanticSyntax.Source(owner) is { } file && file.FileName.StartsWith("/project/main."u8, StringComparison.Ordinal))
                    foreach (var comment in await file.GetDocumentationAsync(owner))
                        foreach (var node in comment.DescendantsAndSelf())
                        {
                            writer.WriteStartArray();
                            writer.WriteNumberValue(Node(owner));
                            writer.WriteNumberValue((int)node.Kind);
                            writer.WriteNumberValue(node.Pos);
                            writer.WriteNumberValue(node.End);
                            writer.WriteNumberValue(SymbolId(await typeHost!.GetSymbolAtLocationAsync(node)));
                            writer.WriteEndArray();
                        }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("locations"u8, out var locationsOption) && locationsOption.GetBoolean())
        {
            writer.WriteStartArray("locationQueries"u8);
            foreach (var node in nodes)
                if (SemanticSyntax.Source(node)?.FileName.StartsWith("/project/main."u8, StringComparison.Ordinal) == true)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(TypeId(await typeHost!.GetTypeAtLocationAsync(node)));
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
        }
        if (typeHost is not null)
        {
            writer.WriteStartArray("typeQueries"u8);
            foreach (var node in nodes)
            {
                Type? result = null;
                if (node is TypeAliasDeclarationNode or EnumDeclarationNode)
                    result = await typeHost.Declared.GetAsync(environment.Declaration(node)!);
                else if (node is ITypedNode { Type: { } annotation }
                    && (node is IFunctionSignature
                        || node.Kind is SyntaxKind.VariableDeclaration or SyntaxKind.PropertyDeclaration or SyntaxKind.PropertySignature
                            or SyntaxKind.Parameter or SyntaxKind.IndexSignature))
                    result = await typeHost.GetTypeFromTypeNodeAsync(annotation);
                if (result is null)
                    continue;
                writer.WriteStartArray();
                writer.WriteNumberValue(Node(node));
                writer.WriteNumberValue(TypeId(result));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("access"u8, out var accessOption) && accessOption.GetBoolean())
        {
            writer.WriteStartArray("accessQueries"u8);
            foreach (var call in nodes.OfType<CallExpressionNode>())
                if (call.Expression is IdentifierNode { Text: { Span: var matchedText6 } } && matchedText6.SequenceEqual("__access"u8))
                    foreach (var argument in call.Arguments!)
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(Node(argument));
                        writer.WriteNumberValue(TypeId(await typeHost!.GetExpressionTypeAsync(argument)));
                        writer.WriteEndArray();
                    }
            writer.WriteEndArray();
            writer.WriteStartArray("accessSymbols"u8);
            foreach (var node in nodes.Where(n => n is PropertyAccessExpressionNode or ElementAccessExpressionNode or QualifiedNameNode))
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(Node(node));
                writer.WriteNumberValue(SymbolId(links.SymbolNodes.Get(node).ResolvedSymbol));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("accessSuggestions"u8);
            foreach (int code in host.ValueSuggestions.Concat(typeHost!.Suggestions).Order())
                writer.WriteNumberValue(code);
            writer.WriteEndArray();
            writer.WriteNumber("deferredAccessDiagnostics"u8, typeHost.DeferredMissingProperties.Count);
        }
        if (input.TryGetProperty("identifiers"u8, out var identifierOption) && identifierOption.GetBoolean())
        {
            writer.WriteStartArray("identifierQueries"u8);
            foreach (var call in nodes.OfType<CallExpressionNode>())
                if (call.Expression is IdentifierNode { Text: { Span: var matchedText7 } } && matchedText7.SequenceEqual("__expr"u8))
                    foreach (var argument in call.Arguments!)
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(Node(argument));
                        writer.WriteNumberValue(TypeId(await typeHost!.GetExpressionTypeAsync(argument)));
                        writer.WriteEndArray();
                    }
            writer.WriteEndArray();
            writer.WriteStartArray("identifierAliasReferences"u8);
            var seen = new HashSet<Symbol>();
            foreach (var node in nodes)
                if (environment.Declaration(node) is { } symbol && (symbol.Flags & SymbolFlags.Alias) != 0 && seen.Add(symbol))
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(SymbolId(symbol));
                    writer.WriteBooleanValue(links.Aliases.Get(symbol).Referenced);
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("flow"u8, out var flowOption) && flowOption.GetBoolean())
        {
            writer.WriteStartArray("flowQueries"u8);
            foreach (var call in nodes.OfType<CallExpressionNode>())
                if (call.Expression is IdentifierNode { Text: { Span: var matchedText8 } } && matchedText8.SequenceEqual("__flow"u8))
                    foreach (var argument in call.Arguments!.OfType<IdentifierNode>())
                    {
                        var declared = await typeHost!.Values.GetAsync(host.ReferenceSymbols.Resolve(argument));
                        writer.WriteStartArray();
                        writer.WriteNumberValue(Node(argument));
                        writer.WriteNumberValue(TypeId(declared));
                        writer.WriteNumberValue(TypeId(await typeHost.FlowTypes.GetAsync(argument, declared)));
                        var flowNode = typeHost.FlowOf(argument);
                        writer.WriteBooleanValue(flowNode is null || await typeHost.FlowTypes.Reachability.ReachableAsync(flowNode));
                        writer.WriteEndArray();
                    }
            writer.WriteEndArray();
            writer.WriteStartArray("assignmentMarks"u8);
            foreach (var declaration in nodes.Where(n => n is VariableDeclarationNode or ParameterDeclarationNode))
                if (environment.Declaration(declaration) is { } symbol && typeHost!.Assignments.ParameterOrMutableLocal(symbol))
                {
                    var mark = await typeHost.Assignments.GetAsync(symbol);
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Node(declaration));
                    writer.WriteNumberValue(mark.LastPosition);
                    writer.WriteBooleanValue(mark.Definite);
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
            writer.WriteStartArray("flowState"u8);
            writer.WriteNumberValue(typeHost!.FlowTypes.LoopCacheCount);
            writer.WriteNumberValue(typeHost.FlowTypes.ActiveLoopCount);
            writer.WriteNumberValue(typeHost.FlowTypes.SharedCount);
            writer.WriteBooleanValue(typeHost.FlowTypes.AnalysisDisabled);
            writer.WriteNumberValue(typeHost.FlowTypes.Reachability.ReachableCacheCount);
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("references"u8, out var referenceOption) && referenceOption.GetBoolean())
        {
            writer.WriteStartArray("declarationOrder"u8);
            foreach (var call in nodes.OfType<CallExpressionNode>())
                if (call.Expression is IdentifierNode { Text: { Span: var matchedText9 } } && matchedText9.SequenceEqual("__order"u8))
                    foreach (var access in call.Arguments!.OfType<PropertyAccessExpressionNode>())
                    {
                        var owner = DeclarationOrder.ContainingClass(access)!;
                        var declaration = owner.DescendantsAndSelf().First(n =>
                            (n is PropertyDeclarationNode or MethodDeclarationNode || DeclarationOrder.ParameterProperty(n))
                            && SemanticSyntax.Name(n) is IdentifierNode name && name.Text == ((IdentifierNode)access.Name!).Text);
                        writer.WriteStartArray();
                        writer.WriteNumberValue(Node(access));
                        writer.WriteNumberValue(Node(declaration));
                        writer.WriteBooleanValue(await host.DeclarationOrder.BeforeUseAsync(declaration, access.Name!));
                        writer.WriteEndArray();
                    }
            writer.WriteEndArray();
            writer.WriteStartArray("referenceSyntax"u8);
            foreach (var node in nodes)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(Node(node));
                writer.WriteBooleanValue(ReferenceSyntax.IsExpression(node));
                writer.WriteBooleanValue(ReferenceSyntax.ValidTypeOnlyUse(node));
                writer.WriteNumberValue(ReferenceSyntax.AccessKind(node));
                writer.WriteNumberValue(Node(ReferenceSyntax.AssignmentTarget(node)));
                writer.WriteNumberValue(ReferenceSyntax.AssignmentKind(node));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("referenceQueries"u8);
            foreach (var call in nodes.OfType<CallExpressionNode>())
                if (call.Expression is IdentifierNode { Text: { Span: var matchedText10 } } && matchedText10.SequenceEqual("__use"u8))
                    foreach (var argument in call.Arguments!.OfType<IdentifierNode>())
                    {
                        var symbol = host.ReferenceSymbols.Resolve(argument);
                        writer.WriteStartArray();
                        writer.WriteNumberValue(Node(argument));
                        writer.WriteNumberValue(SymbolId(symbol));
                        writer.WriteEndArray();
                    }
            writer.WriteEndArray();
            writer.WriteStartArray("referenceSuggestions"u8);
            foreach (int code in host.ValueSuggestions.Order())
                writer.WriteNumberValue(code);
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("awaited"u8, out var awaitedOption) && awaitedOption.GetBoolean())
        {
            writer.WriteStartArray("awaitedQueries"u8);
            foreach (var node in nodes)
                if (node is TypeAliasDeclarationNode or InterfaceDeclarationNode && node is INamedNode { Name: IdentifierNode name }
                    && name.Text.Length > 1 && name.Text[0] == 'A' && Utf8Ascii.IsDigit(name.Text[1]))
                {
                    var type = await typeHost!.Declared.GetAsync(environment.Declaration(node)!);
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(TypeId(type));
                    writer.WriteNumberValue(TypeId((await typeHost.Awaited.PromisedAsync(type)).Type));
                    writer.WriteNumberValue(TypeId(await typeHost.Awaited.NoAliasAsync(type)));
                    writer.WriteNumberValue(TypeId(await typeHost.Awaited.GetAsync(type)));
                    writer.WriteBooleanValue(await typeHost.Awaited.NeededAsync(type));
                    writer.WriteBooleanValue(await typeHost.Awaited.ThenableAsync(type));
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("expressions"u8, out var expressionOption) && expressionOption.GetBoolean())
        {
            writer.WriteStartArray("expressionQueries"u8);
            foreach (var declaration in nodes.OfType<VariableDeclarationNode>())
                if (declaration.Initializer is { } expression)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Node(expression));
                    writer.WriteNumberValue(TypeId(await typeHost!.GetExpressionTypeAsync(expression)));
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
            writer.WriteStartArray("suggestions"u8);
            foreach (int code in typeHost!.Suggestions.Order())
                writer.WriteNumberValue(code);
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("constants"u8, out var constantOption) && constantOption.GetBoolean())
        {
            writer.WriteStartArray("constantQueries"u8);
            foreach (var member in nodes.OfType<EnumMemberNode>())
            {
                var result = await typeHost!.EnumValues.GetAsync(member);
                writer.WriteStartArray();
                writer.WriteNumberValue(Node(member));
                if (result.Value is null)
                    writer.WriteNullValue();
                else
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(result.Value is Utf8String ? Utf8String.Copy("string"u8) : Utf8String.Copy("number"u8));
                    if (result.Value is Utf8String text)
                        writer.WriteBase64StringValue(text.Span.ToArray());
                    else
                        writer.WriteStringValue(
                            BitConverter.DoubleToUInt64Bits((double)result.Value).ToString("x16", CultureInfo.InvariantCulture));
                    writer.WriteEndArray();
                }
                writer.WriteBooleanValue(result.IsSyntacticallyString);
                writer.WriteBooleanValue(result.ResolvedOtherFiles);
                writer.WriteBooleanValue(result.HasExternalReferences);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("indexing"u8, out var indexingOption) && indexingOption.GetBoolean())
            await CheckerIndexTests.WriteAsync(writer, nodes, typeHost!, TypeId, Node);
        if (input.TryGetProperty("members"u8, out var memberOption) && memberOption.GetBoolean())
            await CheckerMemberTests.WriteAsync(writer, nodes, environment, typeHost!, TypeId, SymbolId, Node,
                input.TryGetProperty("values"u8, out var valueOption) && valueOption.GetBoolean(),
                input.TryGetProperty("signatures"u8, out var signatureOption) && signatureOption.GetBoolean(),
                input.TryGetProperty("calls"u8, out var callOption) && callOption.GetBoolean());
        if (input.TryGetProperty("properties"u8, out var propertyOption) && propertyOption.GetBoolean())
            await CheckerPropertyTests.WriteAsync(writer, nodes, environment, typeHost!, TypeId, SymbolId, Node);
        if (input.TryGetProperty("identity"u8, out var identityOption) && identityOption.GetBoolean())
            await CheckerRelationTests.WriteAsync(writer, nodes, environment, typeHost!, TypeId, Node,
                input.TryGetProperty("assignability"u8, out var assignabilityOption) && assignabilityOption.GetBoolean());
        if (input.TryGetProperty("assertions"u8, out var assertionOption) && assertionOption.GetBoolean())
            foreach (var assertion in typeHost!.Assertions.CheckedNodes.OrderBy(Node).ToArray())
                await typeHost.Assertions.DeferredAsync(assertion);
        var variableQueries = typeHost is not null
            && input.TryGetProperty("awaited"u8, out var classifyVariables)
            && classifyVariables.GetBoolean()
            ? new TypeVariables(typeHost.References.TypeArgumentsAsync) : null;
        writer.WriteStartArray("types"u8);
        for (int i = 0; i < types.Count; i++)
        {
            var type = types[i];
            var parameter = type as TypeParameter;
            var intf = type as InterfaceType;
            if (typeHost is not null && type is TypeReference lazy && (type.ObjectFlags & ObjectFlags.Reference) != 0)
                await typeHost.References.TypeArgumentsAsync(lazy);
            bool containsVariables = variableQueries is not null && await variableQueries.CouldContainAsync(type);
            writer.WriteStartArray();
            writer.WriteNumberValue((uint)type.Flags);
            writer.WriteNumberValue((uint)type.ObjectFlags);
            writer.WriteNumberValue(SymbolId(type.Symbol));
            writer.WriteNumberValue(TypeId(type is ObjectType obj ? obj.Target : parameter?.Target));
            TypeIds(type is TypeReference reference ? reference.ResolvedTypeArguments : null);
            TypeIds(intf?.AllTypeParameters);
            writer.WriteNumberValue(intf?.OuterTypeParameterCount ?? 0);
            writer.WriteNumberValue(TypeId(intf?.ThisType));
            writer.WriteBooleanValue(parameter?.IsThisType ?? false);
            writer.WriteNumberValue(TypeId(parameter?.Constraint));
            writer.WriteStringValue((type is IntrinsicType intrinsic ? intrinsic.IntrinsicName : Utf8String.Empty).Span);
            if (typeHost is not null)
            {
                writer.WriteStartObject();
                if (variableQueries is not null)
                    writer.WriteBoolean("couldContainTypeVariables"u8, containsVariables);
                if (type.Alias is { } typeAlias)
                {
                    writer.WriteStartArray("alias"u8);
                    writer.WriteNumberValue(SymbolId(typeAlias.Symbol));
                    TypeIds(typeAlias.TypeArguments);
                    writer.WriteEndArray();
                }
                if (type is ConstrainedType constrained)
                    writer.WriteNumber("baseConstraint"u8, TypeId(constrained.ResolvedBaseConstraint));
                switch (type)
                {
                    case LiteralType literal:
                        writer.WriteNumber("fresh"u8, TypeId(literal.FreshType));
                        writer.WriteNumber("regular"u8, TypeId(literal.RegularType));
                        writer.WritePropertyName("value"u8);
                        switch (literal.Value)
                        {
                            case Utf8String value:
                                writer.WriteBase64StringValue(value.Span.ToArray());
                                break;
                            case double value:
                                writer.WriteStringValue(
                                    BitConverter.DoubleToUInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture));
                                break;
                            case BigInteger value:
                                writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
                                break;
                            case bool value:
                                writer.WriteBooleanValue(value);
                                break;
                            default:
                                writer.WriteNullValue();
                                break;
                        }
                        break;
                    case UnionOrIntersectionType composite:
                        writer.WritePropertyName("parts"u8);
                        TypeIds(composite.Types);
                        if (type is UnionType union)
                            writer.WriteNumber("origin"u8, TypeId(union.Origin));
                        break;
                    case TypeParameter:
                        writer.WriteBoolean("distributed"u8, parameter!.IsDistributed);
                        writer.WriteNumber("default"u8, TypeId(parameter.ResolvedDefaultType));
                        writer.WriteNumber("distributedType"u8, TypeId(parameter.DistributedType));
                        break;
                    case IndexType index:
                        writer.WriteNumber("target"u8, TypeId(index.Target));
                        writer.WriteNumber("indexFlags"u8, (uint)index.IndexFlags);
                        break;
                    case IndexedAccessType indexed:
                        writer.WriteNumber("object"u8, TypeId(indexed.ObjectType));
                        writer.WriteNumber("index"u8, TypeId(indexed.IndexType));
                        writer.WriteNumber("accessFlags"u8, (uint)indexed.AccessFlags);
                        break;
                    case TemplateLiteralType template:
                        writer.WriteStartArray("texts"u8);
                        foreach (Utf8String text in template.Texts)
                            writer.WriteBase64StringValue(text.Span.ToArray());
                        writer.WriteEndArray();
                        writer.WritePropertyName("parts"u8);
                        TypeIds(template.Types);
                        break;
                    case StringMappingType mapping:
                        writer.WriteNumber("target"u8, TypeId(mapping.Target));
                        break;
                    case SubstitutionType substitution:
                        writer.WriteNumber("base"u8, TypeId(substitution.BaseType));
                        writer.WriteNumber("constraint"u8, TypeId(substitution.Constraint));
                        break;
                    case ConditionalType conditional:
                        writer.WriteStartArray("root"u8);
                        writer.WriteNumberValue(Node(conditional.Root.Node));
                        writer.WriteNumberValue(TypeId(conditional.Root.CheckType));
                        writer.WriteNumberValue(TypeId(conditional.Root.ExtendsType));
                        writer.WriteBooleanValue(conditional.Root.IsDistributive);
                        TypeIds(OrderedInferences(conditional.Root.OuterTypeParameters));
                        TypeIds(OrderedInferences(conditional.Root.InferTypeParameters));
                        writer.WriteEndArray();
                        writer.WriteNumber("check"u8, TypeId(conditional.CheckType));
                        writer.WriteNumber("extends"u8, TypeId(conditional.ExtendsType));
                        writer.WriteNumber("true"u8, TypeId(conditional.ResolvedTrueType));
                        writer.WriteNumber("false"u8, TypeId(conditional.ResolvedFalseType));
                        writer.WriteNumber("inferredTrue"u8, TypeId(conditional.ResolvedInferredTrueType));
                        writer.WriteNumber("defaultConstraint"u8, TypeId(conditional.ResolvedDefaultConstraint));
                        writer.WriteNumber("distributiveConstraint"u8, TypeId(conditional.ResolvedConstraintOfDistributive));
                        break;
                }
                if (type is TypeReference referenceType)
                    writer.WriteNumber("node"u8, Node(referenceType.Node));
                if (type is InstantiationExpressionType instantiatedExpression)
                    writer.WriteNumber("node"u8, Node(instantiatedExpression.Node));
                if (type is TupleType tuple)
                {
                    writer.WriteStartArray("tuple"u8);
                    writer.WriteStartArray();
                    foreach (var info in tuple.ElementInfos)
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue((uint)info.Flags);
                        writer.WriteNumberValue(Node(info.LabeledDeclaration));
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                    writer.WriteNumberValue(tuple.MinLength);
                    writer.WriteNumberValue(tuple.FixedLength);
                    writer.WriteNumberValue((uint)tuple.CombinedFlags);
                    writer.WriteBooleanValue(tuple.IsReadonly);
                    writer.WriteEndArray();
                }
                if (type is MappedType mapped)
                {
                    writer.WriteNumber("parameter"u8, TypeId(mapped.TypeParameter));
                    writer.WriteNumber("constraint"u8, TypeId(mapped.ConstraintType));
                    writer.WriteNumber("template"u8, TypeId(mapped.TemplateType));
                    writer.WriteNumber("name"u8, TypeId(mapped.NameType));
                }
                if (type is ReverseMappedType reverse)
                {
                    writer.WriteStartArray("reverse"u8);
                    writer.WriteNumberValue(TypeId(reverse.Source));
                    writer.WriteNumberValue(TypeId(reverse.MappedType));
                    writer.WriteNumberValue(TypeId(reverse.ConstraintType));
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        if (input.TryGetProperty("access"u8, out var accessState) && accessState.GetBoolean())
        {
            writer.WriteStartArray("privateReferences"u8);
            for (int i = 0; i < symbols.Count; i++)
            {
                var symbol = symbols[i];
                if (symbol.ValueDeclaration is not { } declaration
                    || !(SemanticSyntax.HasModifier(declaration, SyntaxKind.PrivateKeyword)
                        || SemanticSyntax.Name(declaration) is PrivateIdentifierNode))
                    continue;
                writer.WriteStartArray();
                writer.WriteNumberValue(i + 1);
                writer.WriteNumberValue((uint)environment.ReferenceKinds(symbol));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        writer.WriteStartArray("symbols"u8);
        for (int i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            writer.WriteStartArray();
            Name(symbol.Name);
            writer.WriteNumberValue((uint)symbol.Flags);
            writer.WriteNumberValue((uint)symbol.CheckFlags);
            writer.WriteNumberValue(SymbolId(symbol.Parent));
            writer.WriteStartArray();
            foreach (var declaration in symbol.Declarations)
                writer.WriteNumberValue(Node(declaration));
            writer.WriteEndArray();
            writer.WriteNumberValue(Node(symbol.ValueDeclaration));
            Table(symbol.Members);
            Table(symbol.Exports);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("diagnostics"u8);
        IEnumerable<DiagnosticCode> diagnostics = host.Diagnostics;
        if (typeHost is not null)
            diagnostics = diagnostics.Concat(typeHost.Diagnostics).Concat(typeHost.Instantiation.Diagnostics)
                .Concat(typeHost.Instantiation.ConstraintDiagnostics).Concat(typeHost.AlgebraDiagnostics);
        foreach (int code in diagnostics.Order())
            writer.WriteNumberValue(code);
        writer.WriteEndArray();
        if (identifierOption.ValueKind == JsonValueKind.True)
        {
            writer.WriteStartArray("assignmentHints"u8);
            foreach (DiagnosticCode code in typeHost!.AssignmentHints.Select(
                h => h.Construct
                    ? DiagnosticCode.DidYouMeanToUseNewWithThisExpression
                    : DiagnosticCode.DidYouMeanToCallThisExpression).Order())
                writer.WriteNumberValue((int)code);
            writer.WriteEndArray();
            writer.WriteStartArray("identifierSuggestions"u8);
            foreach (int code in host.ValueSuggestions.Concat(typeHost!.Suggestions).Order())
                writer.WriteNumberValue(code);
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("identifiers"u8, out var instantiationOption) && instantiationOption.GetBoolean())
        {
            writer.WriteStartArray("instantiationErrors"u8);
            foreach (var error in typeHost!.InstantiationErrors.OrderBy(p => Node(p.Key)))
                writer.WriteStringValue(error.Value.Span);
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("numberStrings"u8, out var numberStrings))
        {
            writer.WriteStartArray("numberStrings"u8);
            foreach (var item in numberStrings.EnumerateArray())
            {
                double number = TypeScript.Compiler.Semantics.JsNumber.FromString(item.GetBytesFromBase64());
                writer.WriteStringValue(
                    double.IsNaN(number) ? "nan" : BitConverter.DoubleToUInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture));
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}
