# Docker image, build and volume management

The Docker package lists and inspects containers, reads their logs, and starts, stops and restarts them
(`docker.containers`, `docker.images`, `docker.networks`, `docker.inspect`, `docker.logs`, `docker.start`,
`docker.stop`, `docker.restart`). V1.3-B adds nine typed tools for images, builds and volumes
([ADR-0033](architecture/adr/0033-governed-docker-image-build-and-volume-management.md)).

All of them talk to the local daemon through `Docker.DotNet`, over the named pipe (Windows) or the Unix socket
(Linux) or the endpoint in `Docker:Endpoint`. None of them accepts an Engine API path, a raw JSON body, a shell
command or a registry credential. A daemon that cannot be reached hides every `docker.*` tool from the planner;
a daemon that goes away in the middle of a call is a failed result, not a crash.

## The tools

| Tool | Risk | Approval | Does |
|---|---|---|---|
| `docker.image.inspect` | Read | no | Whether one image is present, and its id, tags, digests, size, creation time and platform. |
| `docker.volumes` | Read | no | Lists volumes, sorted by name, bounded. |
| `docker.volume.inspect` | Read | no | Whether one volume exists, and its driver, scope, labels and usage. |
| `docker.image.pull` | Medium | by policy | Pulls one image (anonymous registry access) and returns its local id and digests. |
| `docker.image.tag` | Low | by policy | Adds one `repository:tag` reference to an existing local image. |
| `docker.image.remove` | High | always | Removes one image reference or id. Never forced, never pruned. |
| `docker.build` | High | always | Builds one allowed local directory into one tagged image. |
| `docker.volume.create` | Low | by policy | Creates one named, empty volume. |
| `docker.volume.remove` | High | always | Removes one named volume and its data. Never forced, never pruned. |

"Always" means the tool asks for a human approval even when the policy would let it run on its own. Every tool
that changes something is checked afterwards by a read tool (`docker.image.inspect` or `docker.volume.inspect`), and
the result says whether the effect was confirmed, refuted or could not be checked.

**Deliberately absent:** container create, remove or exec, image or volume prune, push, Compose, registry
credentials, build arguments and secrets. There is no way to run a command in a container.

## Arguments

**References.** An image reference is `repository[:tag]` or `repository@sha256:digest`, lower case, with an optional
registry host (`ghcr.io/owner/app:1`, `localhost:5000/app`). `docker.image.inspect` and `docker.image.remove` also
accept an image id (12 to 64 hexadecimal characters, with or without `sha256:`); the other tools accept a name only.
Names are normalized the way Docker prints them, so `docker.io/library/alpine` and `alpine` are `alpine:latest`.
A volume name is 2 to 128 letters, digits, `_`, `.` or `-`, starting with a letter or digit.

| Tool | Arguments |
|---|---|
| `docker.image.inspect` | `image` |
| `docker.image.pull` | `image`; optional `platform` (`linux/amd64`, `linux/arm/v7`) |
| `docker.image.tag` | `source` (an existing image), `image` (the **new** reference) |
| `docker.image.remove` | `image` |
| `docker.build` | `context` (absolute path), `image` (the tag to give), optional `dockerfile` (default `Dockerfile`) |
| `docker.volumes` | optional `limit` (1 to 500, default 100), `maxOutputBytes` (4096 to 65536, default 32768) |
| `docker.volume.inspect`, `docker.volume.remove` | `volume` |
| `docker.volume.create` | `volume`; optional `driver` (must be an allowed driver) |

A value out of range is refused with the reason, never adjusted.

## Results

JSON with `"schemaVersion": 1`. A missing image or volume is `"exists": false` and a *successful* read, so "it is not
there" can be told from "the daemon could not be read". Lists are sorted by name and say `"truncated": true` when they
were cut by the limit or the byte budget.

- **Volumes never show their mount point or driver options**, only the number of options. They are host paths and,
  for the `local` driver, a bind target; bOps does not need them.
- `docker.image.tag` refuses to **move** a tag that already names a different image: remove the old tag first. Tagging
  the same image again succeeds with `"created": false`.
- `docker.image.remove` of an image a container uses, or of an image with several tags when named by id, is refused by
  the daemon (`409 Conflict`) and reported with its reason.
- `docker.volume.create` of a volume that exists with the same driver succeeds with `"created": false`.

## Building an image

`docker.build` is off until you say where it may build from.

```json
{
  "Docker": {
    "Build": {
      "Contexts": [ "C:\\build-contexts", "/srv/build-contexts" ],
      "MaximumContextFiles": 10000,
      "MaximumContextBytes": 67108864
    },
    "Volumes": { "Drivers": [ "local" ] }
  }
}
```

(In `appsettings.json` of the API or the CLI, or as environment variables such as `Docker__Build__Contexts__0`.)

- `Contexts` lists the directories a context must be, or be inside of, after every symbolic link is followed. The
  default is none, and while it is empty the tool does not appear in what the planner sees.
- The Dockerfile is a relative path inside the context, with no `..`.
- **Refused** before anything reaches the daemon: a context outside the list, a symbolic link or reparse point anywhere
  in the context (pointing in or out), a `.dockerignore` (bOps does not apply it, and refuses rather than send files you
  meant to exclude), more than `MaximumContextFiles` files or `MaximumContextBytes` bytes, a missing Dockerfile.
  Build from a directory that holds only what the image needs.
- The daemon receives exactly the archive, the Dockerfile name and one tag. Intermediate containers are removed and the
  cache is used; there are no build arguments, no secrets or SSH forwarding, and no network or privilege options.
- The result is the image id, how many files and bytes the context had, and the last few lines of the daemon's output
  (at most 20 lines of 200 characters). The context and the full log are never returned, audited or sent to telemetry.
- A special file (a FIFO or a socket) in a context can make reading it wait; the tool's timeout stops the call. Do not
  put one there.

**Timeouts.** A build or a pull runs under the runner's tool timeout, `AgentRunner:DefaultToolTimeout`, which is 30
seconds. Real builds and pulls usually need more, so raise it (for example `"00:10:00"`) if you allow them. A call
that times out is reported as a timeout and, because it may have partly happened, is verified like any other change.

## Volume drivers

`docker.volume.create` uses `local` unless `Docker:Volumes:Drivers` lists others; the `driver` argument must be on the
list. There is no options bag and no labels.

## Safety

- **Registry credentials are not supported.** Pulls are anonymous, and an `unauthorized` answer says so.
- **Image and volume labels and build output are text someone else wrote.** They reach the model as data inside the
  delimited tool-result turn, bounded, and never change what the runtime does.
- **Audit records the arguments** (already redacted, as for every tool) and the outcome. None of these arguments is a
  secret, and no result carries a context, a full log or a message from the daemon beyond one bounded line.
- **Risk follows the effect.** Removing an image or a volume, and running a Dockerfile, are High and need a human;
  a volume removal destroys data permanently, and an image built locally cannot be pulled back.
