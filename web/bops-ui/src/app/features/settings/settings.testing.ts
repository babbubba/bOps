// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { SettingsProviderView, SettingsView } from '../../core/api/models';

/** A provider card as GET /api/settings returns it; every override is a plain field so a test states only what it cares about. */
export function settingsProviderFixture(overrides: Partial<SettingsProviderView> = {}): SettingsProviderView {
  return {
    providerId: 'Anthropic',
    isActive: false,
    hasStoredKey: false,
    keyMaskPrefix: null,
    keyMaskSuffix: null,
    keyPlaintextLength: null,
    keyUpdatedUtc: null,
    baseUrl: null,
    model: null,
    supportsNativeToolCalling: null,
    extraParameters: null,
    profileUpdatedUtc: null,
    hasUsableCredential: false,
    credentialSource: 'none',
    isPersistedSelectionShadowed: false,
    shadowedFields: [],
    extraParametersPersistedOnly: true,
    ...overrides,
  };
}

/** GET /api/settings with the ADR-0045 effective/persisted fields at their "fresh install" values. */
export function settingsViewFixture(overrides: Partial<SettingsView> = {}): SettingsView {
  return {
    vaultVersion: 0,
    activeProviderId: null,
    activeProviderSource: 'Default',
    providers: [settingsProviderFixture()],
    settingsRevision: 0,
    configurationGeneration: 1,
    persistedActiveProviderId: null,
    persistedActiveProviderShadowed: false,
    effectiveBaseUrl: 'https://openrouter.ai/api/v1',
    effectiveModel: 'openrouter/free',
    effectiveSupportsNativeToolCalling: true,
    effectiveBaseUrlSource: 'default',
    effectiveModelSource: 'default',
    effectiveToolCallingSource: 'default',
    effectiveCredentialAvailable: false,
    effectiveCredentialSource: 'none',
    persistedFallbacks: [],
    effectiveFallbacks: [],
    effectiveFallbackSource: 'default',
    persistedFallbacksShadowed: false,
    ...overrides,
  };
}
