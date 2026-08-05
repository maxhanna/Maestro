# Benchmark command sandbox

Benchmark verification commands and benchmark-agent terminal commands run in a digest-pinned Python Docker image. Weaver does not fall back to executing benchmark commands as the Weaver process user.

The image must be present locally because the runner uses `--pull=never`:

```text
docker pull python:3.12-alpine@sha256:236173eb74001afe2f60862de935b74fcbd00adfca247b2c27051a70a6a39a2d
```

If Docker or the exact image is unavailable, command checks fail closed. The runner also:

- disables container networking;
- drops Linux capabilities and enables `no-new-privileges`;
- uses a read-only root filesystem and no network;
- limits CPU, memory, processes, temporary storage, output, and execution time;
- runs agent commands with `/bin/sh` against a temporary read-write workspace copy;
- synchronizes agent changes back only after reparse-point, hard-link, and size validation;
- copies verification input into a temporary read-only staging directory and rejects reparse points;
- allows only the verification commands declared by the benchmark policy.

The agent's benchmark files are never bind-mounted directly into the container; only a disposable staging copy is mounted.

Opt-in Docker integration tests can be run with the pinned image using:

```text
WEAVER_RUN_DOCKER_TESTS=1 dotnet test tests/UnitTests/Weaver.UnitTests.csproj
```
