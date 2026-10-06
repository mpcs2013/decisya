#!/bin/sh
# Creates the dedicated `keycloak` role and database on the first init of an empty
# Postgres data volume (docker-entrypoint-initdb.d). Issue #17 (0.05); ADR-0002, ADR-0007.
#
# The role gets no more privilege than it needs (NOSUPERUSER NOCREATEDB NOCREATEROLE
# NOINHERIT), owns only its own database, and PUBLIC gets no connect privilege on it
# (G3 T-08). Later module roles (ADR-0005) go in their own numbered scripts in this
# folder, which the Compose output (0.16) and the restore drill (0.18) both reuse.
#
# Password source (issue #120, G4-120-01, ADR-0018 D3/D4):
#   - dev AppHost: DECISYA_KEYCLOAK_DB_PASSWORD in the environment (unchanged);
#   - deployable stack: DECISYA_KEYCLOAK_DB_PASSWORD_FILE, a path under /run/secrets. The
#     value is read into this shell's variable and handed to psql through its own environment,
#     never through argv and never into the container's `environment:`. The file wins over a
#     plain value. A missing or unreadable file stops the init through the shell's own
#     redirection error (which names the path, never the content), and an empty value stops it
#     at the guard below.
# This file prints nothing itself (a static test forbids any echo or printf here).
set -eu

if [ -n "${DECISYA_KEYCLOAK_DB_PASSWORD_FILE:-}" ]; then
    DECISYA_KEYCLOAK_DB_PASSWORD=
    IFS= read -r DECISYA_KEYCLOAK_DB_PASSWORD < "$DECISYA_KEYCLOAK_DB_PASSWORD_FILE" || [ -n "$DECISYA_KEYCLOAK_DB_PASSWORD" ]
    export DECISYA_KEYCLOAK_DB_PASSWORD
fi

# G3 change 2 / G4-17-15: fail fast, before psql ever runs, rather than let an unset
# password reach `\getenv` as an empty string.
: "${DECISYA_KEYCLOAK_DB_PASSWORD:?DECISYA_KEYCLOAK_DB_PASSWORD (or _FILE) must be set}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<'EOSQL'
-- G3 change 2: read the password with the psql meta-command \getenv, from the psql
-- process's own environment, never from -v on the command line (keeps it out of argv)
-- and never echoed (no `set -x`, no `echo`, no `printf` of the value, anywhere in
-- this file).
\getenv kc_password DECISYA_KEYCLOAK_DB_PASSWORD
SET log_statement = 'none';
SET log_min_error_statement = 'panic';
CREATE ROLE keycloak LOGIN PASSWORD :'kc_password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
CREATE DATABASE keycloak OWNER keycloak;
REVOKE ALL ON DATABASE keycloak FROM PUBLIC;
EOSQL
