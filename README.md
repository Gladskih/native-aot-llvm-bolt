# LLVM BOLT with .NET 10 Native AOT on Linux x86-64

## Abstract

We tested LLVM BOLT on Linux x86-64 executables built with .NET 10 Native AOT. Whole-file BOLT processing terminated with `SIGSEGV`, including on a one-line program. Restricting BOLT to a statically linked Brotli encoder produced an executable that passed the workload checks. Compression retired 0.51% fewer branches, wall-time differences stayed within measurement variability, and the stripped executable grew by 10.83%.

## Minimal reproducer

The test was run on Ubuntu 26.04 LTS in WSL2 with .NET SDK 10.0.114, ILCompiler 10.0.14, Clang 21.1.8 and GNU ld 2.46. The [source](repro/Program.cs) is `return 0;`. The [project](repro/Repro.csproj) enables Native AOT, keeps symbols and passes `--emit-relocs` to the native linker. Run these commands from the repository root in an x86-64 Linux environment with the [.NET Native AOT build prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) and BOLT on `PATH`.

```sh
work=$(mktemp -d)
dotnet publish repro/Repro.csproj -c Release -r linux-x64 -o "$work/publish"
strip --strip-debug -o "$work/Repro" "$work/publish/Repro"
"$work/Repro"
echo "Application exit status: $?"
ulimit -c 0
llvm-bolt "$work/Repro" -o "$work/Repro.bolted"
echo "BOLT exit status: $?"
```

The input ELF exits with status `0`. BOLT exits with status `139` both with and without `--instrument` on Ubuntu BOLT 22.1.2, the apt.llvm.org build of 23.1.6 (`5b10d2d74929`), and the 24.0.0 development snapshot from apt.llvm.org (`51d7f08b16b6`). The 22.1.2 backtrace, symbolized using the matching Ubuntu debug symbols, shows `llvm::bolt::BinaryFunction::parseLSDA` called from `llvm::bolt::RewriteInstance::disassembleFunctions`. The backtrace identifies the failing code path. The underlying compatibility issue remains open for investigation.

## Profile-guided experiment

The benchmark uses a separate Native AOT application with three synthetic workloads. We published it once and produced both measured executables from the same linked ELF. BOLT 22.1.2 instrumented only functions selected from the linked `libbrotlienc.a`. The training run compressed 1,600 blocks at Brotli quality 5 with seed `187201`. BOLT used the collected branch counts for block and function reordering and cold-block splitting. Both measured executables were stripped using the same command.

```sh
bash experiment/build.sh out
python3 experiment/benchmark.py out/baseline/Telemetry out/bolted/Telemetry \
    out/benchmark.csv --cpu 6 --pairs 25 --seed 2938111
```

The build script defaults to `llvm-bolt-22`. Set `LLVM_BOLT` to select a different binary. Use an available logical CPU when running the benchmark on another machine. The timing script pins both executables to the same logical CPU, randomizes their order within each pair and checks the workload checksum on each run. Both executables also passed a self-test covering JSON parsing, exception handling, forced compacting GC and Brotli round trips.

### Results

We ran 25 pairs per workload on an AMD Ryzen 7 9800X3D under WSL2. Wall-time columns show separate medians. The paired effect is the median of `(BOLT / baseline − 1) × 100` across pairs. The 95% percentile bootstrap intervals use 10,000 resamples of whole pairs with replacement and a fixed seed of `13`. [Raw timing data](experiment/data/benchmark.csv)

| Workload | Baseline wall time | BOLT wall time | Paired change | 95% interval |
| --- | ---: | ---: | ---: | ---: |
| Brotli compression, 800 blocks | 749.00 ms | 750.02 ms | +0.02% | −1.09% to +1.04% |
| GC workload, 6,000 rounds | 754.43 ms | 752.14 ms | −0.07% | −2.30% to +1.15% |
| JSON parsing, 6 million records | 689.62 ms | 686.48 ms | −0.40% | −2.22% to +1.31% |

In 11 additional pinned pairs of the Brotli workload, `perf stat` measured 0.508% fewer user-mode branches and 0.081% fewer user-mode instructions by paired median. Both counts fell in all 11 pairs. The command for each run was `taskset -c 6 perf stat -e cycles:u,instructions:u,branches:u,branch-misses:u -- BINARY --brotli 800 2938111`, with the execution order randomized within each pair. [Raw counter data](experiment/data/perf-stat.csv)

The stripped file grew from 2,205,656 to 2,444,552 bytes. The address and size of `__managedcode` stayed the same. Eight bytes changed at the two call relocations for `BrotliEncoderMaxCompressedSize` and `BrotliEncoderCompress`. The `.dotnet_eh_table` and `__unbox` sections matched byte for byte.

### Limitations

The measurements cover one application, one host and synthetic inputs from the same generator with distinct training and evaluation seeds. The only functions selected for transformation in the benchmark came from the native Brotli encoder. The GC and JSON workloads measured changes outside the profiled compression workload. BOLT reported 145 relocations it could not analyze and 15 profiled functions it could not optimize. All timing intervals include zero.

## Acknowledgment

OpenAI Codex assisted with test-case reduction, experiment execution, analysis and editing.
