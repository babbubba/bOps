// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

/**
 * Global set-up for every spec: English is the default, whatever the machine running the tests prefers and whatever an earlier spec
 * stored. A spec that needs another language or a blocked store sets it up itself.
 */
beforeEach(() => {
  try {
    localStorage.removeItem('bops-ui-language');
  } catch {
    // Nothing stored to clear.
  }

  Object.defineProperty(navigator, 'language', { value: 'en-US', configurable: true });
  document.documentElement.lang = 'en';
});
