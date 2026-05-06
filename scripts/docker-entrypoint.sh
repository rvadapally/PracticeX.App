#!/usr/bin/env sh
# PracticeX API container entrypoint.
#
# Translates the conventional DATABASE_URL or RENDER-style postgres:// URL
# into the Npgsql ADO.NET keyword form PracticeX expects under
# ConnectionStrings__PracticeX. If ConnectionStrings__PracticeX is already
# set explicitly, we do nothing and just exec the dotnet host.
#
# Accepts forms like:
#   postgres://user:pass@host:5432/db
#   postgresql://user:pass@host:5432/db?sslmode=require
#
# Output:
#   Host=host;Port=5432;Database=db;Username=user;Password=pass;SSL Mode=Require;Trust Server Certificate=true
#
# Trust Server Certificate is set whenever ssl is required because Render /
# Fly / Supabase use platform-managed certs that .NET's default chain
# validation does not always accept; the connection is still encrypted.

set -e

translate() {
    url="$1"
    case "$url" in
        postgres://*|postgresql://*) ;;
        *) printf '%s' "$url"; return ;;
    esac

    rest="${url#*://}"
    creds="${rest%%@*}"
    host_db="${rest#*@}"

    user="${creds%%:*}"
    pass="${creds#*:}"
    [ "$pass" = "$creds" ] && pass=""

    hostport_db="${host_db%%\?*}"
    query=""
    case "$host_db" in *\?*) query="${host_db#*\?}" ;; esac

    hostport="${hostport_db%%/*}"
    db="${hostport_db#*/}"

    host="${hostport%%:*}"
    port="${hostport#*:}"
    [ "$port" = "$hostport" ] && port="5432"

    out="Host=${host};Port=${port};Database=${db};Username=${user};Password=${pass}"

    sslmode=""
    if [ -n "$query" ]; then
        old_ifs="$IFS"; IFS='&'
        for kv in $query; do
            k="${kv%%=*}"; v="${kv#*=}"
            case "$k" in sslmode|ssl) sslmode="$v" ;; esac
        done
        IFS="$old_ifs"
    fi

    case "$sslmode" in
        require|verify-ca|verify-full)
            out="${out};SSL Mode=Require;Trust Server Certificate=true" ;;
        disable)
            out="${out};SSL Mode=Disable" ;;
        *)
            # Render/Fly require TLS by default; assume require if scheme was
            # postgres:// and no override was provided.
            out="${out};SSL Mode=Require;Trust Server Certificate=true" ;;
    esac

    printf '%s' "$out"
}

if [ -z "${ConnectionStrings__PracticeX:-}" ]; then
    src=""
    if [ -n "${DATABASE_URL:-}" ]; then
        src="$DATABASE_URL"
    elif [ -n "${PRACTICEX_DATABASE_URL:-}" ]; then
        src="$PRACTICEX_DATABASE_URL"
    fi
    if [ -n "$src" ]; then
        export ConnectionStrings__PracticeX="$(translate "$src")"
        echo "[entrypoint] Translated database URL to Npgsql keyword form."
    fi
fi

exec dotnet PracticeX.Api.dll "$@"
