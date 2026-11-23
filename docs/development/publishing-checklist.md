# Before making the repository public

A maintainer's checklist. Nothing here is needed to use or develop Margo.

1. **Turn on private vulnerability reporting.** [SECURITY.md](../../SECURITY.md) and the
   [code of conduct](../../CODE_OF_CONDUCT.md) send reporters to GitHub's private reporting
   (**Security → Report a vulnerability**). It works only once it is enabled under the repository's
   **Settings → Code security**. Check it from a signed-out browser.
2. **Decide about git history.** The working tree has no tenant identifiers, secrets or real workplace data
   (`./tools/check-clean.sh` checks the tree on every handoff), but two older commits of
   `docs/margo-cloud-plan.md` name the domain of the maintainer's test tenant. It is not a secret. Removing it means
   rewriting history and force-pushing, which breaks every existing clone, so do it before the first public push or
   not at all. Scan the history again afterwards (for example with `git log -p` and a secret scanner).
3. **Releases.** Only `v1.0.0` is tagged and has installers. The [changelog](../../CHANGELOG.md) lists 1.1.0 and
   1.2.0 as released and `VERSION` says 1.3.0 (unreleased). Either tag and publish them, or keep the README's note
   that the packaged installers are 1.0.0.
4. **Margo Cloud's status.** The README, `cloud/README.md` and the deployment guide say Margo Cloud is not ready for
   production. Keep that until the deployment guide's [known limits](../autopilot-deployment.md#known-limits) are
   closed or accepted, and the post-review fixes have run in a test tenant.
5. **Terms.** [`cloud/terms.md`](../../cloud/terms.md) names the source's publisher. Operators replace it with their
   own organisation when they publish it to their users; the deployment guide says so.
6. **Run every gate** listed in [AGENTS.md](../../AGENTS.md) on the commit you publish.
