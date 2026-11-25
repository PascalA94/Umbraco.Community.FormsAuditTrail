import { execFileSync } from 'node:child_process';

/** Which database the site under test runs on. Set by the person running the suite, as the site's own connection is not visible to a spec. */
export const provider = (): 'sqlite' | 'sqlserver' => ((process.env.E2E_PROVIDER ?? 'sqlite').toLowerCase() === 'sqlserver' ? 'sqlserver' : 'sqlite');

/**
 * Runs one T-SQL batch as a privileged login through `E2E_SQLCMD`: the path of a `sqlcmd` or of a wrapper script that
 * adds the server, database and credentials itself, so no password is ever an argument (visible in `ps`) or in a spec.
 * Throws with the exit status and the tool's stderr on a non-zero exit (`-b` makes SQL errors exit non-zero).
 */
export function sqlcmd(query: string): string {
  const bin = process.env.E2E_SQLCMD;
  if (!bin) {
    throw new Error('E2E_SQLCMD is not set. Point it at sqlcmd, or at a wrapper that connects as a login able to DENY and REVOKE.');
  }
  try {
    return execFileSync(bin, ['-b', '-h', '-1', '-W', '-Q', query], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  } catch (err) {
    // Only the status and the tool's stderr: the error's own message and argv name the command line, which a bare
    // sqlcmd configured with -P would carry the password on.
    const { status, stderr } = err as { status?: number | null; stderr?: string | Buffer };
    throw new Error(`sqlcmd failed with exit status ${status ?? 'unknown'}: ${String(stderr ?? '').trim()}`);
  }
}

/** The database user the site connects as. Validated because it is placed in a statement, quoted, not parameterised. */
export function siteDatabaseUser(): string {
  const user = process.env.E2E_SQL_SITE_USER ?? '';
  if (!/^[A-Za-z0-9_]{1,64}$/.test(user)) {
    throw new Error('E2E_SQL_SITE_USER must name the (non-owner) database user the site connects as: letters, digits and underscores only.');
  }
  return user;
}

/**
 * How many Forms writes to run at once. SQLite allows one writer, and Forms' own storage fails with "database is
 * locked" (HTTP 500, not the package) under parallel creates, so it is 1 there.
 */
export const writeConcurrency = (): number => (provider() === 'sqlite' ? 1 : 6);
