#!/bin/bash
# Runs inside the Db2 container after the instance and database are created
# (the image executes scripts mounted at /var/custom). Creates the OS account that
# backs the read-only KSEXTRACT Db2 login. The password comes from the environment
# (generated per machine / per test run) and is never written to the repository.
set -euo pipefail

if [[ -z "${KS_EXTRACT_PASSWORD:-}" ]]; then
    echo "KS_EXTRACT_PASSWORD is not set; cannot create the extract user." >&2
    exit 1
fi

if ! id ksextract >/dev/null 2>&1; then
    useradd --no-create-home --shell /sbin/nologin ksextract
fi
echo "ksextract:${KS_EXTRACT_PASSWORD}" | chpasswd
echo "Extract user ksextract is ready."
