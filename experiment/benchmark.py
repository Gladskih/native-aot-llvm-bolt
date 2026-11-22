import argparse
import csv
import os
from pathlib import Path
import random
import resource
import statistics
import subprocess
import time


def run(executable, arguments):
    cpu_before = resource.getrusage(resource.RUSAGE_CHILDREN)
    start = time.perf_counter_ns()
    result = subprocess.run([executable, *arguments], capture_output=True, check=True)
    wall_ms = (time.perf_counter_ns() - start) / 1_000_000
    cpu_after = resource.getrusage(resource.RUSAGE_CHILDREN)
    cpu_ms = 1000 * (cpu_after.ru_utime - cpu_before.ru_utime +
                     cpu_after.ru_stime - cpu_before.ru_stime)
    return result.stdout, wall_ms, cpu_ms


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("baseline", type=lambda arg: Path(arg).resolve())
    parser.add_argument("bolted", type=lambda arg: Path(arg).resolve())
    parser.add_argument("csv", type=Path)
    parser.add_argument("--cpu", type=int, default=max(os.sched_getaffinity(0)))
    parser.add_argument("--pairs", type=int, default=21)
    parser.add_argument("--seed", default="548459")
    parser.add_argument("--workload", choices=("brotli", "gc", "json"), action="append")
    args = parser.parse_args()
    if args.pairs < 1:
        parser.error("--pairs must be positive")
    os.sched_setaffinity(0, {args.cpu})

    workloads = {
        "brotli": ["--brotli", "800", args.seed],
        "gc": ["--gc", "6000", args.seed],
        "json": ["6000000", args.seed],
    }
    binaries = {"baseline": args.baseline, "bolted": args.bolted}
    rng = random.Random(2026)
    print(f"CPU {args.cpu}, {args.pairs} pairs, seed {args.seed}", flush=True)

    with args.csv.open("w", newline="", encoding="utf-8") as output:
        writer = csv.writer(output)
        writer.writerow(["workload", "pair", "position", "binary", "wall_ms", "cpu_ms", "checksum"])
        for name in args.workload or workloads:
            arguments = workloads[name]
            expected = run(args.baseline, arguments)[0]
            if run(args.bolted, arguments)[0] != expected:
                raise RuntimeError(f"{name}: checksum mismatch during warmup")

            timings = {binary: [] for binary in binaries}
            for pair in range(1, args.pairs + 1):
                for position, binary in enumerate(rng.sample(list(binaries), len(binaries)), 1):
                    checksum, wall, cpu = run(binaries[binary], arguments)
                    if checksum != expected:
                        raise RuntimeError(f"{name}, pair {pair}, {binary}: checksum mismatch")
                    timings[binary].append((wall, cpu))
                    writer.writerow([name, pair, position, binary, f"{wall:.6f}", f"{cpu:.6f}",
                                     checksum.decode("ascii").strip()])
                output.flush()

            for metric, index in (("wall", 0), ("CPU", 1)):
                baseline = [sample[index] for sample in timings["baseline"]]
                bolted = [sample[index] for sample in timings["bolted"]]
                paired_change = [100 * (after / before - 1) for before, after in zip(baseline, bolted)]
                print(f"{name:6} {metric:4} {statistics.median(baseline):8.2f} -> "
                      f"{statistics.median(bolted):8.2f} ms | "
                      f"median paired change {statistics.median(paired_change):+6.2f}% | "
                      f"{sum(change < 0 for change in paired_change)}/{args.pairs} faster", flush=True)


if __name__ == "__main__":
    main()
