# Contributing

Thank you for helping. Issues and pull requests are welcome on GitHub.

- **Before a large change**, open an issue to agree on the approach. A smaller change that is easy to review is more
  likely to land.
- **Build and test** as the README says: `dotnet build`, `dotnet test` (with `ABP_SANDBOX_DOCKER=1` and a Docker
  engine for the providers' tests), and `dotnet pack`. A pull request runs the same in CI, on GitHub Actions.
- **Keep what hosts rely on.** These are a contract: changing one is a breaking change and goes in the CHANGELOG under
  the version that makes it.
  - the contracts in `AbpSandbox.Contracts`: `ISandbox`, requests and results, and the provider seam;
  - what a container may not do (docs/adr/0001, decision 4), and that a warm container serves one run;
  - the runner's protocol (docs/adr/0002): a host's runner image and its provider must match;
  - the agents' tools' names and inputs.
- **Nothing of a host's in the package.** `scripts/check-names.sh` holds that.
- **Tests** come with the change: a unit test for logic, and a test against Docker for anything a container does.
- **CHANGELOG.md:** write what changes for someone using the packages under `## [Unreleased]`.

By contributing you agree that your contribution is licensed under the Apache License, Version 2.0.
