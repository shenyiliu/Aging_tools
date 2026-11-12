#!/usr/bin/env python3
"""Read retained acceptance evidence; never launch a test engine or rewrite its evidence.

Uses Python's standard library for analysis and SVG plots. A tiny isolated .NET
probe calls the already-built production report validators, without rebuilding
any application output. Re-run after a live acceptance run reaches a terminal state.
"""
from __future__ import annotations

import argparse
import csv
import ctypes
from ctypes import wintypes
from datetime import datetime
import hashlib
import io
import json
import math
import os
from pathlib import Path
import re
import shutil
import statistics
import subprocess
import sys
from xml.sax.saxutils import escape


METRICS = {
    "CPU%": "CpuPercent", "GPU%": "GpuPercent", "Memory%": "MemoryPercent",
    "DiskActive%": "DiskActivePercent", "DiskReadBytesPerSecond": "DiskReadBytesPerSecond",
    "DiskWriteBytesPerSecond": "DiskWriteBytesPerSecond",
}
SOURCE_DIRS = (
    ("20261112-hardware-elevated", "real-burnin-mode0-six-minute", "BurnIn 0", "BurnIn", 0),
    ("20261112-hardware-elevated", "real-burnin-mode1-six-minute", "BurnIn 1", "BurnIn", 1),
    ("20261112-hardware-elevated", "real-burnin-mode2-six-minute", "BurnIn 2", "BurnIn", 2),
    ("20261112-furmark", "real-furmark-six-minute", "FurMark initial", "FurMark", -1),
    ("20261112-furmark-final", "real-furmark-six-minute", "FurMark final", "FurMark", -1),
)


def read_shared(path: Path) -> bytes:
    """Permit concurrent append/atomic replacement when taking a read-only snapshot."""
    if os.name != "nt":
        return path.read_bytes()
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
        wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.ReadFile.argtypes = [wintypes.HANDLE, wintypes.LPVOID, wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD), wintypes.LPVOID]
    kernel.ReadFile.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.CreateFileW(str(path), 0x80000000, 7, None, 3, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    chunks = []
    try:
        buffer = ctypes.create_string_buffer(1024 * 1024)
        count = wintypes.DWORD()
        while True:
            if not kernel.ReadFile(handle, buffer, len(buffer), ctypes.byref(count), None):
                raise ctypes.WinError(ctypes.get_last_error())
            if count.value == 0:
                return b"".join(chunks)
            chunks.append(buffer.raw[:count.value])
    finally:
        kernel.CloseHandle(handle)


def snapshot(path: Path) -> tuple[dict, str]:
    data = read_shared(path)
    encoding = "utf-16" if data.startswith((b"\xff\xfe", b"\xfe\xff")) else "utf-8-sig"
    if encoding == "utf-8-sig" and data[:200].count(b"\0") > 40:
        encoding = "utf-16-le"
    try:
        text = data.decode(encoding)
    except UnicodeDecodeError:
        encoding = "cp1252"  # FurMark 1.x score files use a legacy degree symbol.
        text = data.decode(encoding, errors="replace")
    return ({"Path": str(path.resolve()), "Length": len(data),
        "Sha256": hashlib.sha256(data).hexdigest().upper(), "DecodedAs": encoding}, text)


def numeric(value) -> float | None:
    try:
        result = float(value)
        return result if math.isfinite(result) else None
    except (ValueError, TypeError):
        return None


def stats(values) -> dict:
    all_values = list(values)
    valid = [v for v in all_values if v is not None and math.isfinite(v)]
    return {"ValidSamples": len(valid), "UnavailableSamples": len(all_values) - len(valid),
        "Mean": statistics.fmean(valid) if valid else None,
        "Peak": max(valid) if valid else None, "Minimum": min(valid) if valid else None}


def parse_time(value: str | None) -> datetime | None:
    return datetime.fromisoformat(value) if value else None


def load_run(base: Path, source: tuple) -> dict:
    dataset, case, label, kind, mode = source
    case_path = base / dataset / case
    checkpoints = list(case_path.glob("data/active-run.json"))
    telemetry = list(case_path.glob("data/LOG/*/load-telemetry.tsv"))
    run = {"Id": dataset + "/" + case, "Label": label, "Kind": kind,
        "Mode": mode if mode >= 0 else None, "CaseDirectory": str(case_path),
        "Sources": [], "Limitations": [], "_samples": [], "_validator": None}
    if len(checkpoints) != 1 or len(telemetry) != 1:
        run["State"] = {"IsTerminal": False, "Outcome": "MissingEvidence"}
        run["Limitations"].append("Expected one checkpoint and one telemetry file; source evidence is incomplete.")
        return run
    checkpoint_info, checkpoint_text = snapshot(checkpoints[0])
    run["Sources"].append(checkpoint_info)
    envelope = json.loads(checkpoint_text)
    payload = envelope.get("Payload")
    state = json.loads(payload) if isinstance(payload, str) else envelope
    if isinstance(payload, str):
        valid = hashlib.sha256(payload.encode("utf-8")).hexdigest().upper() == envelope.get("Sha256", "").upper()
        if not valid:
            raise ValueError("Checkpoint payload SHA256 mismatch: " + str(checkpoints[0]))
    items = state.get("Items", [])
    item = items[0] if items else {}
    definition = item.get("Item", {})
    run["State"] = {key: state.get(key) for key in ("RunId", "Phase", "Outcome", "IsTerminal", "HardwarePassed", "Simulation", "StartedAt", "EndedAt")}
    run["State"]["ItemMessage"] = item.get("Message")
    run["State"]["ItemErrors"] = item.get("Errors", [])
    run["State"]["Execution"] = item.get("Execution")
    run["RequestedParameters"] = definition
    telemetry_info, text = snapshot(telemetry[0])
    run["Sources"].append(telemetry_info)
    samples, rejected = [], 0
    for row in csv.DictReader(io.StringIO(text), delimiter="\t"):
        try:
            if row.get("Status") is None or None in row:
                raise ValueError("Incomplete TSV row")
            point = {"Time": parse_time(row["Date"]), "Status": row["Status"]}
            point.update({metric: numeric(row.get(column)) for column, metric in METRICS.items()})
            if point["Time"] is None:
                raise ValueError("Missing time")
            samples.append(point)
        except (KeyError, ValueError):
            rejected += 1
    samples.sort(key=lambda s: s["Time"])
    run["_samples"] = samples
    gaps = [(b["Time"] - a["Time"]).total_seconds() for a, b in zip(samples, samples[1:])]
    run["Sampling"] = {"Count": len(samples), "RejectedIncompleteRows": rejected,
        "First": samples[0]["Time"].isoformat() if samples else None,
        "Last": samples[-1]["Time"].isoformat() if samples else None,
        "SpanSeconds": (samples[-1]["Time"] - samples[0]["Time"]).total_seconds() if samples else 0,
        "IntervalsSeconds": {"Median": statistics.median(gaps) if gaps else None,
            "Minimum": min(gaps) if gaps else None, "Maximum": max(gaps) if gaps else None},
        "StatusCounts": {status: sum(s["Status"] == status for s in samples) for status in sorted({s["Status"] for s in samples})}}
    run["AllSamplesMetrics"] = {key: stats(s[key] for s in samples) for key in METRICS.values()}
    legacy = telemetry[0].with_name("GW_CoreTemp.log")
    temperature_values = []
    if legacy.exists():
        info, text = snapshot(legacy)
        run["Sources"].append(info)
        temperature_values = [numeric(row.get("Temp")) for row in csv.DictReader(io.StringIO(text), delimiter="\t")]
    run["Temperature"] = {"CoreTemp": stats(temperature_values),
        "CoreTempStatus": "Partial" if any(v is not None and v > 0 for v in temperature_values) and any(v is None for v in temperature_values) else "Available" if any(v is not None and v > 0 for v in temperature_values) else "Unavailable",
        "Attribution": "Shared Core Temp mapping; no session/provider identity is recorded. Values in overlapping runs cannot be independently attributed.",
        "GpuSensorStatus": "Unavailable; sensor values of zero are not valid temperature measurements.",
        "RawBurnInDiskTemperatures": []}
    run["Overlaps"] = []
    if not run["State"]["IsTerminal"]:
        run["Limitations"].append("This is a read-only in-progress snapshot; re-run after termination for final evidence hashes and outcomes.")
    report_files = []
    if kind == "BurnIn":
        reports = list(telemetry[0].parent.glob("1_BurnIn/result.log"))
        if len(reports) == 1:
            info, report = snapshot(reports[0])
            run["Sources"].append(info)
            duration = re.search(r"(?im)^Test Duration:\s*(\d+)h\s*(\d+)m\s*(\d+)s", report)
            run["RawReport"] = {"Path": str(reports[0]),
                "DurationSeconds": sum(int(v) * unit for v, unit in zip(duration.groups(), (3600, 60, 1))) if duration else None,
                "Verdicts": re.findall(r"(?m)^TEST RUN (PASSED|FAILED)\s*$", report),
                "ResultRows": [line.strip() for line in report.splitlines() if re.search(r"\s(?:PASS|FAIL)\s+\d+\s", line)]}
            run["Temperature"]["RawBurnInDiskTemperatures"] = re.findall(r"(?m)^Temperature HDD[^\r\n]+", report)
            run["_validator"] = {"Id": run["Id"], "Kind": "BurnIn", "Report": str(reports[0]),
                "Hours": definition.get("Hours", 0.1), "Mode": mode, "Reports": []}
            window = {}
            for label_part, key in (("Start", "Start"), ("Stop", "Stop")):
                match = re.search(r"(?im)^Test " + label_part + r" time:\s*(.+?)\s*$", report)
                if match:
                    parsed = datetime.strptime(match[1].strip(), "%a %b %d %H:%M:%S %Y")
                    window[key] = parsed.replace(tzinfo=samples[0]["Time"].tzinfo)
            if len(window) == 2:
                selected = [s for s in samples if window["Start"] <= s["Time"] <= window["Stop"]]
                run["ReportWindow"] = {key: value.isoformat() for key, value in window.items()}
                run["ReportWindow"]["Metrics"] = {key: stats(s[key] for s in selected) for key in METRICS.values()}
                run["ReportWindow"]["Samples"] = len(selected)
    else:
        score_rows, monitor_rows = [], []
        for path in sorted(telemetry[0].parent.glob("1_FurMark/*/engine/*")):
            if not path.is_file() or not (path.name.startswith("FurMark_") or path.name == "FurMark-Scores.txt" or path.name.startswith("furmark-gpu-")):
                continue
            info, text = snapshot(path)
            run["Sources"].append(info)
            report_files.append(str(path))
            if path.name == "FurMark-Scores.txt":
                score_rows.extend(line for line in text.splitlines() if line.strip())
            if path.name == "furmark-gpu-monitoring.csv":
                monitor_rows.extend(csv.DictReader(io.StringIO(text)))
        times = [numeric(r.get("seconds")) for r in monitor_rows]
        times = [value for value in times if value is not None]
        monitor_gaps = [right - left for left, right in zip(times, times[1:])]
        score_matches = [re.search(r"\[Score=(\d+)\].*\[DurationInMs=(\d+)\]", line) for line in score_rows]
        score_matches = [match for match in score_matches if match]
        run["RawReport"] = {"ScoreRows": score_rows, "GpuMonitoringRows": len(monitor_rows),
            "Scores": [{"Score": int(match[1]), "DurationInMs": int(match[2])} for match in score_matches],
            "GpuMonitoringFirstSeconds": min(times) if times else None,
            "GpuMonitoringLastSeconds": max(times) if times else None,
            "GpuMonitoringIntervalsSeconds": {"Median": statistics.median(monitor_gaps) if monitor_gaps else None,
                "Maximum": max(monitor_gaps) if monitor_gaps else None,
                "NonIncreasing": sum(gap <= 0 for gap in monitor_gaps)},
            "GpuMonitoringCoreLoad": stats(numeric(r.get("core_load")) for r in monitor_rows),
            "ActualViewports": sorted({(r.get("width"), r.get("height"), r.get("MSAA")) for r in monitor_rows}),
            "GpuZeroTemperatureSamples": sum(numeric(r.get("core_temp")) == 0 for r in monitor_rows)}
        execution = item.get("Execution") or {}
        if execution.get("ExitCode") is not None:
            run["RawReport"]["ProcessExitCodeHex"] = f'0x{execution["ExitCode"] & 0xffffffff:08X}'
        if score_matches and times:
            run["RawReport"]["MonitoringCoverageNote"] = "Polling series ends before the score duration by " + str(round(int(score_matches[-1][2]) / 1000 - max(times), 3)) + " s; samples are discrete and do not establish a successful process exit."
        if run["State"]["IsTerminal"] and execution.get("ExitCode") is not None:
            run["_validator"] = {"Id": run["Id"], "Kind": "FurMark", "Report": None,
                "Hours": definition.get("Hours", 0.1), "Mode": -1,
                "ElapsedSeconds": execution.get("ElapsedSeconds", 0), "ExitCode": execution["ExitCode"], "Reports": report_files}
        else:
            run["Revalidation"] = {"Outcome": "NotRevalidated", "Message": "No completed process exit record; stored outcome is preserved."}
    for pattern in ("1_BurnIn/engine.command.json", "1_BurnIn/engine.txt", "1_FurMark/*/process.command.json", "1_FurMark/*/engine-identity.json"):
        for path in sorted(telemetry[0].parent.glob(pattern)):
            info, text = snapshot(path)
            run["Sources"].append(info)
            if path.name.endswith(".command.json"):
                run["ExecutedCommand"] = json.loads(text)
            if path.name == "engine.txt":
                run["EngineIdentity"] = text.splitlines()
            if path.name == "engine-identity.json":
                run["EngineIdentity"] = json.loads(text)
    return run


PROBE_SOURCE = r'''using Aging.Windows;
using System.Text.Json;
var inputs = JsonSerializer.Deserialize<Input[]>(File.ReadAllText(args[0]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
var output = inputs.Select(input => {
    var result = input.Kind == "BurnIn"
        ? BurnInAdapter.EvaluateReport(File.ReadAllText(input.Report!), input.Hours, input.Mode)
        : FurMarkAdapter.Evaluate(input.ExitCode!.Value, TimeSpan.FromSeconds(input.ElapsedSeconds), input.Hours, input.Reports);
    return new { input.Id, Outcome = result.Outcome.ToString(), result.Message };
}).ToArray();
File.WriteAllText(args[1], JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
record Input(string Id, string Kind, string? Report, double Hours, int Mode, int? ExitCode, double ElapsedSeconds, string[] Reports);
'''


def revalidate(root: Path, output: Path, validator: Path, dotnet: Path, runs: list[dict]) -> dict:
    source = root / "src/Aging.Windows/BurnInAdapter.cs"
    if validator.stat().st_mtime < source.stat().st_mtime:
        raise RuntimeError("Validator DLL predates current BurnInAdapter.cs; finish the production build before summarizing.")
    workspace = output / "load-evidence-analysis"
    private = workspace / "validator"
    private.mkdir(parents=True, exist_ok=True)
    for name in ("Aging.Windows.dll", "Aging.Core.dll"):
        shutil.copyfile(validator.parent / name, private / name)
    project = workspace / "LoadEvidence.csproj"
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>' + ''.join(
        f'<Reference Include="{name[:-4]}"><HintPath>{escape(str(private / name))}</HintPath></Reference>' for name in ("Aging.Windows.dll", "Aging.Core.dll")) + '</ItemGroup></Project>', encoding="utf-8")
    (workspace / "Program.cs").write_text(PROBE_SOURCE, encoding="utf-8")
    manifest = workspace / "input.json"
    result_path = workspace / "revalidated.json"
    manifest.write_text(json.dumps([r["_validator"] for r in runs if r.get("_validator")], indent=2), encoding="utf-8")
    command = [str(dotnet), "run", "--project", str(project), "-c", "EvidenceProbe", "--", str(manifest), str(result_path)]
    completed = subprocess.run(command, cwd=root, text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=180)
    (workspace / "probe-output.txt").write_text(completed.stdout + completed.stderr, encoding="utf-8")
    if completed.returncode != 0:
        raise RuntimeError("Read-only validator probe failed: " + completed.stdout + completed.stderr)
    results = {row["Id"]: row for row in json.loads(result_path.read_text(encoding="utf-8"))}
    for run in runs:
        if run["Id"] in results:
            run["Revalidation"] = results[run["Id"]]
    return {"Assembly": snapshot(validator)[0], "PrivateAssembly": str(private / validator.name),
        "BurnInSource": snapshot(source)[0], "ProbeCommand": command, "ProbeResult": str(result_path),
        "Note": "Calls production validators on retained files; never executes a stress engine or changes an old verdict."}


def overlaps(runs: list[dict]) -> list[dict]:
    results = []
    for index, left in enumerate(runs):
        for right in runs[index + 1:]:
            a, b = left["_samples"], right["_samples"]
            if not a or not b:
                continue
            first, last = max(a[0]["Time"], b[0]["Time"]), min(a[-1]["Time"], b[-1]["Time"])
            if last <= first:
                continue
            overlap = {"Runs": [left["Id"], right["Id"]], "Start": first.isoformat(),
                "End": last.isoformat(), "Seconds": (last - first).total_seconds(),
                "Meaning": "Overlapping system-wide telemetry windows; load cannot be attributed independently."}
            results.append(overlap)
            for run in (left, right):
                run["Overlaps"].append(overlap)
                run["Limitations"].append("System-level load overlaps another acceptance run; inspect Overlaps before attributing CPU/GPU/disk load.")
    return results


def svg_chart(path: Path, runs: list[dict]) -> None:
    width, row_height = 1560, 310
    height = 180 + row_height * len(runs) + 65
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
        '<style>text{font-family:Segoe UI,Arial,sans-serif;fill:#243247;font-size:13px}.title{font-size:26px;font-weight:600}.small{font-size:12px;fill:#59677b}.rowtitle{font-size:18px;font-weight:600}</style>',
        f'<rect width="{width}" height="{height}" fill="#f5f7fb"/>',
        '<text x="34" y="42" class="title">GW aging acceptance: retained system load</text>',
        '<text x="34" y="68">2026-11-12 | Local time +08:00 | Missing measurements remain missing | No hardware PASS inferred from utilization</text>',
        '<text x="34" y="92" class="small">Percentages are system-wide. Means include startup/teardown; report-window statistics are separately retained in JSON.</text>',
        '<text x="34" y="115" class="small">Temperature coverage is incomplete; GPU zero readings are invalid. Disk temperature stays separate. Amber: concurrent runs.</text>']
    colors = {"CpuPercent": "#2563eb", "GpuPercent": "#d97706", "MemoryPercent": "#059669", "DiskActivePercent": "#7c3aed"}
    for index, (key, color) in enumerate(colors.items()):
        x = 35 + index * 178
        parts += [f'<line x1="{x}" y1="143" x2="{x+25}" y2="143" stroke="{color}" stroke-width="3"/>',
            f'<text x="{x+32}" y="148">{key.replace("Percent", " %")}</text>']
    for index, run in enumerate(runs):
        top = 170 + index * row_height
        samples = run["_samples"]
        parts.append(f'<rect x="20" y="{top}" width="1520" height="{row_height-12}" rx="10" fill="white" stroke="#dde4ef"/>')
        verdict = run.get("Revalidation", {}).get("Outcome", run["State"].get("Outcome", "Missing"))
        parts.append(f'<text x="36" y="{top+28}" class="rowtitle">{escape(run["Label"])} | stored: {escape(str(run["State"].get("Outcome")))} | recheck: {escape(str(verdict))}</text>')
        if not samples:
            parts.append(f'<text x="40" y="{top+65}">No retained samples.</text>')
            continue
        start = samples[0]["Time"]
        duration = (samples[-1]["Time"] - start).total_seconds()
        max_x = max(60, math.ceil(duration / 60) * 60)
        info = run["Sampling"]
        parts.append(f'<text x="36" y="{top+49}" class="small">{start:%H:%M:%S}–{samples[-1]["Time"]:%H:%M:%S} | {len(samples)} samples | {duration:.1f} s | Core Temp: {run["Temperature"]["CoreTempStatus"]}; GPU temp: unavailable</text>')
        for panel in range(2):
            x, y, pw, ph = 85 + panel * 750, top + 78, 650, 166
            throughput = [s[key] / 1048576 for s in samples for key in ("DiskReadBytesPerSecond", "DiskWriteBytesPerSecond") if s[key] is not None]
            highest = max(throughput, default=0)
            max_y = 100 if panel == 0 else max(1, math.ceil(highest / (100 if highest > 100 else 1)) * (100 if highest > 100 else 1))
            parts.append(f'<text x="{x}" y="{y-10}" class="small">{"Utilization (%)" if panel == 0 else "Disk throughput (MiB/s): read blue / write orange"}</text>')
            for overlap in run.get("Overlaps", []):
                left = max(0, (parse_time(overlap["Start"]) - start).total_seconds())
                right = min(max_x, (parse_time(overlap["End"]) - start).total_seconds())
                parts.append(f'<rect x="{x+pw*left/max_x:.2f}" y="{y}" width="{pw*(right-left)/max_x:.2f}" height="{ph}" fill="#fff0c2"/>')
            for tick in range(5):
                yy = y + ph - ph * tick / 4
                parts += [f'<line x1="{x}" y1="{yy:.2f}" x2="{x+pw}" y2="{yy:.2f}" stroke="#e5eaf1"/>',
                    f'<text x="{x-9}" y="{yy+4:.2f}" text-anchor="end" class="small">{max_y*tick/4:g}</text>']
            for minute in range(int(max_x / 60) + 1):
                xx = x + pw * minute * 60 / max_x
                parts += [f'<line x1="{xx:.2f}" y1="{y+ph}" x2="{xx:.2f}" y2="{y+ph+5}" stroke="#8a97a8"/>',
                    f'<text x="{xx:.2f}" y="{y+ph+21}" text-anchor="middle" class="small">{minute}</text>']
            parts.append(f'<text x="{x+pw}" y="{y+ph+38}" text-anchor="end" class="small">Elapsed minutes</text>')
            series = list(colors.items()) if panel == 0 else [("DiskReadBytesPerSecond", "#2563eb"), ("DiskWriteBytesPerSecond", "#d97706")]
            for metric, color in series:
                segment, previous = [], None
                for sample in samples:
                    value = sample[metric]
                    if value is None or (previous and (sample["Time"] - previous).total_seconds() > 10):
                        if segment:
                            parts.append(f'<polyline points="{" ".join(segment)}" fill="none" stroke="{color}" stroke-width="1.8"/>')
                        segment = []
                    if value is not None:
                        value = value / 1048576 if panel == 1 else value
                        xx = x + pw * (sample["Time"] - start).total_seconds() / max_x
                        yy = y + ph - ph * max(0, min(value, max_y)) / max_y
                        segment.append(f'{xx:.2f},{yy:.2f}')
                    previous = sample["Time"]
                if segment:
                    parts.append(f'<polyline points="{" ".join(segment)}" fill="none" stroke="{color}" stroke-width="1.8"/>')
    parts.append(f'<text x="34" y="{height-28}" class="small">Source paths, SHA256, mean/peak values, raw report duration, overlap boundaries and validator identity: 20261112-load-summary.json</text>')
    parts.append('</svg>')
    path.write_text("\n".join(parts), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--validator", type=Path)
    parser.add_argument("--dotnet", type=Path)
    parser.add_argument("--require-complete", action="store_true")
    args = parser.parse_args()
    root = args.root.resolve()
    output = root / "artifacts/acceptance"
    validator = args.validator or root / "src/Aging.Windows/bin/Release/net10.0-windows/Aging.Windows.dll"
    dotnet = args.dotnet or Path(shutil.which("dotnet") or str(Path(os.environ.get("LOCALAPPDATA", "")) / "Programs/dotnet/dotnet.exe"))
    runs = [load_run(output, source) for source in SOURCE_DIRS]
    validation = revalidate(root, output, validator, dotnet, runs)
    concurrent = overlaps(runs)
    chart = output / "20261112-load-curves.svg"
    svg_chart(chart, runs)
    document = {"SchemaVersion": 1, "GeneratedAt": datetime.now().astimezone().isoformat(),
        "Scope": "Retained real load runs only; never re-runs a workload or edits original evidence.",
        "Method": {"Counters": "System-wide native CPU/GPU/memory/physical disk counters; other workloads can contribute.",
            "Means": "Arithmetic mean of finite available samples; startup and teardown included in AllSamplesMetrics.",
            "Unavailable": "Excluded from numeric statistics; never replaced with zero.",
            "Peak": "Highest retained sampled value, not a continuous-time maximum.",
            "Gpu": "Per-process contributions summed within each physical engine; maximum physical engine across adapters.",
            "Overlap": "Intersection of telemetry windows, not proof that both engines executed every overlapping instant."},
        "Limitations": ["BurnIn mode 2 and the initial FurMark run overlap for about 1.5 minutes; their system utilization is not independent.",
            "Core Temp CPU temperature is unavailable in BurnIn 0/1 and final FurMark, and only partially available in BurnIn 2/initial FurMark; shared-provider attribution is unbound. FurMark zero GPU-temperature fields are invalid sensor evidence.",
            "BurnIn disk temperature records are retained separately and do not establish CPU/GPU temperature.",
            "Initial FurMark timed out; raw GPU load cannot turn that failed run into a hardware pass.",
            "FurMark provides workload/score evidence without a hardware PASS verdict.",
            "The initial BurnIn run predates final lifecycle/report validation fixes; retained reports are re-evaluated by the identified current validator."],
        "Validator": validation, "Overlaps": concurrent,
        "Chart": str(chart), "ChartFormat": "SVG; standard-library vector plot, no external plotting dependency installed.",
        "PngPreview": str(chart.with_suffix(".png")) if chart.with_suffix(".png").exists() else None,
        "Runs": [{key: value for key, value in run.items() if not key.startswith("_")} for run in runs]}
    summary = output / "20261112-load-summary.json"
    summary.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(str(summary))
    print(str(chart))
    for run in runs:
        print(run["Label"], "stored=" + str(run["State"].get("Outcome")),
            "recheck=" + str(run.get("Revalidation", {}).get("Outcome")),
            "samples=" + str(run.get("Sampling", {}).get("Count", 0)))
    return 2 if args.require_complete and any(not run["State"].get("IsTerminal") for run in runs) else 0


if __name__ == "__main__":
    raise SystemExit(main())
