// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import type { en } from './en';

/** Every key of the English catalogue. A key that is not here cannot be asked for: a typo in a template is a compile error. */
export type MessageKey = keyof typeof en;

/** The shape each catalogue must have: exactly the keys of `en`, no fewer and no more. */
export type Messages = Record<MessageKey, string>;

/** The shared prefix of a message that has `.one` and `.other` variants (`dashboard.task.meta` for `dashboard.task.meta.other`). */
export type PluralKey = MessageKey extends infer K ? (K extends `${infer Base}.other` ? Base : never) : never;

/** What `t` accepts: a message key, or the prefix of a plural message together with a numeric `count` parameter. */
export type TranslationKey = MessageKey | PluralKey;

export type MessageParams = Record<string, string | number>;
