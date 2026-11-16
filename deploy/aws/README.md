# AbpSandbox on AWS Fargate

`abp-sandbox.yaml` makes everything the Fargate provider needs (docs/adr/0002), in one Region:

- **A closed network:** two private subnets with no internet gateway and no NAT. The only ways out are the S3 gateway
  endpoint, whose policy allows only the jobs bucket and the registry's layers, and the registry's interface endpoints.
- **A DNS firewall:** AWS's own names answer and every other name gets NXDOMAIN, so nothing leaves in a DNS query.
- **The jobs bucket:** private, encrypted, TLS only. Objects go after a day, and the provider removes a task's objects
  when it's done with them.
- **Two registries:** `{name}/runner` for the runner's image, and `{name}/images` for the images commands run in. Tags
  are immutable and images are scanned on push.
- **The ECS cluster,** with Fargate and Fargate Spot.
- **A task execution role** that only pulls images. Tasks get no task role.
- **`ProviderPolicy`,** to attach to the role of the server that runs AbpSandbox.

## Deploy

0. **A new account needs ECS's service-linked role** before its first cluster. Without it, the stack's `Cluster`
   fails with "Unable to assume the service linked role" and the stack rolls back. Make it once, then delete the
   rolled-back stack before deploying again:

   ```bash
   aws iam create-service-linked-role --aws-service-name ecs.amazonaws.com
   ```

1. Deploy the stack in the project's Region:

   ```bash
   aws cloudformation deploy --template-file deploy/aws/abp-sandbox.yaml --stack-name abp-sandbox --capabilities CAPABILITY_IAM --region eu-north-1
   ```

2. Build `runner/` and push it to the `RunnerRepository` output, then note its digest. Push the images commands run in to
   `ImagesRepository` the same way. A Fargate task pulls only from these, through the endpoints.
3. Attach `ProviderPolicyArn` to the server's role.
4. Set these values from the stack's outputs:

   ```json
   "AbpSandbox": {
     "Provider": "fargate",
     "Images": { "tools": "<account>.dkr.ecr.eu-north-1.amazonaws.com/abp-sandbox/images@sha256:..." },
     "Remote": { "RunnerImage": "<account>.dkr.ecr.eu-north-1.amazonaws.com/abp-sandbox/runner@sha256:..." },
     "Fargate": {
       "Region": "eu-north-1",
       "Cluster": "abp-sandbox",
       "Subnets": ["subnet-...", "subnet-..."],
       "SecurityGroups": ["sg-..."],
       "ExecutionRoleArn": "arn:aws:iam::<account>:role/...",
       "Bucket": "..."
     }
   }
   ```

   Then depend on `AbpSandboxFargateModule`.

## Check it

`tests/AbpSandbox.Tests/FargateLiveTests.cs` runs real jobs on the deployment: the courier's round trip, no way out to
the internet or to outside DNS while AWS's own names answer, no root or capabilities, the time limit, and a session.
Each test starts a Fargate task, a few cents in all. With the AWS SDK's usual credentials and the stack's outputs:

```bash
export ABP_SANDBOX_FARGATE=1 ABP_SANDBOX_FARGATE_REGION=eu-north-1 ABP_SANDBOX_FARGATE_CLUSTER=abp-sandbox   ABP_SANDBOX_FARGATE_SUBNETS=subnet-...,subnet-... ABP_SANDBOX_FARGATE_SECURITY_GROUP=sg-...   ABP_SANDBOX_FARGATE_EXECUTION_ROLE=arn:aws:iam::... ABP_SANDBOX_FARGATE_BUCKET=...   ABP_SANDBOX_FARGATE_RUNNER=<account>.dkr.ecr.eu-north-1.amazonaws.com/abp-sandbox/runner@sha256:...   ABP_SANDBOX_FARGATE_IMAGE=<account>.dkr.ecr.eu-north-1.amazonaws.com/abp-sandbox/images@sha256:...
dotnet test tests/AbpSandbox.Tests --filter FullyQualifiedName~FargateLiveTests
```

The work image needs `sh`, `wget` and `nslookup`. Alpine's busybox has them.

## What it costs

- **Running all the time, with nothing running in it:** the two registry interface endpoints, in two Availability Zones
  each, which is roughly US$30 a month in eu-north-1.
- **Per use:**
  - Fargate, per vCPU-second and GB-second, while tasks run. A warm pool's tasks count too.
  - The DNS firewall, per million queries.
  - S3 requests. The courier polls about four times a second while it waits, so a warm task waiting an hour makes about
    14,000 GET requests.
- **No charge:** the S3 gateway endpoint.

Check the project's plan and spend limit in AWS Settings before deploying.

## What Fargate can't do that Docker can

- **No `no-new-privileges`:** Fargate doesn't take it. The work runs as a user that isn't root, with every
  capability dropped and a read-only root, so a setuid program in an image gains nothing it can use. Even so, use images
  without setuid programs.
- **No process limit:** the time limit is what bounds a fork bomb.
- **No preparation:** saving a container as an image needs a build service, so requests with preparation steps are
  refused. Build the image with its tools instead and name it in `AbpSandbox:Images`.
