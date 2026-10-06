#!/bin/sh
# Creates the `decisya_migrator` role and the `decisya` database it owns, on the first init of
# an empty Postgres data volume (docker-entrypoint-initdb.d). Issue #120, C-06 (system
# baseline), G4-120-04, ADR-0018 D4.
#
# Stack only. With DECISYA_STACK unset (the dev AppHost) this script does nothing: Aspire's
# AddDatabase creates `decisya` there, owned by the dev superuser (C-06 allows that for dev).
#
# With DECISYA_STACK=1 the migrator password is mandatory. A missing or empty one fails the
# whole init loudly, so an empty volume can never come up with a passwordless or half-made
# migrator.
#
# The migrator is the owner of `decisya` and holds CREATEROLE (it provisions the module roles
# itself, MigrationRunner), but it is never a superuser, cannot create databases, cannot
# replicate and cannot bypass row security. PUBLIC loses CONNECT on every database here, so the
# only logins that reach a database are the roles granted it.
#
# Password handling, as in 10-keycloak-db.sh: the value comes from
# DECISYA_MIGRATOR_DB_PASSWORD_FILE (a path under /run/secrets) or, for a test fixture,
# DECISYA_MIGRATOR_DB_PASSWORD; it reaches psql only through psql's own environment (\getenv),
# never argv; statement logging is off and failed statements are not logged, so a failed
# CREATE ROLE cannot write the password to the server log. No `set -x`, and the file prints
# nothing itself: a missing or unreadable file stops the init through the shell's own redirection
# error (path only), and an empty value stops it at the `:?` guard.
#
# Sourced or executed (issue #120, regression): Aspire's WithInitFiles copies init files without
# the exec bit, so the Postgres entrypoint SOURCES this file; the stack's `stackctl assemble`
# sets the bit, so there it runs as a child process. The script must therefore never `exit` on
# a non-error path (that would end the entrypoint and the container) and must not leak shell
# options or variables into a sourcing shell. The body is a subshell function: `set -eu`, the
# password variable and every other assignment die with the subshell, and the no-op path is a
# plain `return 0`. A failure inside it (the `:?` guard, a failed redirection, psql) ends the
# subshell with a non-zero status, which the caller sees whether sourced or executed.
_decisya_db_init() (
    set -eu

    if [ "${DECISYA_STACK:-}" != "1" ]; then
        return 0
    fi

    if [ -n "${DECISYA_MIGRATOR_DB_PASSWORD_FILE:-}" ]; then
        DECISYA_MIGRATOR_DB_PASSWORD=
        IFS= read -r DECISYA_MIGRATOR_DB_PASSWORD < "$DECISYA_MIGRATOR_DB_PASSWORD_FILE" || [ -n "$DECISYA_MIGRATOR_DB_PASSWORD" ]
        export DECISYA_MIGRATOR_DB_PASSWORD
    fi

    : "${DECISYA_MIGRATOR_DB_PASSWORD:?DECISYA_MIGRATOR_DB_PASSWORD (or _FILE) must be set when DECISYA_STACK=1}"

    psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<'EOSQL'
\getenv migrator_password DECISYA_MIGRATOR_DB_PASSWORD
SET log_statement = 'none';
SET log_min_error_statement = 'panic';
CREATE ROLE decisya_migrator LOGIN PASSWORD :'migrator_password' NOSUPERUSER NOCREATEDB CREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
CREATE DATABASE decisya OWNER decisya_migrator;
REVOKE ALL ON DATABASE decisya FROM PUBLIC;
REVOKE CONNECT ON DATABASE postgres, template1 FROM PUBLIC;
\connect decisya
REVOKE ALL ON SCHEMA public FROM PUBLIC;
EOSQL
)

# The only `exit` in this file is on the error path: a failed init must stop the entrypoint
# (sourced) or the script (child process) with a non-zero status. Every failure inside the
# function is explicit (the `:?` guard exits the subshell; psql is the last command), because
# `set -e` is not honoured inside a command that is the left side of `||`.
_decisya_db_init || { unset -f _decisya_db_init; exit 1; }
unset -f _decisya_db_init
