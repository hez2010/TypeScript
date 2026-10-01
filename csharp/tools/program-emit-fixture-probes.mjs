import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {sha256} from './common.mjs';

export function fixtureCorrectionProbe(item, policy) {
    const source = Object.values(item.input.files).join('\n');
    const script = output => Buffer.from(output.writes.find(write => write.path.endsWith('.js')).textBase64, 'base64').toString('utf8');
    let native, reference = script(item.reference), candidate = script(item.candidate), observation, expected;
    if (policy === 'preserve-async-generator-parameter-scope') {
        const method = text => {
            const match = text.match(/^let fooAsyncGenM[\s\S]*?^};/m);
            assert.ok(match, 'The compiler fixture must contain the original async generator method');
            return match[0];
        };
        const helper = text => {
            const match = text.match(/^var __asyncGenerator =[\s\S]*?^};/m);
            const awaitMatch = text.match(/^var __await =[\s\S]*?^};/m);
            assert.ok(match, 'The emitted fixture must contain the async generator helper');
            assert.ok(awaitMatch, 'The emitted fixture must contain the await helper');
            return awaitMatch[0] + '\n' + match[0];
        };
        native = method(source).replace(': FooAsyncGenMethod', '');
        reference = helper(reference) + '\n' + method(reference);
        candidate = helper(candidate) + '\n' + method(candidate);
        observation = `const values = []; await fooAsyncGenM.method('num', value => values.push(value)).next();
await fooAsyncGenM.method('str', value => values.push(value)).next(); console.log(JSON.stringify(values));`;
        expected = [123, 'abc'];
        assert.equal(candidate.replace('function* method_1(type, cb)', 'function* method_1()'), reference);
    } else if (policy === 'preserve-inferred-binding-names') {
        native = source.replace(/\bpublic /g, '');
        observation = `console.log(JSON.stringify([ClassExpression.name, ClassExpressionStatic.name,
new ClassExpression().value, new ClassExpressionStatic().exposed]));`;
        expected = ['ClassExpression', 'ClassExpressionStatic', 1, 'visible'];
    } else throw Error(`Unknown compiler fixture correction: ${policy}`);
    const execute = text => JSON.parse(execFileSync(process.execPath, ['--input-type=module'],
        {input: text + '\n' + observation, encoding: 'utf8', windowsHide: true, timeout: 10000}));
    const result = {policy, engine: process.version, native, reference, candidate, observation,
        nativeResult: execute(native), referenceResult: execute(reference), candidateResult: execute(candidate)};
    assert.deepEqual(result.nativeResult, expected);
    assert.deepEqual(result.candidateResult, expected);
    if (policy === 'preserve-inferred-binding-names')
        assert.deepEqual(result.referenceResult, ['_a', 'ClassExpressionStatic', 1, 'visible']);
    else assert.deepEqual(result.referenceResult, expected);
    return {...result, hash: sha256(JSON.stringify(result))};
}
