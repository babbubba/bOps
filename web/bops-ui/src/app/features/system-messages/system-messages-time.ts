// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

/**
 * The value of a `datetime-local` input (`2026-10-08T09:30` or with seconds) is wall-clock time in the browser's zone. The API takes
 * an inclusive UTC instant, so the conversion is explicit: the entered moment is read in the local zone and sent as ISO 8601 UTC with
 * milliseconds, exactly the instant typed — nothing is rounded to a day or widened. `undefined` for an empty or unreadable value.
 */
export function localInputToUtcIso(value: string): string | undefined {
  const trimmed = value.trim();
  if (!trimmed) return undefined;
  const instant = new Date(trimmed);
  return Number.isNaN(instant.getTime()) ? undefined : instant.toISOString();
}
