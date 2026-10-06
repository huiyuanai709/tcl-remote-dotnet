# Home Assistant add-on image.
#
# Build from the repository root (this context includes the C# project):
#   docker build -f tcl-remote/Dockerfile -t ghcr.io/huiyuanai709/tcl-remote-amd64:1.0.2 .
#
# The SDK stage is Alpine, so `dotnet publish -r linux-musl-*` is a native
# musl build rather than a glibc-to-musl cross compile. The runtime image is
# Alpine plus that one binary: no .NET runtime, no Debian userland.
# Alpine is smaller than debian-slim, which matters on an HAOS box that
# pulls images from mainland China. glibc release binaries stay separate
# (linux-x64 / linux-arm64) for normal distros.

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:11.0-alpine
FROM ${SDK_IMAGE} AS build
RUN apk add --no-cache clang build-base zlib-dev
WORKDIR /src
COPY global.json ./
COPY src/TclRemote/ src/TclRemote/
RUN set -eu; \
    case "$(uname -m)" in \
      x86_64) RID=linux-musl-x64 ;; \
      aarch64) RID=linux-musl-arm64 ;; \
      *) echo "unsupported architecture $(uname -m)" >&2; exit 1 ;; \
    esac; \
    dotnet publish src/TclRemote/TclRemote.csproj \
      -c Release \
      -r "$RID" \
      -o /out \
      --nologo

FROM scratch AS binary
COPY --from=build /out/tcl-remote /tcl-remote

FROM alpine:3.22 AS runtime
# The musl AOT binary's DT_NEEDED entry is only musl. AES still dlopens
# libssl.so.3 at runtime, so OpenSSL has to be in the image.
RUN apk add --no-cache libssl3
COPY --from=build /out/tcl-remote /usr/bin/tcl-remote
COPY tcl-remote/run.sh /run.sh
RUN chmod +x /usr/bin/tcl-remote /run.sh \
    && /usr/bin/tcl-remote --version
LABEL org.opencontainers.image.source="https://github.com/huiyuanai709/tcl-remote-dotnet" \
      org.opencontainers.image.description="TCL TV LAN remote Home Assistant add-on" \
      org.opencontainers.image.licenses="MIT"
EXPOSE 8765
ENTRYPOINT ["/run.sh"]
