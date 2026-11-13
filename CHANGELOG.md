# Changelog

All notable changes to AbpSandbox are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

## [0.2.0] - 2026-11-13

Knowing whether the sandbox can run, and choosing its provider by a setting.

### Changed (breaking)

- **The provider is chosen by `AbpSandbox:Provider`** (`docker`, `fargate`): each provider's module registers itself
  under its name (`AddSandboxProvider<T>(name)`), and none runs until the setting names one, even with several modules
  loaded. A 0.1.0 host that depended on `AbpSandboxDockerModule` sets `"AbpSandbox": { "Provider": "docker" }`.
  `AddRemoteSandbox` takes the name too.

### Added

- `ISandbox.CheckAsync(image)`: whether the sandbox can run now (`SandboxAvailability`, with why not): a provider is
  set and answers (Docker's ping; Fargate's cluster active and its bucket answering; the runner image set), its answer
  kept for `AbpSandbox:AvailabilityCacheTime` (a minute); and, given an image's name, the settings pin it.
  `ISandboxProvider`, `ISandboxTaskPlatform` and `ISandboxObjectStore` have a `CheckAsync` that says yes by default.
- `SandboxAgentTools.CheckAsync` and `AvailableToolsAsync`: agents' tools only while the sandbox is available, for a
  host's own pages too. `AgentToolsWithSandbox` lists them only then, and a tool given earlier refuses with why.
- deploy/aws: `ProviderPolicy` allows `ecs:DescribeClusters` on the cluster, for the check.

### Fixed

- AbpSandbox.Agents' tools reach `Inside` the run's scope: the container is the agent's own and changes nothing of the
  host's, so a tool set to run no longer asks as if it reached beyond the scope (AbpAgent's rule for a changing tool
  that names no item), and its answers aren't read for item keys.

## [0.1.0] - 2026-11-13

The first release: untrusted work in containers, on Docker and AWS Fargate, with tools for AbpAgent.

### Added

- `ISandbox`: throwaway runs, kept sessions and prepared images, with the operator's images and limits (ADR 0001).
- `AbpSandbox.Docker`: the Docker provider over the Engine API, with no network, a read-only root, no capabilities,
  `no-new-privileges` and a user that isn't root.
- `AbpSandbox.Agents`: AbpAgent tools (`sandbox_run`, `sandbox_exec`, `sandbox_put`, `sandbox_get`), opt-in, refused in
  read-only runs, a session per run, prepared images and a day's calls per agent in its scope; `AgentToolsWithSandbox`
  adds them to a host's catalog.
- A warm pool (`AbpSandbox:WarmPool`): containers kept started for an image, each serving one run and then replaced
  (`SandboxResult.FromWarmPool`).
- `AbpSandbox.Remote`: the base for cloud providers (ADR 0002): a task of three containers (the runner's init and
  courier beside the work), jobs through object storage with signed links, sessions and the warm pool; preparation is
  refused. `runner/` builds the runner's image.
- `AbpSandbox.Fargate`: the AWS Fargate provider (ECS tasks, S3 presigned URLs), and `deploy/aws/abp-sandbox.yaml`, a
  CloudFormation template for its closed network, DNS firewall, jobs bucket, registries, cluster and roles.
- `AbpSandbox.Agents` keys a session on the chat (AbpAgent 0.5.0's `AgentActing.ChatId`), so a chat keeps its container
  between turns; on the run when the run isn't a chat's.
