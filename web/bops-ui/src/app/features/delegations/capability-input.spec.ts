// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import type { InputParameter } from '../../core/api/models';
import type { MessageKey } from '../../core/i18n/messages';
import { FieldValue, buildInput, emptyValues, findDuplicateKey, isChoice, parseRaw, rawProblems, valuesFrom } from './capability-input';

function p(name: string, type: string, extra: Partial<InputParameter> = {}): InputParameter {
  return {
    name, type, description: '', required: false, sensitive: false, allowedValues: null,
    minimum: null, maximum: null, minLength: null, maxLength: null, minItems: null, maxItems: null, ...extra,
  };
}

/** One parameter of each of the eight ToolParameterType values, with the HARDEN-6 constraints each may carry. */
const schema: InputParameter[] = [
  p('name', 'String', { required: true, minLength: 2, maxLength: 5 }),
  p('count', 'Integer', { minimum: 1, maximum: 10 }),
  p('ratio', 'Number', { minimum: 0.5, maximum: 1.5 }),
  p('force', 'Boolean'),
  p('path', 'Path', { maxLength: 8 }),
  p('wait', 'Duration'),
  p('mode', 'Enum', { allowedValues: ['fast', 'safe'] }),
  p('paths', 'PathList', { minItems: 1, maxItems: 2 }),
];

describe('capability input', () => {
  it('starts every field empty and omits empty optional fields from the object', () => {
    const values = emptyValues(schema);
    values['name'] = 'abc';

    const { input, problems } = buildInput(schema, values);

    expect(problems).toEqual([]);
    expect(input).toEqual({ name: 'abc' });
    expect(values['force']).toBeNull();
    expect(values['paths']).toEqual([]);
  });

  it('builds each type with its JSON type, never coercing a value into range', () => {
    const values = {
      ...emptyValues(schema), name: 'abcde', count: '10', ratio: '0.5', force: false, path: '/var/log', wait: '00:05:00', mode: 'safe', paths: ['/a', '/b'],
    };

    expect(buildInput(schema, values)).toEqual({
      input: { name: 'abcde', count: 10, ratio: 0.5, force: false, path: '/var/log', wait: '00:05:00', mode: 'safe', paths: ['/a', '/b'] },
      problems: [],
    });
  });

  const cases: [string, unknown, MessageKey][] = [
    ['name', '', 'delegations.input.required'],
    ['name', 'a', 'delegations.input.minLength'],
    ['name', 'abcdef', 'delegations.input.maxLength'],
    ['count', '1.5', 'delegations.input.wholeNumber'],
    ['count', '0', 'delegations.input.minimum'],
    ['count', '11', 'delegations.input.maximum'],
    ['count', '3000000000', 'delegations.input.wholeNumber'],
    ['ratio', 'abc', 'delegations.input.number'],
    ['ratio', 'Infinity', 'delegations.input.number'],
    ['ratio', '1.51', 'delegations.input.maximum'],
    ['path', '123456789', 'delegations.input.maxLength'],
    ['mode', 'FAST', 'delegations.input.notAllowed'],
    ['paths', ['/a', ''], 'delegations.input.emptyEntry'],
    ['paths', ['/a', '/b', '/c'], 'delegations.input.maxItems'],
  ];
  for (const [name, value, key] of cases) {
    it(`reports ${key} for ${name} = ${JSON.stringify(value)}`, () => {
      const values = { ...emptyValues(schema), name: 'abc', [name]: value } as unknown as Record<string, FieldValue>;

      const { problems } = buildInput(schema, values);

      expect(problems.map((x) => x.key)).toContain(key);
      expect(problems.find((x) => x.key === key)!.parameter).toBe(name);
    });
  }

  it('never puts a value in a problem, only the parameter and the bound', () => {
    const secret = p('password', 'String', { sensitive: true, minLength: 12 });

    const { problems } = buildInput([secret], { password: 'hunter2' });

    expect(JSON.stringify(problems)).not.toContain('hunter2');
    expect(problems[0].params).toEqual({ name: 'password', count: 12 });
  });

  it('treats a String with allowed values as a choice, like an Enum', () => {
    expect(isChoice(p('a', 'Enum', { allowedValues: ['x'] }))).toBeTrue();
    expect(isChoice(p('a', 'String', { allowedValues: ['x'] }))).toBeTrue();
    expect(isChoice(p('a', 'String'))).toBeFalse();
  });

  describe('duplicate keys', () => {
    it('finds a key repeated at the top level or at any depth, before JSON.parse can erase it', () => {
      expect(findDuplicateKey('{"a":1,"a":2}')).toBe('a');
      expect(findDuplicateKey('{"x":{"b":1,"c":{"d":1,"d":2}}}')).toBe('d');
      expect(findDuplicateKey('[{"a":1},{"a":2}]')).toBeNull();
      expect(findDuplicateKey('{"a":{"a":1}}')).toBeNull();
      expect(findDuplicateKey('{"a":"\\"a\\":1","b":2}')).toBeNull();
      expect(findDuplicateKey('{"a":1,"\\u0061":2}')).toBe('a');
      expect(findDuplicateKey('{"a":[1,{"a":2}],"b":3}')).toBeNull();
    });

    it('makes the raw editor refuse a duplicate, invalid JSON, a non-object and an unknown field', () => {
      expect(parseRaw('{"name":"a","name":"b"}', schema)).toEqual({ problem: jasmine.objectContaining({ key: 'delegations.input.duplicateKey', params: { name: 'name' } }) });
      expect(parseRaw('{"name":', schema)).toEqual({ problem: jasmine.objectContaining({ key: 'delegations.input.notJson' }) });
      expect(parseRaw('[1]', schema)).toEqual({ problem: jasmine.objectContaining({ key: 'delegations.input.notObject' }) });
      expect(parseRaw('{"name":"abc","other":1}', schema)).toEqual({ problem: jasmine.objectContaining({ key: 'delegations.input.unknownField' }) });
      expect(parseRaw('{"name":"abc"}', schema)).toEqual({ input: { name: 'abc' } });
    });

    it('applies the same checks to a raw object as to the form', () => {
      expect(rawProblems(schema, { name: 'abc', count: 1.5 }).map((x) => x.key)).toEqual(['delegations.input.wholeNumber']);
      expect(rawProblems(schema, { name: 'abc', count: '3' }).map((x) => x.key)).toEqual(['delegations.input.wrongType']);
      expect(rawProblems(schema, { count: 3 }).map((x) => x.key)).toEqual(['delegations.input.required']);
      expect(rawProblems(schema, { name: null }).map((x) => x.key)).toEqual(['delegations.input.required']);
      expect(rawProblems(schema, { name: 'abc', paths: ['/a'] })).toEqual([]);
    });

    it('maps an object back to the form only when it can show it exactly', () => {
      expect(valuesFrom(schema, { name: 'abc', count: 2, force: true, paths: ['/a'] })).toEqual({
        values: { ...emptyValues(schema), name: 'abc', count: '2', force: true, paths: ['/a'] },
      });
      expect('problem' in valuesFrom(schema, { force: 'yes' })).toBeTrue();
    });
  });
});
