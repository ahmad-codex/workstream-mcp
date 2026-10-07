# Governance

workstream-mcp is maintained by a single maintainer, [@ahmad-codex](https://github.com/ahmad-codex),
who has final say on design, merges and releases.

## How decisions are made

- Small changes (fixes, docs, tests) are decided in the pull request.
- Changes to the MCP tool contracts, the database schema, the state-graph format or the
  security model start as an issue or a Discussion so the trade-offs are written down before
  code is reviewed. [workstream-mcp-spec.md](./workstream-mcp-spec.md) is the design reference;
  a change that contradicts it updates the spec in the same pull request.
- Every merge to `master` needs a passing CI run.

## Releases

Versions follow [Semantic Versioning](https://semver.org/). While the version is 0.x, minor
releases may include breaking changes; they are called out in the release notes.
release-drafter keeps a draft of the next release from merged pull request labels. The
maintainer publishes it and pushes the `vX.Y.Z` tag, which builds and pushes the Docker image
to `ghcr.io/ahmad-codex/workstream-mcp`.

## Becoming a maintainer

Contributors with a record of good reviews and merged changes may be invited to become
maintainers. This file will then list them and describe how they share decisions.
