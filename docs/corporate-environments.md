# Corporate environments: custom SDK images and CA trust

TestWizz runs `dotnet restore`, `dotnet build` and `dotnet test` for proposals only inside Docker/Podman
containers. Standard installations need **no** certificate or image configuration: the SDK image is
`mcr.microsoft.com/dotnet/sdk:<version>`, picked from the test project's target framework.

Behind a TLS-inspecting corporate proxy, restore can fail with errors such as
`NU1301 ... The SSL connection could not be established` or `UntrustedRoot`. `testwizz run` then reports
`diagnosis.code: restore.tls_untrusted` (and `bootstrap` reports `run.restore.tls_untrusted`).

## Why the host's trust is not enough

A container has its own certificate store, from its image. Certificates installed on the host — in the
Windows/macOS store, in Docker Desktop's settings or in the Linux host's `/usr/local/share/ca-certificates`
— are used by the Docker *daemon* (for example to pull images) but **not** by programs running inside a
container. NuGet in the SDK container therefore does not trust your corporate CA unless the image does.

TestWizz never disables TLS validation, never mounts the host's certificate store and never reads
certificates or credentials on your behalf. Trust is established by an image you build and approve.

## 1. Build an SDK image that trusts the approved CA

Use only your organization's **public** root (or intermediate) CA certificate, in PEM format. Never add
private keys, client certificates, tokens or `nuget.config` files with credentials to an image.

```dockerfile
# Dockerfile.testwizz-sdk
FROM mcr.microsoft.com/dotnet/sdk:10.0
COPY corporate-root-ca.crt /usr/local/share/ca-certificates/corporate-root-ca.crt
RUN update-ca-certificates
```

```bash
docker build -f Dockerfile.testwizz-sdk -t testwizz-sdk:10.0-corporate .
```

Use the SDK major/minor version your test projects target. With several target frameworks, build one image per
version you need. Rebuild the image when the base image is updated.

## 2. Verify the image

```bash
# The CA is in the container's trust store:
docker run --rm testwizz-sdk:10.0-corporate \
  sh -c 'ls /etc/ssl/certs | grep -i corporate'
# NuGet's service index is reachable with full TLS validation (through your proxy, if required):
docker run --rm testwizz-sdk:10.0-corporate \
  curl --fail --silent --show-error --output /dev/null https://api.nuget.org/v3/index.json && echo TLS OK
```

## 3. Configure TestWizz

```yaml
run:
  runtime: docker
  image: testwizz-sdk:10.0-corporate
  maxRepairAttempts: 2
```

- `run.image` is optional; without it the default image from the target framework is used.
- `testwizz run --image <image>` overrides `run.image` for one run.
- The effective image is used for restore, build and test, for every repair attempt and for each
  `bootstrap` verification. It is shown as `image` in `result.json`.
- The image must be a plain reference (`[registry[:port]/]name[:tag][@digest]`); anything else is rejected
  as invalid configuration (`$.run.image`) or a usage error (`--image`).

## Isolation is unchanged

A custom image changes only which image starts. Every container still runs without capabilities, with
`no-new-privileges`, a read-only root file system and resource limits. Only restore has a network; build and
test run with `--network none`. The restored package folder is mounted read-only into build and test.
Restores are shared within one batch only when the restore files, test project **and image** are identical.
Because the cache lives only for one `run` or `bootstrap` invocation, rebuilding an image under the same tag
takes effect on the next invocation.

## Other restore diagnoses

| `diagnosis.code` | Meaning |
| --- | --- |
| `restore.image_unavailable` | The image could not be pulled or found (name, registry login, or not built locally). |
| `restore.tls_untrusted` | The feed's certificate is not trusted in the container (see above). |
| `restore.feed_auth` | A feed answered 401/403; the container has none of the host's credential providers. |
| `restore.feed_unreachable` | DNS, proxy or firewall prevented reaching a feed from the container. |
| `restore.package_not_found` | NU1101/NU1102: a package or version is not on the configured feeds. |

Diagnosis messages are fixed text; they never quote the restore log, so feed URLs or tokens in the log are not
repeated in terminal or JSON output. The full log remains in `<bundle>/run/restore.log` for local review.
