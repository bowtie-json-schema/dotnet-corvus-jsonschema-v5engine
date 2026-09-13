FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
ARG TARGETARCH

# Native AOT needs a C toolchain. The arm64 image is cross-compiled on the amd64 build host: Ubuntu keeps its
# arm64 packages on ports.ubuntu.com, so that archive is added (and the stock one pinned to the host architecture)
# before the arm64 cross toolchain and zlib are installed.
RUN if [ "${TARGETARCH}" = "arm64" ] && [ "$(dpkg --print-architecture)" != "arm64" ]; then \
        sed -i 's/^Components: .*/&\nArchitectures: amd64/' /etc/apt/sources.list.d/ubuntu.sources \
        && printf 'Types: deb\nURIs: http://ports.ubuntu.com/ubuntu-ports\nSuites: noble noble-updates noble-security\nComponents: main universe\nArchitectures: arm64\nSigned-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\n' > /etc/apt/sources.list.d/ubuntu-ports-arm64.sources \
        && dpkg --add-architecture arm64 \
        && apt-get update \
        && apt-get install -y --no-install-recommends clang zlib1g-dev gcc-aarch64-linux-gnu binutils-aarch64-linux-gnu zlib1g-dev:arm64; \
    else \
        apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev; \
    fi \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /source

COPY *.csproj .
RUN dotnet restore -a ${TARGETARCH}

COPY . .
# When cross-compiling, the symbol strip at the end of the AOT publish must use the cross objcopy.
RUN if [ "${TARGETARCH}" = "arm64" ] && [ "$(dpkg --print-architecture)" != "arm64" ]; then objcopy="-p:ObjCopyName=aarch64-linux-gnu-objcopy"; fi \
    && dotnet publish -a ${TARGETARCH} --no-restore -c Release -o /app ${objcopy}

# runtime-deps carries ICU and the native libraries the AOT binary links against; no .NET runtime is needed.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app
COPY --from=build /app/bowtie_corvus_jsonschema_v5engine .
ENTRYPOINT ["./bowtie_corvus_jsonschema_v5engine"]
