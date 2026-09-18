# Bootstrap the private commercial and coordination repositories

Status: **complete, with one accepted deviation: no platform-enforced branch protection or secret scanning on the private repositories (owner decision 2026-09-18)**
Recommended model effort: **alto**
Repositories: public `bOps`, private `bOps.Commercial`, private `bOps.Workspace`
Deadline: complete before V1.3 private implementation

## Outcome

Create a private commercial repository and a private coordination root that pins public `bOps` and
private `bOps.Commercial` as Git submodules so an authorized agent can bootstrap both safely from
one checkout.

## Decisions required before remote mutation

- [x] Confirm GitHub and the `babbubba` owner.
- [x] Confirm the exact private commercial repository name: `bOps.Commercial`.
- [x] Confirm the exact coordination-root repository name: `bOps.Workspace`.
- [x] Confirm `repos/bOps` and `repos/bOps.Commercial`, both using `main`.
- [x] Confirm private license/copyright holder and collaborator/team access: copyright holder Fabio
      Cavallari, all rights reserved (owner placeholder, not reviewed by counsel); sole collaborator
      `babbubba`.
- [x] Use fresh workspace/submodule clones without moving the existing public checkout.
- [x] Obtain explicit authorization before creating and pushing the remote repositories.

## Commercial repository bootstrap

- [x] Create it private and verify visibility through the GitHub API.
- [x] Add private license notice, README, security/contact policy, `.gitignore`, CODEOWNERS and
      branch protection suitable for proprietary code. Branch protection is replaced by the binding
      `CONDUCT.md` in both private repositories: the plan returns 403 for branch protection and
      rulesets, the owner is the only contributor, and any future collaborator must accept the
      conduct rules before access. CI guards back it up.
- [x] Configure secret scanning and CI with no dependency on a developer machine: a full-history
      gitleaks job and a public-boundary job run on GitHub-hosted runners and were proven to fail on
      a fake token and an Apache header. GitHub-native secret scanning is unavailable on the plan.

## Coordination-root bootstrap

- [x] Create `bOps.Workspace` private with no product source and no license ambiguity.
- [x] Add the two repositories as submodules at the approved paths using remotes accessible to the
      authorized team; pin exact commits.
- [x] Add bootstrap documentation and a safe script for recursive clone/update, clean-state checks,
      per-repository build/test dispatch and current commit reporting.
- [x] Add root agent instructions that route work to the owning repository, forbid cross-repo
      commits, preserve submodule boundaries and require submodule commits before pointer updates.
- [x] Add cross-repository plan/task coordination without duplicating repository-owned task detail.
- [x] Add CI that validates submodule reachability and authorized dependency combinations without
      publishing private commit identifiers into the public repository. Approved-submodule, no-product-
      source and secret-scan checks always run; the reachability check runs when the optional secret
      `SUBMODULE_TOKEN` exists and otherwise skips with a visible warning.

## Validation

- [x] Fresh recursive clone succeeds for the authorized `babbubba` identity at both pinned commits.
- [x] Confirm an unauthorized identity cannot initialize `bOps.Commercial`: an unauthenticated clone
      is refused. A second, authenticated non-collaborator account was not tested.
- [x] Public-only clone/use of `bOps` remains unchanged.
- [x] Both submodules build/test through root orchestration and independently (`bOps` built and
      tested; `bOps.Commercial` reports no buildable source yet).
- [x] Dirty/detached/wrong-branch submodule states are reported clearly before automation mutates
      pointers.
- [x] A sample public change, private change and root pointer update follow the documented order
      (public docs PR, private governance PRs, then a workspace pointer PR).
- [x] No secret, proprietary source or private CI log is committed to public `bOps`.

## Definition of Done

- [x] Both remote repositories exist with intended visibility; platform protections are replaced by
      `CONDUCT.md` and CI guards (accepted deviation).
- [x] An authorized agent can clone the root recursively and operate both repositories quickly.
- [x] Ownership, licensing and commits remain structurally separate; CI/release governance remains
      to be completed.
- [x] The coordination root pins reviewed commits and contains no duplicated product code.

## Evidence

- `https://github.com/babbubba/bOps.Commercial` — private, bootstrap commit `f3c6295`.
- `https://github.com/babbubba/bOps.Workspace` — private, root commit `a2d6ef1`.
- Fresh recursive clone verified pins `c08b1f7` for public `bOps` and `f3c6295` for
  `bOps.Commercial`; the temporary validation checkout was moved to the Recycle Bin afterwards.
- Accepted deviations: no platform-enforced protection on the private repositories; the license is
  an owner placeholder awaiting legal review before any external distribution; an authenticated
  non-collaborator account was not tested for access denial; `SUBMODULE_TOKEN` is not configured.
