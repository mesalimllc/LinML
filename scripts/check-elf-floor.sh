#!/usr/bin/env bash
# Fail if a shared library needs a newer glibc / libstdc++ than the oldest host
# LinML supports for it.
#
# A library built on a new distro silently picks up new symbol versions
# (GLIBC_2.43 on a current Ubuntu) and then fails to load on anything older with
# "version `GLIBC_x' not found". The patched TensorRT-RTX EP must load wherever
# the stock NVIDIA wheel does (manylinux_2_35: Ubuntu 22.04, glibc 2.35, libstdc++
# 3.4.30), so build-trt-rtx-ep.sh runs this gate on its output, and CI runs it
# over the published archive.
#
# Only symbols the library needs from elsewhere are checked (undefined dynamic
# symbols); what it defines itself is irrelevant to the host's loader.
#
# Usage:
#   check-elf-floor.sh <library.so>
#   (env overrides: MAX_GLIBC, MAX_GLIBCXX, MAX_CXXABI)
#
# Exit: 0 within the floor, 1 over it (offending symbols printed), 2 usage / tooling.

set -euo pipefail

MAX_GLIBC="${MAX_GLIBC:-2.35}"
MAX_GLIBCXX="${MAX_GLIBCXX:-3.4.30}"
MAX_CXXABI="${MAX_CXXABI:-1.3.13}"

if [ $# -ne 1 ] || [ ! -f "$1" ]; then
    echo "usage: $0 <library.so>" >&2
    exit 2
fi
LIB="$1"

command -v objdump >/dev/null 2>&1 || { echo "ERROR: objdump not found (apt-get install binutils)." >&2; exit 2; }

# Captured before parsing: see the SIGPIPE note in build-trt-rtx-ep.sh.
DUMP="$(objdump -T "$LIB")" || { echo "ERROR: objdump could not read $LIB." >&2; exit 2; }

# newer <a> <b>: true when version a is strictly greater than b.
newer() {
    [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n1)" = "$1" ]
}

# Undefined dynamic symbols look like
#   0000000000000000      DF *UND*  0000000000000000 (GLIBC_2.34) __libc_start_main
# Prints "<version> <symbol>" for every one tagged with <family>_<digit>.
needed() { # family
    awk -v fam="$1_" '
        /\*UND\*/ {
            for (i = 1; i <= NF; i++) {
                if (index($i, "(" fam) == 1) {
                    v = $i
                    gsub(/[()]/, "", v)
                    sub("^" fam, "", v)
                    if (v ~ /^[0-9]/) { print v, $NF }
                }
            }
        }' <<< "$DUMP"
}

status=0
for entry in "GLIBC:$MAX_GLIBC" "GLIBCXX:$MAX_GLIBCXX" "CXXABI:$MAX_CXXABI"; do
    family="${entry%%:*}"
    limit="${entry#*:}"
    symbols="$(needed "$family")"

    highest=""
    while read -r version _; do
        [ -n "$version" ] || continue
        if [ -z "$highest" ] || newer "$version" "$highest"; then highest="$version"; fi
    done <<< "$symbols"

    if [ -n "$highest" ] && newer "$highest" "$limit"; then
        echo "FAIL ${family}_${highest} > ${family}_${limit} required by $(basename "$LIB"); offending symbols:" >&2
        while read -r version symbol; do
            [ -n "$version" ] || continue
            if newer "$version" "$limit"; then echo "  ${family}_${version}  ${symbol}" >&2; fi
        done <<< "$symbols"
        status=1
    else
        echo "ok   ${family} max ${highest:-none} <= ${limit}"
    fi
done

if [ "$status" -ne 0 ]; then
    echo "Build in an old-glibc environment: bash scripts/build-trt-rtx-ep-docker.sh" >&2
fi
exit "$status"
