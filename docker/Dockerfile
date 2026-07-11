# E2E test image: builds DiskWeaver and runs the full loop-device validation
# workflow (docs/testing.md) against real mdadm/parted/lvm2/util-linux inside
# the container. Must run with --privileged (see docker/README.md).

FROM mcr.microsoft.com/dotnet/sdk:10.0

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        mdadm \
        lvm2 \
        parted \
        util-linux \
        e2fsprogs \
        jq \
    && rm -rf /var/lib/apt/lists/*

# Default lvm.conf on some distros' filters can reject loop devices entirely,
# silently dropping pvcreate/vgcreate -- see docs/testing.md's LVM filter
# gotcha. Loosened here on purpose: this image only ever runs disposable,
# --privileged test containers, never a production host.
RUN mkdir -p /etc/lvm/lvm.conf.d \
    && printf 'devices {\n    filter = [ "a|.*|" ]\n}\n' > /etc/lvm/lvm.conf.d/99-diskweaver-test.conf

WORKDIR /src
COPY . .
RUN dotnet build src/DiskWeaver.slnx -c Release \
    && chmod +x docker/e2e-test.sh

ENTRYPOINT ["docker/e2e-test.sh"]
