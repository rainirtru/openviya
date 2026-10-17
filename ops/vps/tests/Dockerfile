ARG UBUNTU_VERSION=26.04
FROM ubuntu:${UBUNTU_VERSION}
ENV DEBIAN_FRONTEND=noninteractive OPENVIYA_DISPOSABLE_TEST=yes LC_ALL=C
RUN apt-get update && apt-get install -y --no-install-recommends \
    bash shellcheck openssh-server python3 sqlite3 curl ca-certificates \
    openssl iproute2 sudo && rm -rf /var/lib/apt/lists/*
RUN apt-get update && apt-get install -y --no-install-recommends \
    ufw fail2ban python3-systemd unattended-upgrades systemd certbot \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /work
CMD ["bash", "/work/ops/vps/tests/verify-bootstrap.sh"]
