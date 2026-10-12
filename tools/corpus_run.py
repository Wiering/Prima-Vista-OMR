"""Run PVOMR over a corpus of PDFs and keep a manifest of what was processed.

usage: python tools/corpus_run.py --root "D:/OMR/Scores" [--exe <pvomr>] [--max-pages 6] [--timeout 300] [--force] [--report]

Every PDF is identified by the MD5 of its content, so files that exist twice under different names or folders are
processed once; the manifest (<root>/.pvomr-corpus.json) records, per file and page, the measure-fit numbers, the
time taken and any error or timeout, and a later run only adds files and pages that are not in it yet.
`--report` prints the ranking from the manifest without running anything.
"""
import argparse, hashlib, json, os, re, subprocess, sys, time

try:
    from pypdf import PdfReader
except ImportError:  # pragma: no cover
    PdfReader = None

HEAD = re.compile(r"^=== .* - page (\d+): .*?, ([0-9.]+)s(?:\s+staves=(\d+) systems=(\d+) measures=(\d+)|\s+\(no music\))?")
FIT = re.compile(r"^\s*fit: (\d+)/(\d+) measures add up \((\d+)%\), (\d+) too long, (\d+) too short, (\d+) needed dropping(?:, (\d+) without time signature)?")


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def page_count(path):
    if PdfReader is None:
        return None
    try:
        return len(PdfReader(path).pages)
    except Exception:
        return None


def load_manifest(path):
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    return {"files": {}}


def save_manifest(path, m):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(m, f, indent=1, sort_keys=True)
    os.replace(tmp, path)


def run_file(exe, pdf, pages, timeout_per_page):
    """Runs one PDF over the given pages (a continuous range) in one process; returns {page: result}."""
    results = {}
    spec = f"{pages[0]}-{pages[-1]}" if len(pages) > 1 else str(pages[0])
    cmd = [exe, pdf, "--pages", spec, "--quiet", "--fit"]
    start = time.time()
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout_per_page * len(pages))
        out, rc, timed_out = proc.stdout, proc.returncode, False
    except subprocess.TimeoutExpired as ex:
        out = (ex.stdout or b"").decode("utf-8", "replace") if isinstance(ex.stdout, bytes) else (ex.stdout or "")
        rc, timed_out = None, True
    current = None
    for line in out.splitlines():
        m = HEAD.match(line)
        if m:
            current = int(m.group(1))
            results[current] = {"secs": float(m.group(2)), "staves": int(m.group(3) or 0), "systems": int(m.group(4) or 0), "measures": int(m.group(5) or 0)}
            continue
        m = FIT.match(line)
        if m and current is not None:
            r = results[current]
            r.update({"ok": int(m.group(1)), "checked": int(m.group(2)), "fit": int(m.group(3)), "long": int(m.group(4)), "short": int(m.group(5)), "dropped": int(m.group(6)), "unknown": int(m.group(7) or 0)})
    for p in pages:
        if p not in results:
            results[p] = {"error": "timeout" if timed_out else f"exit {rc}" if rc not in (0, None) else "no output"}
    if rc not in (0, None) and not timed_out:
        for p in pages:
            results[p].setdefault("error", f"exit {rc}")
    return results, time.time() - start


def report(m):
    rows = []
    errors = []
    for h, entry in m["files"].items():
        name = os.path.basename(entry["paths"][0])
        for p, r in sorted(entry.get("results", {}).items(), key=lambda kv: int(kv[0])):
            if "error" in r:
                errors.append((name, int(p), r["error"]))
            elif r.get("checked", 0) >= 8:
                rows.append((r["fit"], r["ok"], r["checked"], r.get("secs", 0), name, int(p)))
    rows.sort()
    print(f"{len(m['files'])} files, {sum(len(e.get('results', {})) for e in m['files'].values())} pages, {len(errors)} errors/timeouts, {len(rows)} pages with >= 8 checked measures")
    if errors:
        print("errors:")
        for name, p, e in errors[:40]:
            print(f"  {name} p{p}: {e}")
    if rows:
        fits = [r[0] for r in rows]
        print(f"fit: median {sorted(fits)[len(fits) // 2]}%, mean {sum(fits) / len(fits):.0f}%")
        print("worst pages:")
        for fit, ok, checked, secs, name, p in rows[:30]:
            print(f"  {fit:3d}% {ok:3d}/{checked:<3d} {secs:5.1f}s  {name} p{p}")
        slow = sorted(rows, key=lambda r: -r[3])[:10]
        print("slowest pages:")
        for fit, ok, checked, secs, name, p in slow:
            print(f"  {secs:6.1f}s {name} p{p}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--exe", default=os.path.join(os.path.dirname(__file__), "..", "src", "PVOMR.Cli", "bin", "Release", "net9.0-windows10.0.19041.0", "PVOMR.Cli.exe"))
    ap.add_argument("--manifest", default=None)
    ap.add_argument("--max-pages", type=int, default=6, help="pages per file to process (from the first)")
    ap.add_argument("--timeout", type=int, default=300, help="seconds per page")
    ap.add_argument("--force", action="store_true", help="redo files already in the manifest")
    ap.add_argument("--report", action="store_true", help="only print the ranking from the manifest")
    args = ap.parse_args()
    manifest_path = args.manifest or os.path.join(args.root, ".pvomr-corpus.json")
    m = load_manifest(manifest_path)
    if args.report:
        report(m)
        return
    pdfs = sorted(os.path.join(r, f) for r, _, fs in os.walk(args.root) for f in fs if f.lower().endswith(".pdf"))
    print(f"{len(pdfs)} PDFs under {args.root}")
    done_files = 0
    for i, pdf in enumerate(pdfs, 1):
        h = md5(pdf)
        rel = os.path.relpath(pdf, args.root)
        entry = m["files"].setdefault(h, {"paths": [], "pages": None, "results": {}})
        if rel not in entry["paths"]:
            entry["paths"].append(rel)
        if entry["pages"] is None:
            entry["pages"] = page_count(pdf)
        total = entry["pages"] or 1
        wanted = [p for p in range(1, min(total, args.max_pages) + 1) if args.force or str(p) not in entry["results"]]
        if not wanted:
            continue
        if len(entry["paths"]) > 1 and entry["results"]:
            continue  # a duplicate of a file processed earlier in this run
        results, secs = run_file(args.exe, pdf, wanted, args.timeout)
        for p, r in results.items():
            entry["results"][str(p)] = r
        done_files += 1
        fits = [str(r.get("fit", "-")) + "%" if "error" not in r else r["error"] for _, r in sorted(results.items())]
        print(f"[{i}/{len(pdfs)}] {rel} ({total} pages): {' '.join(fits)} in {secs:.0f}s", flush=True)
        save_manifest(manifest_path, m)
    save_manifest(manifest_path, m)
    print(f"done: {done_files} files processed this run")
    report(m)


if __name__ == "__main__":
    main()
