import { LanguageVariant } from "#enums/languageVariant";
import type { CommentKind, CommentRange, Scanner } from "./scanner.utf16.ts";
import * as utf16 from "./scanner.utf16.ts";
import { Utf8Positions } from "./positions.ts";

export * from "./scanner.utf16.ts";

export function computeLineStarts(text: string): number[] {
    const positions = new Utf8Positions(text);
    return utf16.computeLineStarts(text).map(position => positions.toUtf8(position));
}

export function couldStartTrivia(text: string, pos: number): boolean {
    return utf16.couldStartTrivia(text, new Utf8Positions(text).toUtf16(pos));
}

export function skipTrivia(text: string, pos: number, stopAfterLineBreak?: boolean, stopAtComments?: boolean, inJSDoc?: boolean,
    positions: Utf8Positions = new Utf8Positions(text)): number {
    return positions.toUtf8(utf16.skipTrivia(text, positions.toUtf16(pos), stopAfterLineBreak, stopAtComments, inJSDoc));
}

export function getLeadingCommentRanges(text: string, pos: number): CommentRange[] | undefined {
    const positions = new Utf8Positions(text);
    return utf16.getLeadingCommentRanges(text, positions.toUtf16(pos))?.map(range =>
        ({ ...range, pos: positions.toUtf8(range.pos), end: positions.toUtf8(range.end) }));
}

export function getTrailingCommentRanges(text: string, pos: number): CommentRange[] | undefined {
    const positions = new Utf8Positions(text);
    return utf16.getTrailingCommentRanges(text, positions.toUtf16(pos))?.map(range =>
        ({ ...range, pos: positions.toUtf8(range.pos), end: positions.toUtf8(range.end) }));
}

type CommentCallback<T, U> = (pos: number, end: number, kind: CommentKind, hasTrailingNewLine: boolean, state: T, accumulator?: U) => U;

export function forEachLeadingCommentRange<T, U>(text: string, pos: number, cb: CommentCallback<T, U>, state: T): U | undefined;
export function forEachLeadingCommentRange<U>(text: string, pos: number, cb: (pos: number, end: number, kind: CommentKind, hasTrailingNewLine: boolean) => U): U | undefined;
export function forEachLeadingCommentRange<T, U>(text: string, pos: number, cb: CommentCallback<T, U>, state?: T): U | undefined {
    const positions = new Utf8Positions(text);
    return utf16.forEachLeadingCommentRange(text, positions.toUtf16(pos),
        (start, end, kind, line, state) => cb(positions.toUtf8(start), positions.toUtf8(end), kind, line, state), state!);
}

export function forEachTrailingCommentRange<T, U>(text: string, pos: number, cb: CommentCallback<T, U>, state: T): U | undefined;
export function forEachTrailingCommentRange<U>(text: string, pos: number, cb: (pos: number, end: number, kind: CommentKind, hasTrailingNewLine: boolean) => U): U | undefined;
export function forEachTrailingCommentRange<T, U>(text: string, pos: number, cb: CommentCallback<T, U>, state?: T): U | undefined {
    const positions = new Utf8Positions(text);
    return utf16.forEachTrailingCommentRange(text, positions.toUtf16(pos),
        (start, end, kind, line, state) => cb(positions.toUtf8(start), positions.toUtf8(end), kind, line, state), state!);
}

export function reduceEachLeadingCommentRange<T, U>(text: string, pos: number, cb: CommentCallback<T, U>, state: T, initial: U): U | undefined {
    const positions = new Utf8Positions(text);
    return utf16.reduceEachLeadingCommentRange(text, positions.toUtf16(pos),
        (start, end, kind, line, state, accumulator?: U) => cb(positions.toUtf8(start), positions.toUtf8(end), kind, line, state, accumulator), state, initial);
}

export function reduceEachTrailingCommentRange<T, U>(text: string, pos: number, cb: CommentCallback<T, U>, state: T, initial: U): U | undefined {
    const positions = new Utf8Positions(text);
    return utf16.reduceEachTrailingCommentRange(text, positions.toUtf16(pos),
        (start, end, kind, line, state, accumulator?: U) => cb(positions.toUtf8(start), positions.toUtf8(end), kind, line, state, accumulator), state, initial);
}

/** Uses the reference scanner's token algorithms with byte coordinates at its boundary. */
export function createScanner(skipTrivia: boolean, languageVariant: LanguageVariant = LanguageVariant.Standard,
    textInitial?: string, start?: number, length?: number, sourcePositions?: Utf8Positions): Scanner {
    let positions = sourcePositions ?? new Utf8Positions(textInitial ?? "");
    const utf16Start = positions.toUtf16(start ?? 0);
    const scanner = utf16.createScanner(skipTrivia, languageVariant, textInitial, utf16Start,
        length === undefined ? undefined : positions.toUtf16((start ?? 0) + length) - utf16Start);
    return {
        ...scanner,
        getTokenFullStart: () => positions.toUtf8(scanner.getTokenFullStart()),
        getTokenStart: () => positions.toUtf8(scanner.getTokenStart()),
        getTokenEnd: () => positions.toUtf8(scanner.getTokenEnd()),
        getCommentDirectives: () => scanner.getCommentDirectives()?.map(directive => ({ ...directive,
            range: { pos: positions.toUtf8(directive.range.pos), end: positions.toUtf8(directive.range.end) } })),
        resetTokenState: position => scanner.resetTokenState(positions.toUtf16(position)),
        setText(text, start, length) {
            if (positions.text !== (text ?? "")) positions = new Utf8Positions(text ?? "");
            const from = positions.toUtf16(start ?? 0);
            scanner.setText(text, from, length === undefined ? undefined : positions.toUtf16((start ?? 0) + length) - from);
        },
        scanRange(start, length, callback) {
            const from = positions.toUtf16(start);
            return scanner.scanRange(from, positions.toUtf16(start + length) - from, callback);
        },
    };
}
