# 0002. Cloud providers: a task, a runner beside the work, and jobs through object storage

- **Status:** Accepted: the AWS Fargate provider now, built so that an Azure one follows
- **Date:** 2026-11-13
- **Revisit at:** the Azure provider, prepared images on a cloud (a build service), Kubernetes

## Context

ADR 0001 gives AbpSandbox a provider seam; the Docker provider reaches into its containers through the Engine API
(copy files, exec, read files, commit). A cloud's container service gives none of that: AWS Fargate (ECS) and Azure
Container Instances start a group of containers that share a volume and a network, and say when they stop. Getting
files in and out, running more than one command, and keeping a warm container waiting all need something inside the
task. Both clouds have the same parts: a task of several containers (an ECS task, an ACI container group), a volume the
containers share, containers that start in order (ECS `dependsOn`, ACI init containers), object storage with links
signed for one object and a short time (S3 presigned URLs, Blob SAS URLs), and pinned images from the cloud's registry.

## Decision

1. **`AbpSandbox.Remote` holds everything both clouds share**: an `ISandboxProvider` over two small seams, a task
   platform (`ISandboxTaskPlatform`: start a task, stop it, say whether it's running) and an object store
   (`ISandboxObjectStore`: put, get, delete and sign links). `AbpSandbox.Fargate` is S3 and ECS; `AbpSandbox.Azure`
   (later) is Blob Storage and Container Instances. A cloud provider is then a few hundred lines with no protocol of its
   own.

2. **A task is three containers sharing a volume at `/sandbox`:**
   - **init** (the runner image, as root, once): makes the work folder owned by the sandbox's user and copies a static
     shell and the work loop to `/sandbox/.abp`, owned by root and not writable by the user.
   - **work** (the operator's image, after init): the work loop, run by the copied shell as the sandbox's user, with a
     read-only root, no capabilities and no privileges. Any image works, even one with no shell of its own.
   - **courier** (the runner image, after init, as a user that isn't root or the sandbox's): the only part that talks
     to object storage. It downloads jobs, puts their files in, and uploads results.

3. **Jobs go through object storage, one object each way.** When the task starts, the provider writes a manifest of
   signed links (job N's input to read, job N's result to write, for N up to a session's count) and gives the courier
   one signed link to it. The courier polls for job N, applies it, and uploads its result:
   - `put`: files into the work folder;
   - `exec`: the work loop runs a script the provider wrote (every argument quoted), with a time limit, its stdout and
     stderr cut at the cap, its exit code;
   - `get`: a tar of paths in the work folder, links kept as links;
   - `stop`: everything exits.
   One protocol serves runs (put, exec, get, stop), sessions (any number of jobs) and the warm pool (a task started
   with no job, which waits for its first).

4. **The courier never opens what the work wrote.** It only tars it (a tar keeps a link as a link, so a link to the
   courier's own files gives nothing), reads its links from its own file (mode 600, its own user) and clears them from
   its environment. Results are read by the provider as untrusted: only regular files, within the caps.

5. **The network is the cloud's to close.** Containers in a task share it, so the work can reach what the courier
   reaches. The deployment template gives tasks private subnets with no route out except the object store's and the
   registry's endpoints, an endpoint policy that allows only the sandbox's bucket, no task role (signed links need
   none), and a DNS firewall that answers only the cloud's own names, so nothing leaves through DNS. A task's work
   folder and links last only as long as its jobs.

6. **What a cloud can't do yet is refused**: preparation (saving a container as an image needs a build service), so
   `Capabilities.Preparation` is false on Fargate and the module refuses requests with one. Sessions and the warm pool
   work.

7. **Tested without a cloud:** the protocol runs end to end on Docker, with a local task platform (the three
   containers on a shared volume) and a local object store (signed links to an HTTP server in the tests). The cloud
   adapters are then checked against the cloud itself.

## Consequences

- A job waits for the courier's next poll (under a second) and a round trip to object storage; a cold task also waits
  for the cloud to start it (tens of seconds on Fargate), which the warm pool hides.
- The work container still shares the task's network with the courier: the template's closed subnets are what keep
  it from the internet, and a deployment without them isn't a sandbox. The provider checks nothing of the network; the
  template and the troubleshooting notes say what must be true.
- Fargate takes neither `no-new-privileges` nor a process limit: the work is a user that isn't root with every
  capability dropped and a read-only root, and the time limit bounds a fork bomb. Images for Fargate should have no
  setuid programs (deploy/aws/README.md).
- The courier polls the bucket while it waits, four times a second, so a warm task costs S3 requests as well as its
  Fargate time.
- Inside a session, jobs are the same owner's: a process one job left can change what the next job reads or writes.
  That is the session's owner's own work, as on Docker.
