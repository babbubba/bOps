// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import type { InputParameter } from '../../core/api/models';
import type { MessageKey, MessageParams } from '../../core/i18n/messages';

/**
 * The Capability input form of the Delegations page (ADR-0044 §9.1, §9.3), as pure functions over a Capability's
 * `inputSchema` — the only schema there is. Every check here is a convenience for the person filling the form: the server
 * validates the same input again and is the authority. Nothing is coerced, clamped, trimmed or guessed to make a value pass, an
 * optional field left empty is omitted (never sent as `""` or `null`), and a message names the parameter and the constraint,
 * never a value.
 */

/** What a field holds while it is edited: text for the text-like types, a tri-state for Boolean, a list for PathList. */
export type FieldValue = string | boolean | null | string[];

/** One problem with the form, as a message key and its parameters (the parameter name, a bound — never a value). */
export interface InputProblem {
  parameter: string | null;
  key: MessageKey;
  params: MessageParams;
}

const WHOLE_NUMBER = /^-?\d+$/;
const NUMBER = /^-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?$/;
const INT_MIN = -2147483648;
const INT_MAX = 2147483647;

/** Whether a parameter is chosen from a list: an Enum, or any string-valued type that declares allowed values. */
export function isChoice(parameter: InputParameter): boolean {
  return parameter.type === 'Enum' || ((parameter.allowedValues?.length ?? 0) > 0 && parameter.type !== 'PathList');
}

/** The starting value of every field: empty, unset, or an empty list. */
export function emptyValues(schema: InputParameter[]): Record<string, FieldValue> {
  const values: Record<string, FieldValue> = {};
  for (const parameter of schema) {
    values[parameter.name] = parameter.type === 'Boolean' ? null : parameter.type === 'PathList' ? [] : '';
  }

  return values;
}

/**
 * The input object the form describes, and every problem with it. The object holds only what the person filled in, with the
 * JSON type the schema declares; it is sent only when there is no problem.
 */
export function buildInput(schema: InputParameter[], values: Record<string, FieldValue>): { input: Record<string, unknown>; problems: InputProblem[] } {
  const input: Record<string, unknown> = {};
  const problems: InputProblem[] = [];
  const problem = (parameter: InputParameter, key: MessageKey, params: MessageParams = {}): void => {
    problems.push({ parameter: parameter.name, key, params: { name: parameter.name, ...params } });
  };

  for (const parameter of schema) {
    const value = values[parameter.name];
    const empty = value === undefined || value === null || value === '' || (Array.isArray(value) && value.length === 0);
    if (empty) {
      if (parameter.required) problem(parameter, 'delegations.input.required');
      continue;
    }

    switch (parameter.type) {
      case 'Boolean':
        input[parameter.name] = value === true;
        break;
      case 'Integer': {
        const text = String(value);
        const number = Number(text);
        if (!WHOLE_NUMBER.test(text) || !Number.isSafeInteger(number) || number < INT_MIN || number > INT_MAX) {
          problem(parameter, 'delegations.input.wholeNumber');
          break;
        }

        if (checkBounds(parameter, number, problem)) input[parameter.name] = number;
        break;
      }
      case 'Number': {
        const text = String(value);
        const number = Number(text);
        if (!NUMBER.test(text) || !Number.isFinite(number)) {
          problem(parameter, 'delegations.input.number');
          break;
        }

        if (checkBounds(parameter, number, problem)) input[parameter.name] = number;
        break;
      }
      case 'PathList': {
        const items = value as string[];
        if (items.some((item) => item === '')) {
          problem(parameter, 'delegations.input.emptyEntry');
          break;
        }

        if (parameter.minItems !== null && items.length < parameter.minItems) {
          problem(parameter, 'delegations.input.minItems', { count: parameter.minItems });
          break;
        }

        if (parameter.maxItems !== null && items.length > parameter.maxItems) {
          problem(parameter, 'delegations.input.maxItems', { count: parameter.maxItems });
          break;
        }

        input[parameter.name] = [...items];
        break;
      }
      default: {
        // String, Path, Duration, Enum: a string, exactly as typed. Lengths are UTF-16 code units, as the server counts them.
        const text = String(value);
        if ((parameter.allowedValues?.length ?? 0) > 0 && !parameter.allowedValues!.includes(text)) {
          problem(parameter, 'delegations.input.notAllowed');
          break;
        }

        if ((parameter.type === 'String' || parameter.type === 'Path') && parameter.minLength !== null && text.length < parameter.minLength) {
          problem(parameter, 'delegations.input.minLength', { count: parameter.minLength });
          break;
        }

        if ((parameter.type === 'String' || parameter.type === 'Path') && parameter.maxLength !== null && text.length > parameter.maxLength) {
          problem(parameter, 'delegations.input.maxLength', { count: parameter.maxLength });
          break;
        }

        input[parameter.name] = text;
      }
    }
  }

  return { input, problems };
}

function checkBounds(parameter: InputParameter, number: number, problem: (p: InputParameter, key: MessageKey, params?: MessageParams) => void): boolean {
  if (parameter.minimum !== null && number < parameter.minimum) {
    problem(parameter, 'delegations.input.minimum', { bound: parameter.minimum });
    return false;
  }

  if (parameter.maximum !== null && number > parameter.maximum) {
    problem(parameter, 'delegations.input.maximum', { bound: parameter.maximum });
    return false;
  }

  return true;
}

/**
 * The first property name that appears twice in one object of `text`, at any depth, or `null`. Read from the raw text before any
 * `JSON.parse`, which would keep only the last of the two and so erase the ambiguity (ADR-0044 §9.3). Names are compared as JSON
 * reads them (escapes decoded). Text that is not JSON simply finds nothing here; the parse that follows reports it.
 */
export function findDuplicateKey(text: string): string | null {
  const frames: { object: boolean; keys: Set<string>; expectKey: boolean }[] = [];
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    const frame = frames[frames.length - 1];
    if (c === '"') {
      let j = i + 1;
      while (j < text.length && text[j] !== '"') j += text[j] === '\\' ? 2 : 1;
      const token = text.slice(i, j + 1);
      i = j;
      if (frame?.object && frame.expectKey) {
        let name: string;
        try {
          name = JSON.parse(token) as string;
        } catch {
          return null;
        }

        if (frame.keys.has(name)) return name;
        frame.keys.add(name);
        frame.expectKey = false;
      }
    } else if (c === '{') {
      frames.push({ object: true, keys: new Set(), expectKey: true });
    } else if (c === '[') {
      frames.push({ object: false, keys: new Set(), expectKey: false });
    } else if (c === '}' || c === ']') {
      frames.pop();
    } else if (c === ',' && frame?.object) {
      frame.expectKey = true;
    }
  }

  return null;
}

/**
 * The input object written in the advanced JSON editor, or why it cannot be sent: a duplicate key (found before parsing), text that
 * is not JSON, JSON that is not one object, or a field the Capability does not declare. The same client checks as the form then
 * apply (see {@link rawProblems}).
 */
export function parseRaw(text: string, schema: InputParameter[]): { input: Record<string, unknown> } | { problem: InputProblem } {
  const duplicate = findDuplicateKey(text);
  if (duplicate !== null) {
    return { problem: { parameter: null, key: 'delegations.input.duplicateKey', params: { name: duplicate } } };
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return { problem: { parameter: null, key: 'delegations.input.notJson', params: {} } };
  }

  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return { problem: { parameter: null, key: 'delegations.input.notObject', params: {} } };
  }

  const declared = new Set(schema.map((parameter) => parameter.name));
  const unknown = Object.keys(parsed).find((name) => !declared.has(name));
  if (unknown !== undefined) {
    return { problem: { parameter: unknown, key: 'delegations.input.unknownField', params: { name: unknown } } };
  }

  return { input: parsed as Record<string, unknown> };
}

/** The same client checks for an object written as JSON: each value is put back through the form's rules, unchanged. */
export function rawProblems(schema: InputParameter[], input: Record<string, unknown>): InputProblem[] {
  const mapped = valuesFrom(schema, input);
  if ('problem' in mapped) return [mapped.problem];
  return buildInput(schema, mapped.values).problems;
}

/**
 * Form values for an input object, when the form can represent it exactly; otherwise the problem (a value of the wrong JSON type
 * for its parameter). Used when switching from the JSON editor back to the form, and by {@link rawProblems}.
 */
export function valuesFrom(schema: InputParameter[], input: Record<string, unknown>): { values: Record<string, FieldValue> } | { problem: InputProblem } {
  const values = emptyValues(schema);
  for (const parameter of schema) {
    if (!(parameter.name in input)) continue;
    const value = input[parameter.name];
    // A JSON null is "not given", as the server reads it; a required parameter is then reported as missing.
    if (value === null) continue;
    const wrongType: InputProblem = { parameter: parameter.name, key: 'delegations.input.wrongType', params: { name: parameter.name, type: parameter.type } };
    switch (parameter.type) {
      case 'Boolean':
        if (typeof value !== 'boolean') return { problem: wrongType };
        values[parameter.name] = value;
        break;
      case 'Integer':
      case 'Number':
        if (typeof value !== 'number') return { problem: wrongType };
        values[parameter.name] = String(value);
        break;
      case 'PathList':
        if (!Array.isArray(value) || value.some((item) => typeof item !== 'string')) return { problem: wrongType };
        values[parameter.name] = [...(value as string[])];
        break;
      default:
        if (typeof value !== 'string') return { problem: wrongType };
        values[parameter.name] = value;
    }
  }

  return { values };
}
