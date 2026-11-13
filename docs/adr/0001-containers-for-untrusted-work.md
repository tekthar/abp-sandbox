# 0001. Containers for untrusted work: runs, sessions and prepared images

- **Status:** Accepted
- **Date:** 2026-11-13
- **Revisit at:** a Kubernetes provider, a session's work kept between sessions, sessions shared across servers

## Context

A host that takes packages from strangers (plugins as zips, say) can read them safely enough: in memory, within limits
(size, file count, unpacked size, paths that stay inside), with their signature verified. Reading isn't enough for the
checks it wants next: load a package's assemblies to see what they really register, scan them, compare what their code
calls with what the manifest declares, render their docs. Each of those runs or parses untrusted content with code that
wasn't written for hostile input, and a mistake there runs on the server that holds the host's database and keys. A
host that installs plugins is in the same place before it loads one.

Agents (AbpAgent) want somewhere to run code they write: a calculation, a file conversion, a script over data a person
brought in.

So: run such work in a container that is made, does everything inside, and is removed, from a reusable module that
hosts and AbpAgent share. Containers may be thrown away or kept, and something that recurs may start from a prepared
container instead of preparing it again. Production runs where creating containers is a service: AWS Fargate first.

## Decision

1. **AbpSandbox is a package of the Abp family**, independent of any host: `AbpSandbox.Contracts` (plain .NET),
   `AbpSandbox` (the ABP module: policy, sessions, prepared images, the sweep) and a package per provider, first
   `AbpSandbox.Docker`. `AbpSandbox.Agents` adapts it to AbpAgent's tools (agreed with AbpAgent's maintainers: it
   lives here and depends only on `AbpAgent.Contracts`, as `AbpAi.Agents` adapts AbpAi). It keeps no tables.

2. **Three ways to use a container:**
   - **A run** is thrown away: a container is made from an image, given files, runs one command within its limits,
     and is removed, whatever happened. It gives back the exit code, what it wrote to stdout and stderr (capped, saying
     when cut) and the output files asked for (capped).
   - **A session** is kept: a container opened for an owner under a key (an agent's chat or run, say) stays up between
     calls, which exec commands in it and put and get files, until it's closed, idle past its idle time, or past its
     lifetime. Opening the same owner and key again gives the same session while it lives and its image is the same.
     Sessions live on the server that opened them; a restart ends them (the sweep removes what's left).
   - **A prepared image** is cached: a request may name preparation steps (installing a tool, say). The first time,
     a container runs them from the base image and is saved as an image; later requests with the same base, steps and
     files start from it. Its key is a SHA-256 of the base image's pinned reference, the steps, the preparation files'
     contents and the owner, so one owner's prepared image never serves another's. A request marks a preparation
     shared only when its steps and files are the host's own, never content someone uploaded. Prepared images are
     evicted oldest-used first past a count.
   - **A warm pool** keeps containers running ahead of need (added 13 Nov 2026), so a
     run doesn't wait for one to start; on Fargate that is tens of seconds. The operator names how many to keep for
     each image. A warm container serves one run, one that asks for nothing it wasn't made with (the default limits,
     no environment, no preparation), and is removed after; another starts in its place. Warm containers are replaced
     after an hour, so an image's update reaches the pool. A warm container is never used twice: what one run left
     (files, processes) must not reach the next, which may be another owner's. Keeping a container for one owner's
     calls is a session.

3. **Images are named, not given.** The host's settings map a short name to a pinned reference
   (`AbpSandbox:Images:dotnet = mcr.microsoft.com/dotnet/runtime:10.0@sha256:...`), and requests name the short
   name. What someone uploads is only ever data put into a container, never an image to pull or build, so a package
   can't choose what runs.

4. **Locked down by default**, and a request can't loosen it:
   - no network (Docker's `none`); a provider that can't promise this refuses to run;
   - the root file system read-only; `/tmp` a small `tmpfs` without exec; the work folder (`/sandbox/work`), where
     files go in and out, is the container's own volume, removed with it;
   - every capability dropped, `no-new-privileges`, and a user that isn't root (65534 unless the image's name says
     another non-zero uid);
   - memory (with no swap), CPU, process count and time limited; past its time a run is killed, and a session whose
     command runs past its time is closed;
   - input and output capped: files in, files out, stdout and stderr.

   Preparation is the one step that writes the root file system, as the image's own user with Docker's default
   capabilities less raw sockets and `mknod`, still with no network. Its steps come from the host's code, which
   chose them; content from a request reaches a prepared image only in the owner's own scope.

5. **Limits are the operator's.** Settings give defaults and maxima (time, memory, CPU, processes, bytes in and out,
   runs at once, sessions in all and per owner, prepared images kept); a request may ask for less than the maximum or
   up to it, and is refused above it. A host may add its own quotas (`AbpSandbox.Agents` keeps one per agent).

6. **Providers run containers; the module keeps the rules.** `ISandboxProvider` makes, feeds, runs, execs in, reads
   from, saves and removes containers. The module resolves image names, applies limits, computes preparation keys,
   keeps sessions and their timers, and sweeps. Every container a provider makes carries labels: `abp-sandbox=1`,
   its kind, the server that made it and when it expires, so the sweep (at start and every minute) removes containers
   past their time or left by a restart, even another server's.

7. **Providers:**
   - **Docker** (development, self-hosting): the Engine API over a Unix socket, a Windows named pipe or TCP, with no
     client library. Files go in and out as tar archives into the container's volume, which Docker allows on a
     read-only root.
   - **AWS Fargate** (production, next): a task per run, each in its own small virtual machine, in a private subnet
     that reaches only S3 (a gateway endpoint), with no task role beyond reading its input and writing its output
     through presigned links. Sessions keep a task up; prepared images go to ECR. A provider says what it can do
     (sessions, preparation); the module refuses what it can't.
   - **Kubernetes** (later): a Job with a sandboxed runtime class (gVisor or Kata).

## Consequences

- A host can run package checks without any of them touching its own process, and can check a plugin before it
  loads one.
- Agents get a disposable computer with no network: AbpSandbox.Agents' tools are opt-in, ask first (they aren't
  read-only), are refused in read-only runs, take only what the model gives them (text or base64; a record's files
  come through the host's own tools, which check rights), and cap what they give back.
- A sandbox is only as strong as its provider. Docker's containers share the server's kernel; that is acceptable for
  development and for hosts that run their own trusted checks, not for code from strangers on a server with secrets.
  Production uses Fargate's virtual machines or a sandboxed runtime class.
- The local Docker volume driver has no size limit: a run's disk use is bounded by its input cap, its time and what it
  can write in that time, not by a quota.
- Nothing is kept across restarts: sessions end, prepared images stay (they're images) and are found again by key.
