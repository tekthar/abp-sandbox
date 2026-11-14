# For ABP developers: where these packages sit in ABP

AbpSandbox is infrastructure, like ABP's own `Volo.Abp.BlobStoring` or `Volo.Abp.BackgroundJobs`, not an application
module. It keeps no tables and serves no endpoints, so it has no Domain, Application, EntityFrameworkCore or HttpApi
layers to map. Its contracts are a package of their own, as ABP's `*.Abstractions` packages are, and each provider is a
package and a module, as ABP's BlobStoring providers are. This page says which ABP part each package resembles and how
it fits.

| Package | ABP's counterpart | How it fits |
|---|---|---|
| `AbpSandbox.Contracts` | An `*.Abstractions` package (such as `Volo.Abp.BlobStoring`'s interfaces) | `ISandbox`, the requests and results, and the provider seam `ISandboxProvider`, with no ABP or other dependency, so a host's own contracts can reference them |
| `AbpSandbox` | `Volo.Abp.BlobStoring`, the part with no provider | `AbpSandboxModule`: the policy (named and pinned images, limits), runs, sessions, prepared images, the warm pool and the sweep, and `SandboxOptions` from the `AbpSandbox` section. `AbpSandbox:Provider` picks a provider by name |
| `AbpSandbox.Docker` | A BlobStoring provider, such as `Volo.Abp.BlobStoring.FileSystem` | `AbpSandboxDockerModule` registers the `docker` provider: containers through the Docker Engine API |
| `AbpSandbox.Remote` | None (it's what cloud providers share) | The base for cloud providers: the runner beside the work, and jobs through object storage. A cloud's module adds it with `AddRemoteSandbox<TPlatform, TStore>(configuration, name)` |
| `AbpSandbox.Fargate` | A BlobStoring provider, such as `Volo.Abp.BlobStoring.Aws` | `AbpSandboxFargateModule` registers the `fargate` provider: ECS tasks on Fargate and jobs through S3. `deploy/aws` makes the network it needs |
| `AbpSandbox.Agents` | None (it's for AbpAgent, not ABP) | Tools for AbpAgent's `IAgentTools`. It depends only on `AbpAgent.Contracts`, as `AbpAi.Agents` adapts AbpAi |

## What works the ABP way

- **Modules and options:** each package with a runtime part is a module with `[DependsOn]`. Its settings bind to an
  options class from the configuration section it names (`SandboxOptions` from `AbpSandbox`, `DockerSandboxOptions`
  from `AbpSandbox:Docker`, `FargateSandboxOptions` from `AbpSandbox:Fargate`).
- **Providers chosen by configuration,** as BlobStoring's are: a provider's module registers itself under a name
  (`AddSandboxProvider<T>(name)`), and `AbpSandbox:Provider` picks one. Write your own by implementing `ISandboxProvider`.
- **Dependency injection:** the services are ABP's usual registrations (`ISandbox` a singleton), with `TryAdd` where a
  host may give its own, and `TimeProvider` for anything timed, so tests can turn the clock.
- **Background work:** the sweep is a hosted service, like ABP's background workers, and can be turned off
  (`AbpSandbox:Sweep`) for a host that runs it elsewhere.

## Where it differs, and why

- **No tables and no endpoints:** the sandbox runs work for the host's own features (a package check, an agent's tool),
  which decide who may start it and what to keep. So there's nothing to migrate, and no HTTP API to expose or secure.
- **The state of its containers lives with the containers:** sessions are in the memory of the server that opened them,
  and every container carries labels (or a cloud task carries tags) saying which server made it and when it expires. A
  sweep on any server can then clean up after one that stopped, with no table to keep in step.
- **Nothing runs until a provider is named:** even with a provider's module loaded, `AbpSandbox:Provider` must name it.
  BlobStoring falls back to a default container; a sandbox mustn't fall back to running untrusted work on the host's own
  server by accident.
- **The runner and the deployment template aren't .NET:** `runner/` is a small container image (a shell courier and a
  static busybox), and `deploy/aws` is CloudFormation. The cloud's closed network is what keeps the work in, and the
  module can't check it, so these sit beside the packages, not in them.
