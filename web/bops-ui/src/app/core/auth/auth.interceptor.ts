// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AuthService, OWN_UNAUTHORIZED } from './auth.service';

/** The header the API's browser-session CSRF gate requires on unsafe requests (ADR-0043 §9). Not CORS-safelisted, on purpose. */
export const CSRF_HEADER = 'X-bOps-Request';

const SAFE_METHODS = ['GET', 'HEAD', 'OPTIONS', 'TRACE'];

/** A bOps API request is a relative URL under `/api/`. Absolute (and protocol-relative) URLs never get bOps headers. */
export function isBopsApiRequest(url: string): boolean {
  return url.startsWith('/api/') || url === '/api';
}

/**
 * The browser session rides on the same-origin `HttpOnly` cookie the browser attaches by itself (`withFetch()`, default
 * `credentials: 'same-origin'`): this interceptor never sets `Authorization`. It adds `X-bOps-Request: 1` to unsafe bOps API
 * requests, and is the single place that turns a 401 from the API into "session expired" while the operator is signed in.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const bops = isBopsApiRequest(request.url);
  const outgoing =
    bops && !SAFE_METHODS.includes(request.method.toUpperCase())
      ? request.clone({ setHeaders: { [CSRF_HEADER]: '1' } })
      : request;

  return next(outgoing).pipe(
    catchError((error: unknown) => {
      if (bops && error instanceof HttpErrorResponse && error.status === 401 && !request.context.get(OWN_UNAUTHORIZED)) {
        auth.expireSession();
      }

      return throwError(() => error);
    }),
  );
};
