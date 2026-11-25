import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { APIRequestContext, APIResponse } from '@playwright/test';
import { UmbracoApi } from './umbraco-api';

export interface Stats {
  n: number;
  medianMs: number;
  p95Ms: number;
  meanMs: number;
}

export function stats(samples: number[]): Stats {
  const sorted = [...samples].sort((a, b) => a - b);
  const at = (q: number) => sorted[Math.min(sorted.length - 1, Math.ceil(q * sorted.length) - 1)];
  const median = sorted.length % 2 ? sorted[(sorted.length - 1) / 2] : (sorted[sorted.length / 2 - 1] + sorted[sorted.length / 2]) / 2;
  return { n: sorted.length, medianMs: median, p95Ms: at(0.95), meanMs: samples.reduce((a, b) => a + b, 0) / samples.length };
}

/** Wall-clock milliseconds from send to the last body byte, which is what a user's browser waits for. */
export async function timed(work: () => Promise<APIResponse>): Promise<{ ms: number; response: APIResponse }> {
  const start = performance.now();
  const response = await work();
  await response.body();
  return { ms: performance.now() - start, response };
}

/**
 * A request with no client timeout, for the two operations in the load check that legitimately run for minutes (a
 * 5,000 entry bulk delete and a 100,000 row export). `UmbracoApi` keeps its context private and applies Playwright's
 * 30 second default, so this reaches the context directly rather than changing the shared helper.
 */
export function noTimeout(api: UmbracoApi): { get(url: string): Promise<APIResponse>; post(url: string, data: unknown): Promise<APIResponse>; delete(url: string): Promise<APIResponse> } {
  const ctx = (api as unknown as { ctx: APIRequestContext }).ctx;
  const headers = { Authorization: 'Bearer [redacted]', Accept: '*/*' };
  return {
    get: (url) => ctx.get(url, { headers, timeout: 0, maxRedirects: 0 }),
    post: (url, data) => ctx.post(url, { headers, data, timeout: 0 }),
    delete: (url) => ctx.delete(url, { headers, timeout: 0 }),
  };
}

/**
 * Results of the "on" and "off" passes are written here between site restarts. Outside the repository on purpose:
 * Playwright empties `test-results` at the start of every run, which would delete the first pass before the second.
 */
export function loadDir(): string {
  const dir = process.env.E2E_LOAD_DIR ?? path.join(os.tmpdir(), 'formsaudit-load-check');
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

export interface Pass {
  mode: 'on' | 'off';
  provider: string;
  list: Stats[];
  detail: Stats[];
  edit: Stats[];
}

export const passFile = (provider: string, mode: 'on' | 'off') => path.join(loadDir(), `${provider}-${mode}.json`);

export function readPass(provider: string, mode: 'on' | 'off'): Pass | null {
  try {
    return JSON.parse(fs.readFileSync(passFile(provider, mode), 'utf8')) as Pass;
  } catch {
    return null;
  }
}

export const fmt = (s: Stats) => `median ${s.medianMs.toFixed(1)} ms, p95 ${s.p95Ms.toFixed(1)} ms, mean ${s.meanMs.toFixed(1)} ms (n=${s.n})`;
