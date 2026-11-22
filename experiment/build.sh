#!/usr/bin/env bash
set -euo pipefail
ulimit -c 0

project=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
if [[ $# -gt 1 ]]; then
    echo "Usage: bash build.sh [new-output-directory]" >&2
    exit 2
fi
if [[ $# -eq 1 ]]; then
    mkdir -- "$1"
    output=$(cd -- "$1" && pwd)
else
    output=$(mktemp -d "${TMPDIR:-/tmp}/native-aot-bolt.XXXXXXXX")
fi
printf 'Output directory: %s\n' "$output"

bolt=${LLVM_BOLT:-llvm-bolt-22}
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

cd "$output"
mkdir source input instrumented baseline bolted
cp "$project/Telemetry.csproj" "$project/Program.cs" source/
dotnet publish source/Telemetry.csproj -c Release -r linux-x64 -p:BoltInput=true \
    -p:BoltArchivePathFile="$output/brotli-encoder-archive.txt" -o input \
    2>&1 | tee publish.log
strip --strip-debug -o input/Telemetry.bolt-input input/Telemetry
readelf -W -h -l -S -n input/Telemetry.bolt-input > input.elf.txt

# Select functions from the encoder archive used by the NativeAOT linker.
read -r encoder_archive < brotli-encoder-archive.txt
nm -P --defined-only "$encoder_archive" | awk '$2 ~ /^[tT]$/ {print $1}' | sort -u \
    | sed 's/\./\\./g; s/^/^/; s/$/(\/.*)?$/' > brotli-functions.txt
test -s brotli-functions.txt

profile_seed=187201
profile_rounds=1600
"$bolt" input/Telemetry.bolt-input --funcs-file=brotli-functions.txt --no-huge-pages \
    --instrument --instrumentation-file="$output/brotli.fdata" -o instrumented/Telemetry \
    > instrument.log 2>&1
instrumented/Telemetry --self-test
profile_checksum=$(instrumented/Telemetry --brotli "$profile_rounds" "$profile_seed")
test -s brotli.fdata

"$bolt" input/Telemetry.bolt-input --funcs-file=brotli-functions.txt --no-huge-pages \
    --data=brotli.fdata --reorder-blocks=ext-tsp --reorder-functions=cdsort \
    --split-functions --split-all-cold --cold-threshold=25 --dyno-stats \
    -o bolted/Telemetry.unstripped \
    > bolt.log 2>&1

strip --strip-all -o baseline/Telemetry input/Telemetry.bolt-input
strip --strip-all -o bolted/Telemetry bolted/Telemetry.unstripped
readelf -W -h -l -S -n baseline/Telemetry > baseline.elf.txt
readelf -W -h -l -S -n bolted/Telemetry > bolted.elf.txt

for binary in baseline/Telemetry bolted/Telemetry; do
    "$binary" --self-test
    test "$("$binary" --brotli "$profile_rounds" "$profile_seed")" = "$profile_checksum"
done

stat -c '%s bytes %n' baseline/Telemetry bolted/Telemetry
sha256sum baseline/Telemetry bolted/Telemetry
