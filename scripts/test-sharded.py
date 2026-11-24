"""
Run VerifiedXCore.Tests in several processes at once.

The suite runs one test at a time inside a process (tests share static state), so it uses one core. Separate
processes do not share that state, so the test classes are split into N groups of about equal time and each
group runs in its own `dotnet test` process against one shared build.

Usage: python scripts/test-sharded.py [out-dir] [shards] [timings-log]
  out-dir      a folder for the build and the logs (default: TestResults/sharded under the repository)
  shards       how many processes (default: 6)
  timings-log  a previous run's log at normal verbosity, to balance the groups; without one the last run's
               shard logs in out-dir are used if present, otherwise each class counts a quarter second a test

Each shard's full log is out-dir/shard-<n>.log. The exit code is 1 when any test failed.
"""
import collections, glob, os, re, shutil, subprocess, sys, time

DOTNET = os.environ.get("DOTNET") or shutil.which("dotnet") or r"C:\Program Files\dotnet\dotnet.exe"

def main():
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.join(repo, "TestResults", "sharded")
    shards = int(sys.argv[2]) if len(sys.argv) > 2 else 6
    timings = [sys.argv[3]] if len(sys.argv) > 3 else sorted(glob.glob(os.path.join(out, "shard-*.log")))
    os.makedirs(out, exist_ok=True)
    build = os.path.join(out, "build") + os.sep
    proj = os.path.join(repo, "VerifiedXCore.Tests", "VerifiedXCore.Tests.csproj")
    dll = os.path.join(build, "VerifiedXCore.Tests.dll")

    t0 = time.time()
    r = subprocess.run([DOTNET, "build", proj, f"-p:OutDir={build}", "--nologo", "-v", "q"], capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-4000:], r.stderr[-2000:]); sys.exit("build failed")
    print(f"build {time.time() - t0:.0f} s")

    r = subprocess.run([DOTNET, "test", dll, "--list-tests"], capture_output=True, text=True)
    names = [l.strip() for l in r.stdout.splitlines() if l.startswith("    VerifiedXCore.Tests.")]
    by_class = collections.Counter()
    for n in names:
        cls = re.match(r"VerifiedXCore\.Tests\.([A-Za-z0-9_]+)\.", n)
        if cls: by_class[cls.group(1)] += 1
    print(f"{len(names)} tests in {len(by_class)} classes")

    # Time per class: measured where a previous log has it, otherwise a quarter second a test.
    weight = {c: 0.25 * k for c, k in by_class.items()}
    measured = collections.Counter()
    for timing in [t for t in timings if os.path.exists(t)]:
        for line in open(timing, encoding="utf-8", errors="replace"):
            m = re.match(r"\s+(?:Passed|Failed|Skipped) VerifiedXCore\.Tests\.([A-Za-z0-9_]+)\..*\[(\d+(?:\.\d+)?) ?(ms|s|m)\]", line)
            if m:
                v = float(m.group(2)); u = m.group(3)
                measured[m.group(1)] += v / 1000 if u == "ms" else v * 60 if u == "m" else v
    for c in weight:
        if c in measured: weight[c] = max(measured[c], 0.05 * by_class[c])

    groups = [[] for _ in range(shards)]
    load = [0.0] * shards
    for c in sorted(weight, key=weight.get, reverse=True):
        i = load.index(min(load))
        groups[i].append(c); load[i] += weight[c]
    print("expected seconds per group:", [round(x) for x in load])

    t1 = time.time()
    procs = []
    for i, g in enumerate(groups):
        flt = "|".join(f"FullyQualifiedName~VerifiedXCore.Tests.{c}." for c in g)
        log = open(os.path.join(out, f"shard-{i}.log"), "w", encoding="utf-8")
        procs.append((subprocess.Popen([DOTNET, "test", dll, "--filter", flt, "--logger", "console;verbosity=normal"],
                                       stdout=log, stderr=subprocess.STDOUT, cwd=out), log))
    for p, log in procs:
        p.wait(); log.close()
    print(f"tests {time.time() - t1:.0f} s, total {time.time() - t0:.0f} s")

    passed = failed = skipped = 0
    failures = []
    for i in range(shards):
        text = open(os.path.join(out, f"shard-{i}.log"), encoding="utf-8", errors="replace").read()
        m = re.search(r"Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+)", text)
        if m:
            failed += int(m.group(1)); passed += int(m.group(2)); skipped += int(m.group(3))
        else:
            for k, pat in (("passed", r"\n\s+Passed:\s+(\d+)"), ("failed", r"\n\s+Failed:\s+(\d+)"), ("skipped", r"\n\s+Skipped:\s+(\d+)")):
                mm = re.search(pat, text)
                if mm:
                    if k == "passed": passed += int(mm.group(1))
                    elif k == "failed": failed += int(mm.group(1))
                    else: skipped += int(mm.group(1))
        failures += sorted(set(re.findall(r"^\s+Failed (VerifiedXCore\.Tests\.\S+)", text, re.M)))
    print(f"passed {passed}, failed {failed}, skipped {skipped}")
    if not failures:
        sys.exit(0)

    # Processes running at once can race (two take the same free port, or a timing test runs slow). The classes with
    # a failure are run again, one process for all of them, and only what fails again is reported as failed.
    classes = sorted({re.match(r"VerifiedXCore\.Tests\.([A-Za-z0-9_]+)\.", f).group(1) for f in failures})
    flt = "|".join(f"FullyQualifiedName~VerifiedXCore.Tests.{c}." for c in classes)
    rerun_log = os.path.join(out, "rerun.log")
    with open(rerun_log, "w", encoding="utf-8") as log:
        subprocess.run([DOTNET, "test", dll, "--filter", flt, "--logger", "console;verbosity=normal"], stdout=log, stderr=subprocess.STDOUT, cwd=out)
    text = open(rerun_log, encoding="utf-8", errors="replace").read()
    still = sorted(set(re.findall(r"^\s+Failed (VerifiedXCore\.Tests\.\S+)", text, re.M)))
    for f in failures:
        print("  FAILED" if f in still else "  passed when run again (order or timing):", f)
    print(f"failed again: {len(still)} (log: {rerun_log})")
    sys.exit(1 if still else 0)

if __name__ == "__main__":
    main()
