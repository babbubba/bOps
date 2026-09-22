# ADR-0033 — Governed Docker image, build and volume management

Status: Accepted
Date: 2026-09-21

## Context

The Docker package can list and inspect containers, read their logs, and start, stop or restart them
(`docker.containers`, `docker.images`, `docker.networks`, `docker.inspect`, `docker.logs`, `docker.start`,
`docker.stop`, `docker.restart`). An operator diagnosing or repairing a host also needs to know whether an
image is present and what it is, to pull or tag one, to build an image from a local directory, and to see and
manage volumes, which hold application data.

Every one of these is either a read or a mutation with a large blast radius. A build runs the instructions of
a Dockerfile, uses network, CPU and disk, and sends a directory to the daemon. A volume removal destroys data
for good. The tools therefore have to be typed and narrow, bounded in output and duration, verified after the
fact, and must never become a route to the Docker Engine API in general, to a shell in a container, or to a
prune.

## Decision

### Nine additions, no change to the existing eight

The eight existing tools keep their names, parameters and output. Nine tools are added, all named
`docker.*`, all requiring the existing `docker` capability, all declaring `windows` and `linux`, all backed by
`Docker.DotNet` through the existing client factory:

| Tool | Risk | Explicit approval | Verified by |
|---|---|---|---|
| `docker.image.inspect` | Read | no | — |
| `docker.volumes` | Read | no | — |
| `docker.volume.inspect` | Read | no | — |
| `docker.image.pull` | Medium | no | `docker.image.inspect` |
| `docker.image.tag` | Low | no | `docker.image.inspect` |
| `docker.image.remove` | High | yes | `docker.image.inspect` |
| `docker.build` | High | yes | `docker.image.inspect` |
| `docker.volume.create` | Low | no | `docker.volume.inspect` |
| `docker.volume.remove` | High | yes | `docker.volume.inspect` |

Risk follows the effect. Removing an image or a volume, and running a Dockerfile, are High and always need a
human approval; pulling downloads and stores data and is Medium; tagging and creating an empty volume are
minor and reversible. Every non-`Read` tool declares a `VerificationSpec` and implements `IVerifiableTool`, or
it cannot register.

Not added, on purpose: container create, remove or exec; image or volume prune; push; registry credentials;
Compose. A future prune needs a prepare, manifest, approve, execute design comparable to governed recursive
deletion. There is no generic Engine API, no shell, and no argument that carries a daemon path or JSON.

### Verification arguments keep their names

The runtime carries an argument into the verification call by name (`VerificationSpec.ArgumentsFrom`), so the
argument that names the thing to verify has to be called what the verifier calls it. The image tools use
`image` for the reference that must exist (or stop existing) afterwards, and the volume tools use `volume`.
`docker.image.tag` therefore takes `source` (an existing local image) and `image` (the new reference).

### Results

Deterministic JSON with `schemaVersion: 1`. A missing image or volume is `exists: false` and a successful
read, so verification can tell absence from a reader that failed; a daemon error, a permission problem or an
unreadable answer is a failed read. Lists are sorted by name, bounded by `limit` (1 to 500, default 100) and
`maxOutputBytes` (4096 to 65536, default 32768) and say so with `truncated`.

- `docker.image.inspect`: `exists`, `reference`, `id`, `tags`, `digests`, `sizeBytes`, `createdUtc`, `os`,
  `architecture`, `variant`. Tags and digests are bounded. No labels, environment, entry point or command.
- `docker.volume.inspect` and `docker.volumes`: `exists`, `name`, `driver`, `scope`, `createdAt`, at most 20
  labels (bounded text), `optionCount`, and `usageBytes` and `referenceCount` when the daemon reports them.
  **The mount point and the driver options are not returned.** They are host paths and a local-driver bind
  target, they tell an attacker where data lives and give bOps nothing it needs.
- Mutations return what happened: the resolved id and digests after a pull, the id after a build, what was
  untagged and deleted after a removal (bounded).

### References and names

An image reference is parsed by a strict grammar (repository components, optional registry host and port,
optional tag, optional `sha256:` digest, at most 255 characters); uppercase repository names, stray
characters and query-like text are rejected before the daemon sees them. `docker.image.inspect` and
`docker.image.remove` also accept an image id (12 to 64 hexadecimal characters, optional `sha256:`);
`docker.image.pull`, the target of `docker.image.tag` and the tag of `docker.build` accept a name only, so a
hexadecimal name can never be mistaken for an id. Familiar names are normalized the way Docker does
(`docker.io/library/alpine` is `alpine:latest`) so that verification compares like with like.
A volume name is 2 to 128 characters of `[A-Za-z0-9_.-]`, starting with a letter or digit.

### Image mutations

- **Pull.** A typed reference and an optional `platform` (`os/arch[/variant]`). Anonymous registry access
  only: registry credentials are not supported in this batch and never appear in arguments, output, audit or
  telemetry; an `unauthorized` answer says so. The daemon's progress stream is consumed by bOps and reduced to
  the last error, so no progress output is unbounded.
- **Tag.** Creates one target reference from one local source. If the target already names a different image
  the call is refused (moving a tag silently is a mutation with consequences); the operator removes the old
  tag first. If it already names the same image the call is a success that changed nothing.
- **Remove.** Removes exactly the named reference or id, without force and without pruning untagged parents.
  An image in use by a container, or one that has other tags when named by id, is refused by the daemon and
  reported as a failure with the daemon's reason. A missing image is a failure, not a silent success.

### Build

`docker.build(context, dockerfile?, image)` builds one local directory into one tagged image.

- **Allowed contexts.** `Docker:Build:Contexts` is a list of directories. The context must be an existing
  directory whose fully resolved path (every symbolic link followed) is one of them or inside one. The list is
  empty by default, so nothing can be built until the operator says where; while it is empty the tool is
  hidden from the planner (capability `docker.build-contexts`), the same way a missing daemon hides `docker.*`.
  The list is Docker's own; the tool does not depend on the filesystem package's patterns.
- **Dockerfile.** A relative path inside the context, default `Dockerfile`, with no `..`, no rooted path and no
  control characters; it must be a regular file. Remote Git contexts, inline Dockerfiles and model-supplied
  Dockerfile text do not exist in the contract.
- **No links.** Any symbolic link or reparse point anywhere in the context refuses the build, whether it
  points outside or inside. This is stricter than "refuse an escape" and is deliberate: what the daemon would
  do with a link during `COPY` is not something the tool should have to reason about. It is checked while the
  archive is written, on each entry, and not only once beforehand.
- **`.dockerignore`.** The daemon does not read it (the Docker client does), and bOps does not implement its
  matching rules. A context containing one is refused with that reason, so that files the operator meant to
  exclude are never sent silently. The operator builds from a directory that holds only what the image needs.
- **Bounds.** At most `Docker:Build:MaximumContextFiles` files (default 10,000) and
  `Docker:Build:MaximumContextBytes` bytes (default 64 MiB). The context is written to a temporary archive
  that is deleted when the call ends, and the limits are enforced while writing, so an oversized context is
  refused before anything reaches the daemon. Cancellation stops the archive and the build.
- **Fixed options.** Intermediate containers are removed, the cache is used, base images are not force-pulled,
  the default network is used. There are no build arguments, no BuildKit secrets or SSH forwarding, no
  network or isolation overrides, no target, no platform, no squash and no extra hosts; the daemon receives
  exactly the archive, the Dockerfile name and one tag.
- **Output.** The image id, the file and byte count of the context, and a short bounded tail of the daemon's
  build output (at most 20 lines of 200 characters), plus the daemon's error text (bounded) on failure. The
  context and the full build log are never placed in the result, audit or telemetry.
- **Duration.** A build runs under the runner's per-tool timeout (`AgentRunner:DefaultToolTimeout`, 30 seconds
  by default). Real builds and pulls usually need more; the operator raises it, and the documentation says so.
  A cancelled or timed-out build is reported as the runtime reports every timeout, and because the effect may
  have partly happened it is verified like any other mutation.

### Volumes

`docker.volume.create` creates one named volume with driver `local` unless `Docker:Volumes:Drivers` lists
others; the `driver` argument must be on that list. There is no options bag, no labels and no driver-specific
option. Creating a volume that exists with the same driver succeeds and says `created: false`; a different
driver is refused. `docker.volume.remove` removes one named volume, never forces and never prunes; a volume
in use is refused by the daemon and reported.

### Errors are outcomes

A daemon not-found, conflict, in-use or unauthorized answer is a failed `ToolCallResult` with the daemon's
reason (bounded), never an uncaught exception. A daemon that is unreachable makes the tools disappear through
the `docker` capability, and a call that fails on a daemon that went away afterwards is a failed result.

### Audit, telemetry and untrusted data

Arguments are audited already redacted, as for every tool; none of the new arguments is a secret. Results
carry identifiers, counts and bounded daemon text. Image labels, volume labels and build output are text that
someone else wrote: labels are bounded and volumes' are the only ones returned, and build output is reduced to
a short tail. All of it reaches the model as data inside the delimited tool-result turn, like any output.

## Alternatives considered

- **`docker` CLI through a process.** Rejected: a command line is what the safety model forbids, and the
  client library already speaks the API.
- **One `docker.image` or `docker.volume` tool with an `operation` argument.** Rejected: risk, approval and
  verification belong to a tool, and a single tool would carry the highest risk of its operations for all of
  them.
- **Honouring `.dockerignore`.** Deferred, not rejected: it is a matching language of its own with negation and
  `**`, and a partial implementation would send files the operator meant to keep out. Refusing is honest.
- **Following links inside the context.** Rejected for this batch (see above).
- **Returning the volume mount point.** Rejected (see above).
- **Forcing removal, or pruning what removal leaves behind.** Rejected: both destroy more than was named.
- **Build arguments and secrets.** Rejected until a threat-modelled contract exists for values that must never
  reach the model or the log.

## Consequences

- An agent can answer "is this image here, and which one is it", pull and tag one, build a local directory, and
  create or remove a named volume, each verified afterwards, without a shell and without the Engine API.
- Builds are impossible until the operator configures a context directory, and the planner does not see the
  tool until then.
- Contexts with links or a `.dockerignore` are refused; the operator prepares a plain directory.
- A special file (a FIFO or a socket) in a context can make reading the archive wait; the tool's cancellation
  stops the call, and the file is an operator mistake the bounds do not catch. Recorded as a residual risk.
- The 30-second default tool timeout is short for a pull or a build; the operator raises it knowingly.
- No change to `bOps.Abstractions`, policy, runtime or persistence. `Docker.DotNet` stays the only dependency.
