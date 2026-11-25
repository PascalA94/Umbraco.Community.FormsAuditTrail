/** Moves focus to a view's heading when the dashboard switches tab on the user's behalf. */
export function focusHeadingIn(root: ShadowRoot | null): boolean {
  const heading = root?.querySelector<HTMLElement>('#heading');
  heading?.focus();
  return heading !== null && heading !== undefined;
}

export async function bearerToken(auth?: { getLatestToken(): Promise<string> }): Promise<string | undefined> {
  return (await auth?.getLatestToken()) ?? undefined;
}
