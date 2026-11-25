import crypto from 'node:crypto';
import { APIRequestContext, APIResponse, request } from '@playwright/test';

const CLIENT_ID = 'umbraco-back-office';

/**
 * A backoffice session driven over HTTP, the way the backoffice client does it: cookie login, then the OpenIddict
 * PKCE code flow. Umbraco 17 and later keep the tokens in HttpOnly cookies and accept a literal placeholder in the
 * Authorization header, so the cookie jar of this context is the credential and nothing secret is ever read or logged.
 */
export class UmbracoApi {
  private constructor(
    private readonly ctx: APIRequestContext,
    readonly baseURL: string,
  ) {}

  static async login(baseURL: string, email: string, password: string): Promise<UmbracoApi> {
    if (!password) {
      throw new Error(`No password for ${email}. Copy .env.example to .env and fill it in.`);
    }
    const ctx = await request.newContext({ baseURL, ignoreHTTPSErrors: true });
    const login = await ctx.post('/umbraco/management/api/v1/security/back-office/login', {
      data: { username: email, password },
    });
    if (login.status() !== 200) {
      throw new Error(`Login failed for ${email} (HTTP ${login.status()}).`);
    }

    const verifier = crypto.randomBytes(48).toString('base64url');
    const challenge = crypto.createHash('sha256').update(verifier).digest('base64url');
    const redirect = `${baseURL}/umbraco/oauth_complete`;
    const authorize = await ctx.get('/umbraco/management/api/v1/security/back-office/authorize', {
      params: {
        client_id: CLIENT_ID,
        response_type: 'code',
        redirect_uri: redirect,
        scope: 'offline_access',
        code_challenge: challenge,
        code_challenge_method: 'S256',
        state: crypto.randomBytes(8).toString('hex'),
      },
      maxRedirects: 0,
    });
    const code = new URL(authorize.headers()['location'] ?? 'http://x/').searchParams.get('code');
    if (!code) {
      throw new Error(`The authorize step gave HTTP ${authorize.status()} and no code.`);
    }
    const token = await ctx.post('/umbraco/management/api/v1/security/back-office/token', {
      form: { grant_type: 'authorization_code', code, redirect_uri: redirect, client_id: CLIENT_ID, code_verifier: verifier },
    });
    if (token.status() !== 200) {
      throw new Error(`The token step failed (HTTP ${token.status()}).`);
    }
    return new UmbracoApi(ctx, baseURL);
  }

  private static readonly headers = { Authorization: 'Bearer [redacted]', Accept: 'application/json' };

  get(url: string, params?: Record<string, string | number | boolean | string[]>): Promise<APIResponse> {
    return this.ctx.get(url, { headers: UmbracoApi.headers, params: params as never, maxRedirects: 0 });
  }

  /** Raw GET with no Authorization header, for the cookie-authenticated upload route Forms protects. */
  getWithCookiesOnly(url: string): Promise<APIResponse> {
    return this.ctx.get(url, { maxRedirects: 0 });
  }

  post(url: string, data?: unknown, params?: Record<string, string>): Promise<APIResponse> {
    return this.ctx.post(url, { headers: UmbracoApi.headers, data, params });
  }

  put(url: string, data?: unknown): Promise<APIResponse> {
    return this.ctx.put(url, { headers: UmbracoApi.headers, data });
  }

  async json<T = any>(response: APIResponse): Promise<T> {
    return (await response.json()) as T;
  }

  dispose(): Promise<void> {
    return this.ctx.dispose();
  }
}
