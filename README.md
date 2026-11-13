# AbpSandbox

Untrusted work in containers, for ABP applications on .NET 10. Unpack and check an uploaded package, run a script an
agent wrote, convert a file someone brought in: in a container that has no network, a read-only root, no privileges and
a user that isn't root, within limits of time, memory, CPU, processes and bytes, and that is removed when it's done.

> **Preview.** 0.x versions may change their API in any minor version (SemVer). Pin an exact version.

> **Not a Volosoft project.** [ABP Framework](https://abp.io) is made and owned by [Volosoft](https://volosoft.com).
> AbpSandbox is an independent enhancement that works on ABP: this repository and its packages don't belong to
> Volosoft, and aren't affiliated with or endorsed by them.

## What it does

- **Runs, thrown away:** a container gets files, runs one command, gives back its exit code, its output and the files
  asked for, and is removed whatever happened. Past its time it's killed.
- **Sessions, kept:** a container kept for an owner under a key (an agent's chat, say), where commands run and files go
  in and out between calls, until it's closed, idle too long, or past its lifetime.
- **Prepared images, cached:** a request may name preparation steps; the first time they run they're saved as an
  image, and later requests with the same base, steps, files and owner start from it.
- **A warm pool, kept running:** containers started ahead of need, so a run doesn't wait for one; each serves one run
  and is replaced, so nothing one run left reaches the next.
- **The operator's rules:** images are names the settings map to pinned references, never something a request or an
  upload chooses; limits have defaults and maxima; the sweep removes what's past its time, even another server's.

Why and how: [docs/adr/0001](docs/adr/0001-containers-for-untrusted-work.md).

## Packages

| Package | What it holds |
|---|---|
| `AbpSandbox.Contracts` | `ISandbox`, requests and results, and the provider seam (`ISandboxProvider`). Nothing but .NET. |
| `AbpSandbox` | The ABP module: the policy, runs, sessions, prepared images and the sweep. |
| `AbpSandbox.Docker` | The Docker provider, through the Engine API (Unix socket, Windows named pipe or TCP). |
| `AbpSandbox.Remote` | The base for cloud providers: a runner beside the work in each task, jobs through object storage (ADR 0002). |
| `AbpSandbox.Fargate` | The AWS Fargate provider: ECS tasks, jobs through S3. [deploy/aws](deploy/aws/README.md) makes the closed network it needs. |
| `AbpSandbox.Agents` | Tools for [AbpAgent](https://github.com/tekthar/abp-agent): `sandbox_run`, `sandbox_exec`, `sandbox_put`, `sandbox_get`, opt-in and asking first. Depends only on `AbpAgent.Contracts`. |

An Azure (Container Instances) provider fits the same base, and Kubernetes comes later. Docker's containers share the
server's kernel: use it for development and a host's own checks; code from strangers belongs on Fargate, whose tasks
are each their own small virtual machine.

## Use it

```csharp
[DependsOn(typeof(AbpSandboxDockerModule))]
public class MyModule : AbpModule { }
```

```json
"AbpSandbox": {
  "Images": { "alpine": "alpine:3.20@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc" },
  "MaxTime": "00:05:00"
}
```

```csharp
SandboxResult result = await sandbox.RunAsync(new SandboxRequest
{
    Owner = "review:" + versionId,
    Image = "alpine",
    Command = ["sh", "-c", "unzip -q package.zip -d out && ls -R out"],
    Files = [new("package.zip", bytes)],
});
```

## Build and test

`dotnet build` and `dotnet test`. The Docker provider's tests run against a real engine when `ABP_SANDBOX_DOCKER=1`
(and `ABP_SANDBOX_DOCKER_ENDPOINT` for one that isn't the platform's own).

## Releasing

The version is `VersionPrefix` and `VersionSuffix` in `Directory.Build.props`, and `CHANGELOG.md` needs an entry for
it. Pushing the tag `v<version>`, such as `v0.1.0`, runs the checks. GitHub Actions then pushes the packages to
nuget.org through [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), with no
stored key, and makes a GitHub release. Set it up once:

1. **On nuget.org**, signed in as the account that will own the packages: **Trusted Publishing**, add a policy with
   owner `tekthar`, repository `abp-sandbox`, workflow file `ci.yml` and environment `release`.
2. **On GitHub**, in the repository's **Settings**: an environment `release`, and the Actions variable `NUGET_USER`,
   the nuget.org profile name of the account in step 1.
3. **Release:** `git tag v<version> && git push origin v<version>`.

## License

Apache License 2.0: see [LICENSE](LICENSE).
