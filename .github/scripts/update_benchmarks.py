#!/usr/bin/env python3
"""Turns the BenchmarkDotNet reports from each CI runner into compact tables, and writes them into the
README between the ci-benchmarks markers (and into the job summary when running in GitHub Actions).

Usage: update_benchmarks.py <results dir> <README path>

<results dir> holds one folder per runner (benchmarks-Linux, benchmarks-Windows, benchmarks-macOS), each
containing BenchmarkDotNet's *-report-github.md files.
"""

import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

START = "<!-- ci-benchmarks:start -->"
END = "<!-- ci-benchmarks:end -->"
SYSTEMS = ["Linux", "Windows", "macOS"]
UPSTREAM, OURS, NO_QUEUE = "SerilogSinksAsync", "AsyncRing", "NoQueue"
NANOSECONDS_PER_UNIT = {"ns": 1.0, "us": 1e3, "μs": 1e3, "µs": 1e3, "ms": 1e6, "s": 1e9}


def parse_report(path):
    """Returns the environment lines and the table rows (as dicts) of a BenchmarkDotNet GitHub report."""
    text = path.read_text(encoding="utf-8")
    parts = text.split("```")
    environment = parts[1].strip().splitlines() if len(parts) >= 3 else []

    header, rows = None, []
    for line in text.splitlines():
        if not line.startswith("|"):
            continue
        cells = [cell.strip().replace("*", "") for cell in line.strip().strip("|").split("|")]
        if header is None:
            header = cells
        elif all(set(cell) <= set("-: ") for cell in cells):
            continue  # the separator under the header, or a blank row between groups
        else:
            rows.append(dict(zip(header, cells)))

    # With a single runtime, BenchmarkDotNet leaves the Runtime column out; take it from the job line instead
    # ("  Job-ABCDEF : .NET 10.0.0 (...)").
    job_runtime = next((re.search(r":\s*(\.NET [\d.]+)", line).group(1)
                        for line in environment if "Job-" in line and re.search(r":\s*\.NET [\d.]+", line)), "")
    for row in rows:
        row.setdefault("Runtime", job_runtime)
    return environment, rows


def nanoseconds(text):
    match = re.match(r"([\d,.]+)\s*(\S+)", text or "")
    if not match or match.group(2) not in NANOSECONDS_PER_UNIT:
        return None
    return float(match.group(1).replace(",", "")) * NANOSECONDS_PER_UNIT[match.group(2)]


def runtime_version(runtime):
    match = re.search(r"(\d+)(?:\.(\d+))?", runtime)
    return (int(match.group(1)), int(match.group(2) or 0)) if match else (0, 0)


def by_case(rows):
    """Groups rows as {(runtime, threads): {method: row}}, ordered newest runtime first, then by threads."""
    cases = {}
    for row in rows:
        cases.setdefault((row.get("Runtime", ""), int(row["Threads"])), {})[row["Method"]] = row
    return dict(sorted(cases.items(), key=lambda item: (tuple(-v for v in runtime_version(item[0][0])), item[0][1])))


def runtime_label(runtime):
    major, minor = runtime_version(runtime)
    return f".NET {major}" if minor == 0 else f".NET {major}.{minor}"


def time(ns):
    return "–" if ns is None else (f"{ns:,.0f} ns" if ns >= 100 else f"{ns:,.1f} ns")


def rate(ns):
    return "–" if not ns else f"{1e9 / ns / 1e6:,.1f}M/s"


def comparison(upstream_ns, ours_ns):
    if not upstream_ns or not ours_ns:
        return "–"
    ratio = upstream_ns / ours_ns
    if 1 / 1.05 <= ratio <= 1.05:
        return "about the same"
    return f"{ratio:.1f}× faster" if ratio >= 1 else f"{1 / ratio:.1f}× slower"


def mean(methods, method):
    return nanoseconds(methods.get(method, {}).get("Mean"))


def table(header, lines):
    out = ["| " + " | ".join(header) + " |", "|" + "---|" * len(header)]
    out += ["| " + " | ".join(line) + " |" for line in lines]
    return "\n".join(out)


def log_call_table(rows):
    lines = []
    for (runtime, threads), methods in by_case(rows).items():
        up, ours, none = mean(methods, UPSTREAM), mean(methods, OURS), mean(methods, NO_QUEUE)
        lines.append([runtime_label(runtime), str(threads), time(up), time(ours), time(none), comparison(up, ours)])
    return table(["Runtime", "Threads", "Serilog.Sinks.Async", "AsyncRing", "No queue", "AsyncRing is"], lines)


def throughput_table(rows):
    lines = []
    for (runtime, threads), methods in by_case(rows).items():
        up, ours, none = mean(methods, UPSTREAM), mean(methods, OURS), mean(methods, NO_QUEUE)
        lines.append([runtime_label(runtime), str(threads), rate(up), rate(ours), rate(none), comparison(up, ours)])
    return table(["Runtime", "Threads", "Serilog.Sinks.Async", "AsyncRing", "No queue", "AsyncRing is"], lines)


def overload_table(rows):
    def cell(methods, method):
        row = methods.get(method)
        return "–" if row is None else f"{time(nanoseconds(row.get('Mean')))}, {row.get('Delivered', '?')} delivered"

    lines = [[runtime_label(runtime), str(threads), cell(methods, UPSTREAM), cell(methods, OURS)]
             for (runtime, threads), methods in by_case(rows).items()]
    return table(["Runtime", "Threads", "Serilog.Sinks.Async", "AsyncRing"], lines)


SINK_NAMES = {UPSTREAM: "Serilog.Sinks.Async", OURS: "AsyncRing", NO_QUEUE: "No queue"}


def resources_table(rows):
    """Memory in use and allocation rates, one row per sink, runtime and thread count."""
    def value(row, column):
        return row.get(column) or "–"

    lines = []
    for (runtime, threads), methods in by_case(rows).items():
        for method in (UPSTREAM, OURS, NO_QUEUE):
            row = methods.get(method)
            if row is None:
                continue
            memory = f"{value(row, 'Memory avg')} / {value(row, 'Memory peak')}"
            lines.append([runtime_label(runtime), str(threads), SINK_NAMES[method], memory,
                          value(row, "Allocations/s"), value(row, "Allocated/s"), value(row, "Allocated/event")])
    return table(["Runtime", "Threads", "Sink", "Memory in use (avg / peak)", "Allocations/s",
                  "Allocated/s", "Allocated/event"], lines)


def resources_section(parsed):
    if not any("Memory avg" in row for _, rows in parsed.values() for row in rows):
        return ""
    return "\n".join([
        "<details>\n<summary>Memory and allocations</summary>\n",
        "Memory in use is the managed heap after garbage collections, above what it was before the logger existed "
        "(average over time / peak). Allocations per second are estimated from the runtime's allocation sampling; "
        "allocated bytes are exact. Faster sinks log more events per second, so they allocate more per second: "
        "compare the bytes per event.\n",
        "**Cost of a logging call**\n",
        resources_table(parsed["LogCallBenchmarks"][1]) + "\n",
        "**Throughput**\n",
        resources_table(parsed["ThroughputBenchmarks"][1]) + "\n",
        "**Overload with the default 10,000-event buffer**\n",
        resources_table(parsed["OverloadBenchmarks"][1]) + "\n",
        "</details>\n",
    ])


def headline(rows):
    """'AsyncRing is 9.8× faster per logging call with 16 threads', from the newest runtime and most threads."""
    cases = by_case(rows)
    if not cases:
        return "no results"
    newest = max(runtime_version(runtime) for runtime, _ in cases)
    runtime, threads = max((key for key in cases if runtime_version(key[0]) == newest), key=lambda key: key[1])
    methods = cases[(runtime, threads)]
    compared = comparison(mean(methods, UPSTREAM), mean(methods, OURS))
    return "results" if compared == "–" else f"AsyncRing is {compared} per logging call with {threads} threads"


def system_section(name, folder):
    reports = {report: folder / f"AsyncRingBenchmarks.{report}-report-github.md"
               for report in ("LogCallBenchmarks", "ThroughputBenchmarks", "OverloadBenchmarks")}
    if not any(path.exists() for path in reports.values()):
        return f"<details>\n<summary><b>{name}</b>: no results</summary>\n\nThe benchmark job didn't produce results.\n\n</details>"

    parsed = {report: parse_report(path) if path.exists() else ([], []) for report, path in reports.items()}
    environment = next((env for env, _ in parsed.values() if env), [])
    operating_system = environment[0].split(",", 1)[1].strip() if environment and "," in environment[0] else ""
    cpu = environment[1].replace(", 1 CPU", "") if len(environment) > 1 else ""

    parts = [
        f"<details>\n<summary><b>{name}</b>: {headline(parsed['LogCallBenchmarks'][1])}</summary>\n",
        " · ".join(part for part in (operating_system, cpu) if part) + "\n",
        "**Cost of a logging call** (per call, on each logging thread; nothing dropped)\n",
        log_call_table(parsed["LogCallBenchmarks"][1]) + "\n",
        "**Throughput** (events per second reaching the sink)\n",
        throughput_table(parsed["ThroughputBenchmarks"][1]) + "\n",
        "**Overload with the default 10,000-event buffer** (per call, and the share of events not dropped)\n",
        overload_table(parsed["OverloadBenchmarks"][1]) + "\n",
        resources_section(parsed),
        "</details>",
    ]
    return "\n".join(part for part in parts if part)


def main():
    results, readme = Path(sys.argv[1]), Path(sys.argv[2])

    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    commit = os.environ.get("GITHUB_SHA", "")[:7]
    run_url = os.environ.get("RUN_URL")
    source = f"commit `{commit}`" if commit else "a local run"
    if run_url:
        source += f" ([workflow run]({run_url}))"

    sections = [f"Last updated {now} from {source}.\n"]
    for name in SYSTEMS:
        sections.append(system_section(name, results / f"benchmarks-{name}") + "\n")
    generated = "\n".join(sections)

    text = readme.read_text(encoding="utf-8")
    if START not in text or END not in text:
        sys.exit(f"{readme} has no {START} ... {END} markers")
    before, rest = text.split(START, 1)
    _, after = rest.split(END, 1)
    readme.write_text(f"{before}{START}\n{generated}\n{END}{after}", encoding="utf-8", newline="\n")

    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as file:
            file.write("## Benchmark results\n\n" + generated + "\n")


if __name__ == "__main__":
    main()
