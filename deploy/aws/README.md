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
