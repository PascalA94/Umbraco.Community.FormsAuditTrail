# Security Policy

## Supported versions

| Package version | Umbraco | Supported |
|---|---|---|
| 18.x | 18 | Yes |
| 17.x | 17 | Yes |
| 1.0.0 | 17 | No - upgrade to 17.x |

Security fixes are released as patch versions on each supported line.

## Reporting a vulnerability

Please **do not** open a public issue for a security problem.

Report it privately through GitHub's private vulnerability reporting: go to the repository's [**Security** tab](https://github.com/PascalA94/Umbraco.Community.FormsAuditTrail/security) and choose **Report a vulnerability**.

Please include:

- the affected package version, and the Umbraco CMS and Umbraco Forms versions
- steps to reproduce, or a proof of concept
- the impact as you understand it (for example, a user reading audit history for forms they cannot access)

You should get an acknowledgement within 5 working days. Once a fix is released, the advisory is published with credit to you, unless you would rather not be named.

## Scope

In scope: this package's code - the audit API and its per-form permission checks, the CSV export, the backoffice dashboard, and data stored in its database tables.

Out of scope: vulnerabilities in Umbraco CMS or Umbraco Forms themselves. Report those to Umbraco through its [security reporting process](https://umbraco.com/trust-center/security-and-umbraco/how-to-report-a-vulnerability-in-umbraco/).
