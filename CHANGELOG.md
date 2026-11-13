# Changelog

All notable changes to AbpSandbox are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

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
