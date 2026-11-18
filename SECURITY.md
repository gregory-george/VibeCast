# Security Policy

## Supported versions

Only the latest release on `master` is supported. Fixes are not backported.

## Reporting a vulnerability

Please **do not** open a public issue for security problems.

Report privately through GitHub's
[private vulnerability reporting](https://github.com/gregory-george/VibeCast/security/advisories/new)
("Report a vulnerability" on the repository's **Security** tab). Include steps to reproduce
and, if relevant, a sample feed URL or OPML file that triggers the issue.

You can expect an initial response within a week. This is a single-maintainer hobby
project, so please allow reasonable time for a fix before any public disclosure.

## Scope

VibeCast is a local, single-user Windows app that binds to `127.0.0.1` only. Areas of
particular interest:

- Handling of untrusted feed content (RSS/Atom/YouTube XML, show-note HTML, enclosures,
  OPML imports) — e.g. XSS through show notes, XXE, path traversal or executable file
  types landing in `downloads/`.
- The loopback HTTP endpoints (`/media/...`, `/opml/export`) — e.g. access from other
  origins or other machines.
- Anything that writes outside the app's own folder.

Out of scope: issues requiring an attacker who already has local access to the user's
account or the app folder.
