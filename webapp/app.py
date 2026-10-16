"""
DWG Tools -- Reference Tree + WBLOCK helper (local web app).

Map a nested equipment DWG (Reference Tree), then right-click a block to
light it up in Plant 3D, pick the occurrence and save it as a clean WBLOCK
(exploded to layer 0, master coordinates). The CAD work is done by
DwgFlatPublisher.dll inside the Plant 3D running on this PC (see cad.py).

    run.bat   ->  http://localhost:5014   (this machine only)
"""

import json
import sys
import os
import string
import threading
import time
import uuid
from collections import OrderedDict

from flask import Flask, abort, jsonify, render_template, request, send_file

import applog
import block_sources
import cad
import layer_standard
import native_dialog
import safe_fs

ROOT = cad.ROOT
HISTORY_PATH = os.path.join(ROOT, "jobs_history.json")
TREES_DIR = os.path.join(ROOT, "trees")
app = Flask(__name__)
STARTED = {"at": "", "pid": None}     # which copy of the app this is (shown on the page)
app.config["TEMPLATES_AUTO_RELOAD"] = True   # an updated page shows on the next refresh, no restart needed
app.jinja_env.auto_reload = True


@app.after_request
def _no_stale_pages(resp):
    if resp.mimetype == "text/html":
        resp.headers["Cache-Control"] = "no-store"
    return resp

# ── job queue ───────────────────────────────────────────────────────────────
_jobs = OrderedDict()          # id -> job dict (newest last)
_jobs_lock = threading.Lock()
_queue = []
_queue_cv = threading.Condition()


def _lan_ips():
    """This PC's IPv4 addresses on the local network (192.168.x.x, 10.x.x.x ...)."""
    import socket
    ips = set()
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            ips.add(info[4][0])
    except Exception:
        pass
    try:   # the address used for outbound traffic (no packet is actually sent)
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("10.255.255.255", 1))
        ips.add(s.getsockname()[0])
        s.close()
    except Exception:
        pass
    return sorted(i for i in ips if not i.startswith("127."))


def _load_history():
    try:
        with open(HISTORY_PATH, encoding="utf-8") as fh:
            for j in json.load(fh):
                if j.get("state") in ("queued", "running"):
                    j["state"] = "interrupted"
                _jobs[j["id"]] = j
    except Exception:
        pass


def _save_history():
    try:
        with _jobs_lock:
            items = list(_jobs.values())[-200:]
        safe_fs.write_app_state(HISTORY_PATH, json.dumps(items, indent=1, default=str))
    except Exception:
        pass


def _new_job(kind, params, label):
    j = {"id": uuid.uuid4().hex[:10], "kind": kind, "params": params, "label": label,
         "state": "queued", "created": time.strftime("%Y-%m-%d %H:%M:%S"),
         "steps": [], "head": "", "log": [], "report": None, "reportPath": "",
         "children": [], "elapsed": None}
    with _jobs_lock:
        _jobs[j["id"]] = j
    with _queue_cv:
        _queue.append(j["id"])
        _queue_cv.notify()
    _save_history()
    return j


def _worker():
    while True:
        with _queue_cv:
            while not _queue:
                _queue_cv.wait()
            jid = _queue.pop(0)
        j = _jobs.get(jid)
        if not j:
            continue
        j["state"] = "running"
        t0 = time.time()
        p = j.get("params") or {}
        applog.log("JOB." + j["kind"], "START", id=j["id"], source=p.get("source"), engine=p.get("engine"),
                   plugin=os.path.basename(cad.plugin_dll()))
        try:
            _run_one(j)
        except Exception as exc:
            j["state"] = "failed"
            j["steps"].append(["Unexpected error: %s" % exc, False])
        j["elapsed"] = round(time.time() - t0, 1)
        applog.log("JOB." + j["kind"], "OK" if j["state"] == "done" else "FAILED", id=j["id"], seconds=j["elapsed"],
                   head=j.get("head"), detail=applog.steps_lines(j.get("steps")) + ["plugin  " + l for l in (j.get("log") or [])[-60:]])
        _save_history()


def _read_json(path):
    try:
        with open(path, encoding="utf-8-sig") as fh:
            return json.load(fh)
    except Exception:
        return None


def _dispatch(job, engine, label):
    job = {k: v for k, v in job.items() if k != "engine"}
    if engine == "console":
        return cad.run_job_console(job, label=label)
    if job.get("live"):
        return cad.run_job(job, label=label, drawing=job.get("source", ""), launch=False)
    return cad.run_job(job, label=label)


def _run_one(j):
    p = j["params"]
    job = dict(p)
    job["action"] = j["kind"]
    if j["kind"] == "analyze":
        job["reportPath"] = os.path.join(cad.JOB_DIR, "analysis_%s.json" % j["id"])
    if j["kind"] == "tree":
        os.makedirs(TREES_DIR, exist_ok=True)
        stem = os.path.splitext(os.path.basename(p.get("source", "drawing")))[0]
        job["reportPath"] = os.path.join(TREES_DIR, "%s_%s_%s.json" % (stem, time.strftime("%Y%m%d-%H%M%S"), j["id"]))
    res = _dispatch(job, p.get("engine"), "an %s of %s" % (j["kind"], os.path.basename(p.get("source", ""))))
    j["steps"] += [[m, ok] for m, ok in res["steps"]]
    j["head"] = res.get("head", "")
    j["log"] = res.get("lines", [])
    parts = j["head"].split("|")
    if j["kind"] in ("analyze", "tree"):
        rp = parts[3] if len(parts) > 3 else job.get("reportPath", "")
    else:
        rp = parts[-1] if len(parts) >= 3 else ""
        if not rp or not rp.lower().endswith(".json"):
            out = p.get("output") or ""
            rp = out + ".flatreport.json" if out else ""
    j["reportPath"] = rp
    if j["kind"] == "tree":
        # the tree can be large: keep only a summary in the job history; the page loads the file itself
        t = _read_json(rp) if rp else None
        j["report"] = _tree_summary(t) if t else None
        j["state"] = "done" if (res["ok"] and t) else "failed"
        return
    j["report"] = _read_json(rp) if rp else None
    if j["kind"] == "extract":
        j["state"] = "done" if (j["report"] or {}).get("status") == "SUCCESS" else "failed"
        return
    if j["kind"] == "publish" and j["report"]:
        j["state"] = "done" if j["report"].get("status", "").startswith("SUCCESS") else "failed"
    else:
        j["state"] = "done" if res["ok"] else "failed"


def _tree_summary(t):
    files = t.get("files") or []
    outside = [f for f in files if f.get("outsideProject")]
    missing = sum(1 for f in files for x in (f.get("xrefs") or []) if x.get("status") == "not found at saved path")
    return {"files": len(files), "outsideProject": len(outside), "missingXrefs": missing,
            "originalsUnchanged": t.get("originalsUnchanged"), "originalsChanged": t.get("originalsChanged"),
            "projectRoot": t.get("projectRoot"), "source": t.get("source")}


def project_root_for(path: str) -> str:
    """'P:\\867 - Name\\...\\x.dwg' -> 'P:\\867 - Name' (UNC: \\\\server\\share\\folder)."""
    try:
        full = os.path.abspath(path)
        drive, rest = os.path.splitdrive(full)
        parts = [x for x in rest.replace("/", "\\").split("\\") if x]
        return drive + "\\" + parts[0] if len(parts) > 1 else os.path.dirname(full)
    except Exception:
        return ""


# ── routes ──────────────────────────────────────────────────────────────────
@app.route("/")
def home():
    return render_template("tree.html")


@app.route("/history")
def history_page():
    return render_template("history.html")


@app.route("/tree")
def tree_page():
    return render_template("tree.html")


@app.route("/api/tree", methods=["POST"])
def api_tree():
    d = request.get_json(force=True) or {}
    src = str(d.get("source", "")).strip().strip('"')
    if not os.path.isfile(src) or not src.lower().endswith(".dwg"):
        return jsonify({"error": "Drawing not found: " + src}), 400
    root = str(d.get("projectRoot") or "").strip().strip('"') or project_root_for(src)
    engine = "session" if str(d.get("engine", "console")).lower() == "session" else "console"
    params = {"source": src, "projectRoot": root, "maxFiles": int(d.get("maxFiles") or 500), "engine": engine}
    if d.get("live"):
        # read the drawing as it is open in Plant 3D right now (unsaved inserts included)
        params["engine"] = "session"
        params["live"] = "1"
    j = _new_job("tree", params, "Reference tree: " + os.path.basename(src))
    return jsonify({"id": j["id"], "projectRoot": root})


@app.route("/api/project-root")
def api_project_root():
    return jsonify({"projectRoot": project_root_for(request.args.get("path", ""))})


@app.route("/api/tree-data/<jid>")
def api_tree_data(jid):
    j = _jobs.get(jid)
    if not j or not j.get("reportPath") or not os.path.isfile(j["reportPath"]):
        abort(404)
    return send_file(j["reportPath"], mimetype="application/json")


@app.route("/api/trees")
def api_trees():
    """Earlier trees, newest first (kept in the trees folder)."""
    with _jobs_lock:
        items = [_summary(j) | {"summary": j.get("report")} for j in reversed(list(_jobs.values()))
                 if j.get("kind") == "tree" and j.get("state") == "done"]
    return jsonify(items[:50])


# ── block sources: match block names to .dwg files (folder listing only) ────
def _bs_input():
    d = request.get_json(force=True) or {}
    blocks = [{"name": str(b.get("name", "")), "uses": int(b.get("uses") or 0)} for b in (d.get("blocks") or []) if b.get("name")]
    return d, blocks


@app.route("/api/block-sources/suggest", methods=["POST"])
def api_bs_suggest():
    d, blocks = _bs_input()
    root = str(d.get("projectRoot") or "") or project_root_for(str(d.get("master") or ""))
    return jsonify({"folders": block_sources.suggest_folders(str(d.get("master") or ""), root, [b["name"] for b in blocks]),
                    "projectRoot": root})


@app.route("/api/block-sources/scan", methods=["POST"])
def api_bs_scan():
    d, blocks = _bs_input()
    folders = []
    for f in d.get("folders") or []:
        f = str(f).strip().strip('"')
        if f and os.path.isdir(f) and f not in folders:
            folders.append(f)
    if not folders:
        return jsonify({"error": "None of the search folders exist (or the drive is not mapped)."}), 400
    root = str(d.get("projectRoot") or "") or project_root_for(str(d.get("master") or ""))
    st = block_sources.start_scan(folders, blocks, str(d.get("master") or ""), root, rescan=bool(d.get("rescan")))
    return jsonify({"id": st["id"]})


@app.route("/api/block-sources/<sid>")
def api_bs_state(sid):
    st = block_sources.scan_state(sid)
    if st is None:
        abort(404)
    return jsonify(st)


@app.route("/api/block-sources/<sid>/cancel", methods=["POST"])
def api_bs_cancel(sid):
    return jsonify({"ok": block_sources.cancel(sid)})


def api_extract_defaults():
    d = request.get_json(force=True) or {}
    master = str(d.get("master", ""))
    stem = os.path.splitext(os.path.basename(master))[0]
    return jsonify({"outFolder": _block_folder_for(master), "workingCopies": _working_copies(master),
                    "newWorkingCopy": os.path.join(os.path.dirname(master), stem + " - WORKING.dwg"),
                    "masterExists": os.path.isfile(master)})


def api_extract():
    d = request.get_json(force=True) or {}
    master = str(d.get("master", "")).strip().strip('"')
    if not os.path.isfile(master) or not master.lower().endswith(".dwg"):
        return jsonify({"error": "Equipment model not found: " + master}), 400
    wc = str(d.get("workingCopy", "")).strip().strip('"')
    if wc and os.path.normcase(os.path.abspath(wc)) == os.path.normcase(os.path.abspath(master)):
        return jsonify({"error": "The working copy can't be the equipment model itself."}), 400
    block = str(d.get("block", "")).strip()
    out_folder = str(d.get("outFolder", "")).strip().strip('"')
    if not block or not out_folder:
        return jsonify({"error": "Block and folder are required."}), 400
    params = {"source": master, "master": master, "workingCopy": wc, "block": block,
              "handles": ",".join(str(h) for h in (d.get("handles") or [])),
              "outFolder": out_folder, "fileStem": str(d.get("fileStem", "")).strip(),
              "keepLayers": bool(d.get("keepLayers")), "asXref": d.get("asXref", True) is not False,
              "targetLayer": str(d.get("targetLayer", "")).strip(),
              "engine": "console" if str(d.get("engine", "session")).lower() == "console" else "session"}
    j = _new_job("extract", params, "Extract %s" % block)
    return jsonify({"id": j["id"]})


# ── WBLOCK helper: light up / pick / wblock in the drawing open in Plant 3D ──
_wb_tasks = {}


def _wb_run(tid, action, drawing, extra, front):
    t = _wb_tasks[tid]
    job = {"action": action, "drawing": drawing}
    job.update(extra)
    label = {"find": "lighting up a block", "pick": "picking a block in CAD", "wblock": "making a WBLOCK", "clear": "clearing the markers",
             "selected": "reading the selection in CAD", "insert": "inserting WBLOCKs",
             "insertcheck": "checking the drawing before inserting"}[action]
    t0 = time.time()
    applog.log("WBLOCK." + action, "START", task=tid, drawing=drawing,
               rows=" | ".join(" > ".join(r.split("\u0001")) for r in str(extra.get("rows", "")).split("\u0002")) or None,
               allow=extra.get("allow") or None, placements=extra.get("placements"),
               handles=(str(extra.get("chains"))[:600] if extra.get("chains") else None), focus=extra.get("focus") or None, save=extra.get("savePath"),
               plugin=os.path.basename(cad.plugin_dll()))
    res = cad.run_job(job, wait=1800.0, label=label, drawing=drawing, front=front)
    t["steps"] = [[m, ok] for m, ok in res["steps"]]
    parts = (res.get("head") or "").split("|")
    data = _read_json(parts[-1]) if parts and parts[-1].lower().endswith(".json") else None
    if data is not None:
        # the plugin answered: make sure it is the current build
        if _plugin_is_stale(data.get("plugin")) and data.get("status") != "OK":
            data = {"status": "FAILED", "message": cad.STALE_MSG}
    elif "Source drawing not found" in (res.get("head") or ""):
        # a plugin from before the WBLOCK helper answered (it treats any request as a flatten job)
        data = {"status": "FAILED", "message": cad.STALE_MSG}
    else:
        data = {"status": "FAILED", "message": (parts[1] if len(parts) > 1 else "") or "; ".join(m for m, ok in res["steps"] if not ok) or "AutoCAD did not answer."}
    t["result"] = data
    t["state"] = "done"
    det = applog.steps_lines(res["steps"])
    if data.get("message"):
        det.insert(0, "message  " + str(data.get("message")))
    if data.get("plugin"):
        det.append("answered by  " + str(data.get("plugin")))
    for k in ("file", "block", "count", "entities", "leftOut", "placement", "rejectedFile", "picked", "layers", "layerSort", "layerSortOther", "layerSortNotes", "types", "linetypes"):
        if data.get(k) not in (None, ""):
            det.append("%s  %s" % (k, data.get(k)))
    c = data.get("coordinates") or {}
    if c:
        det.append("coordinates  %s  master=%s  wblock=%s  difference=%s  base=%s" % (c.get("status"), (c.get("master") or {}).get("mainBox"),
                   (c.get("wblock") or {}).get("mainBox"), c.get("difference"), (c.get("wblock") or {}).get("basePoint")))
        det.append("points  %s of %s at the same place  missed=%s  units master=%s wblock=%s" % (c.get("pointsFound"), c.get("pointsChecked"),
                   c.get("pointsMissed"), (c.get("master") or {}).get("units"), (c.get("wblock") or {}).get("units")))
    for x in data.get("inserted") or []:
        det.append("inserted  %s as block '%s' on layer '%s' at %s,%s%s  handle=%s" % (x.get("file"), x.get("block"), x.get("layer"), x.get("x"), x.get("y"),
                                                                                    "  (definition updated)" if x.get("redefined") else "", x.get("handle")))
    if data.get("erased") is not None:
        det.append("erased originals  %s" % data.get("erased"))
    if data.get("purged") is not None:
        det.append("purged definitions  %s" % data.get("purged"))
    if data.get("currentLayer"):
        det.append("current layer now  %s" % data.get("currentLayer"))
    for x in (data.get("items") or []) if action == "insertcheck" else []:
        det.append("check  block '%s' exists=%s copies=%s others=%s sameAsOriginal=%s  layer '%s' exists=%s" % (
            x.get("block"), x.get("blockExists"), x.get("copies"), x.get("otherCopies"), x.get("sameAsOriginal"), x.get("layer"), x.get("layerExists")))
    if data.get("selected") is not None:
        det.append("selected (%s)  %s" % (data.get("how"), " | ".join(" > ".join("%s[%s]" % (x.get("name"), x.get("handle")) for x in ch)
                                                                         for ch in (data.get("selected") or [])[:20])))
    for w in data.get("warnings") or []:
        det.append("WARNING  " + str(w))
    for o in data.get("outside") or []:
        det.append("outside  " + str(o))
    if data.get("occurrences") is not None:
        det.append("occurrences  %d" % len(data.get("occurrences") or []))
        for o in (data.get("occurrences") or [])[:5]:
            det.append("  #%s at %s,%s  visible=%s hidden=%s attdefs=%s partial=%s  box=%s  autocad_box=%s  hidden_layers=%s" % (
                o.get("index"), o.get("x"), o.get("y"), o.get("visibleObjects"), o.get("hiddenObjects"), o.get("attributeDefinitions"),
                o.get("partial"), o.get("box"), o.get("fullBox"), o.get("hiddenLayers")) + "  strays=%s %s" % (o.get("strays"), o.get("strayList")))
    det += ["plugin  " + l for l in ((data.get("log") or res.get("lines") or []))[-80:]]
    applog.log("WBLOCK." + action, "OK" if data.get("status") == "OK" else "FAILED", task=tid,
               seconds=round(time.time() - t0, 1), detail=det)
    if action == "wblock" and data.get("status") == "OK":
        # keep a record so the sidebar can list every WBLOCK made (persists in jobs_history.json)
        j = {"id": tid, "kind": "wblock", "label": "WBLOCK %s" % data.get("block", ""), "state": "done",
             "created": time.strftime("%Y-%m-%d %H:%M:%S"), "elapsed": None, "steps": [], "log": [], "children": [],
             "params": {"drawing": drawing}, "reportPath": "",
             "report": {k: data.get(k) for k in ("file", "block", "inside", "entities", "leftOut", "insertionPoint", "sizeBytes")}}
        with _jobs_lock:
            _jobs[tid] = j
        _save_history()


def _plugin_is_stale(loaded_path):
    """The plugin answers with the file it was loaded from. Every build has its
    own file and command (plugin/build.bat), so an answer must come from the
    newest build; one from anything else means an old plugin answered."""
    if not loaded_path:
        return True          # a plugin from before builds reported themselves
    return os.path.normcase(os.path.abspath(str(loaded_path))) != os.path.normcase(os.path.abspath(cad.plugin_dll()))


@app.route("/api/wb/<action>", methods=["POST"])
def api_wb(action):
    if action not in ("find", "pick", "wblock", "clear", "selected", "insert", "insertcheck"):
        abort(404)
    d = request.get_json(force=True) or {}
    drawing = str(d.get("drawing", "")).strip().strip('"')
    if not os.path.isfile(drawing):
        return jsonify({"error": "Drawing not found: " + drawing}), 400
    extra = {}
    if action in ("find", "pick"):
        # one or more rows (name paths, all under the same parent); per row the
        # placements ticked at each level (empty = every one)
        rows = d.get("rows") or ([d.get("path")] if d.get("path") else [])
        rows = [[str(n) for n in (r or []) if str(n)] for r in rows]
        rows = [r for r in rows if r]
        if not rows:
            return jsonify({"error": "No block chosen."}), 400
        allow = d.get("allow") or []
        extra["rows"] = "\u0002".join("\u0001".join(r) for r in rows)
        extra["path"] = "\u0001".join(rows[0])
        extra["allow"] = "\u0002".join(";".join(",".join(str(h) for h in (lvl or [])) for lvl in (allow[i] if i < len(allow) else []))
                                        for i in range(len(rows)))
        extra["focus"] = int(d.get("focus") or 0)
    if action == "wblock":
        save = str(d.get("savePath", "")).strip().strip('"')
        if not save.lower().endswith(".dwg"):
            save += ".dwg"
        if os.path.exists(save):
            return jsonify({"error": "%s already exists. Pick another name: existing files are never replaced." % save}), 400
        if os.path.normcase(os.path.abspath(save)) == os.path.normcase(os.path.abspath(drawing)):
            return jsonify({"error": "That is the drawing itself."}), 400
        extra["savePath"] = save
        chains = d.get("chains") or ([d.get("handles")] if d.get("handles") else [])
        chains = [[str(h) for h in c if str(h)] for c in chains if c]
        if not chains:
            return jsonify({"error": "Nothing chosen to put in the WBLOCK."}), 400
        extra["chains"] = ";".join(",".join(c) for c in chains)
        extra["handles"] = ",".join(chains[0])
        extra["placements"] = len(chains)
        # the standard layers to sort onto (all of them unless the page sent a choice)
        std = layer_standard.load(ROOT)
        keep = d.get("layers")
        layers = [l for l in std["layers"] if l["aci"] and (keep is None or l["name"] in keep)]
        if layers:
            extra["layers"] = layer_standard.to_job(layers)
    if action in ("insert", "insertcheck"):
        # WBLOCK files into the drawing open in CAD (nothing is saved by this tool)
        rows = []
        for it in d.get("items") or []:
            f = str(it.get("file", "")).strip().strip('"')
            if not os.path.isfile(f):
                return jsonify({"error": "WBLOCK file not found: " + f}), 400
            if os.path.normcase(os.path.abspath(f)) == os.path.normcase(os.path.abspath(drawing)):
                return jsonify({"error": "That is the drawing itself."}), 400
            rows.append("\u0001".join([f, str(it.get("block") or ""), str(it.get("layer") or ""),
                                        repr(float(it.get("x") or 0)), repr(float(it.get("y") or 0)),
                                        str(int(it.get("color") or 72))]))
        if not rows:
            return jsonify({"error": "Nothing to insert."}), 400
        extra["items"] = "\u0002".join(rows)
        extra["replace"] = ";".join(",".join(str(h) for h in c) for c in (d.get("replace") or []) if c)
        extra["purge"] = "1" if d.get("purge") else ""
        extra["redefine"] = "1" if d.get("redefine") else ""
        extra["current"] = "1" if d.get("current") else ""
    tid = uuid.uuid4().hex[:10]
    _wb_tasks[tid] = {"id": tid, "action": action, "state": "running", "result": None, "steps": []}
    threading.Thread(target=_wb_run, args=(tid, action, drawing, extra, action in ("find", "pick", "selected", "insert")), daemon=True).start()
    return jsonify({"id": tid})


@app.route("/api/wb/task/<tid>")
def api_wb_task(tid):
    t = _wb_tasks.get(tid)
    if not t:
        abort(404)
    d = dict(t)
    if t["state"] == "running":
        d["progress"] = cad.read_progress()
    return jsonify(d)


@app.route("/api/cad/status", methods=["POST"])
def api_cad_status():
    """Ask Plant 3D which DWG Tools plugin it is running. Changes nothing, never starts Plant 3D."""
    dll = cad.plugin_dll()
    newest = time.strftime("%Y-%m-%d %H:%M", time.localtime(os.path.getmtime(dll))) if os.path.isfile(dll) else ""
    out = {"build": cad.build_status(), "newestBuild": newest, "running": False, "plugin": "", "pluginBuilt": "", "upToDate": None,
           "documents": [], "steps": [], "verdict": "", "action": ""}
    res = cad.run_job({"action": "ping"}, wait=60.0, label="checking Plant 3D", launch=False)
    out["steps"] = [[m, ok] for m, ok in res["steps"]]
    head = res.get("head") or ""
    parts = head.split("|")
    data = _read_json(parts[-1]) if parts and parts[-1].lower().endswith(".json") else None
    attached = any(m.startswith("Attached to AutoCAD") for m, ok in res["steps"])
    out["running"] = attached
    if not attached:
        out["verdict"] = "Plant 3D isn't running (or this app can't reach it)."
        out["action"] = "Start Plant 3D, then check again. The page starts it for you when you light up a block."
    elif data and data.get("status") == "OK":
        out["plugin"] = data.get("plugin", ""); out["pluginBuilt"] = data.get("pluginBuilt", "")
        out["documents"] = data.get("documents") or []
        out["upToDate"] = not _plugin_is_stale(out["plugin"])
        if out["upToDate"]:
            out["verdict"] = "Plant 3D is running the current DWG Tools plugin."
        else:
            out["verdict"] = "Plant 3D is running an older DWG Tools plugin (built %s; the newest is %s)." % (out["pluginBuilt"], newest)
            out["action"] = "Save your drawings, close Plant 3D completely and open it again."
    elif "Source drawing not found" in head or any("older DWG Tools plugin" in m for m, ok in res["steps"]):
        out["upToDate"] = False
        out["verdict"] = "Plant 3D is running an older DWG Tools plugin from before the WBLOCK helper. It doesn't understand \"light up\", so it runs an old flatten job instead (that's the \"Checking request\" / \"Source drawing not found\" you saw)."
        out["action"] = "Save your drawings, close Plant 3D completely and open it again. The newest plugin (built %s) loads automatically the next time you use the page." % newest
    else:
        out["verdict"] = "Plant 3D didn't answer: " + ((parts[1] if len(parts) > 1 else "") or "; ".join(m for m, ok in res["steps"] if not ok) or "no reply")
        out["action"] = "If AutoCAD is in the middle of a command or has a dialog open, finish it (Esc), then check again."
    applog.log("CAD.status", "OK" if out.get("upToDate") else "FAILED", verdict=out.get("verdict"),
               detail=[out.get("action") or ""] + applog.steps_lines(out.get("steps")))
    return jsonify(out)


@app.route("/api/log", methods=["POST"])
def api_log():
    """Things the page sees (a failed step, a script error), into the same log."""
    d = request.get_json(silent=True) or {}
    applog.log("PAGE." + str(d.get("event", "note"))[:30], str(d.get("status", "INFO"))[:8],
               detail=[str(d.get("detail", ""))[:2000]], phase=str(d.get("phase", ""))[:20] or None)
    return jsonify({"ok": True})


@app.route("/logs")
@app.route("/logs/<name>")
def logs_page(name=None):
    """Read-only view of the log files (newest day first)."""
    files = applog.files()
    name = name if name in files else (files[0] if files else None)
    text = ""
    if name:
        try:
            with open(os.path.join(safe_fs.LOG_DIR, name), encoding="utf-8", errors="replace") as fh:
                text = fh.read()
        except OSError:
            text = ""
    if request.args.get("raw"):
        return text, 200, {"Content-Type": "text/plain; charset=utf-8"}
    import html as _h
    links = " ".join('<a href="/logs/%s"%s>%s</a>' % (f, ' style="font-weight:700"' if f == name else "", f[9:19]) for f in files[:30])
    body = _h.escape(text[-400000:]) if text else "Nothing logged yet."
    return ("<!doctype html><meta charset=utf-8><title>DWG Tools log</title>"
            "<body style='margin:0;font:13px ui-sans-serif,Segoe UI,sans-serif;background:#fff;color:#0d0d0d'>"
            "<div style='position:sticky;top:0;background:#f9f9f9;padding:10px 18px;border-bottom:1px solid #e5e5e5'>"
            "<b>DWG Tools log</b> &nbsp; <a href='/'>&larr; back</a> &nbsp; " + links +
            (" &nbsp; <a href='/logs/%s?raw=1'>plain text</a>" % name if name else "") +
            "<div style='color:#8f8f8f;font-size:12px;margin-top:4px'>%s</div></div>" % _h.escape(os.path.join(safe_fs.LOG_DIR, name or "")) +
            "<pre id=l style='margin:0;padding:14px 18px;font:12px ui-monospace,Consolas,monospace;white-space:pre-wrap'>" + body + "</pre>"
            "<script>scrollTo(0,document.body.scrollHeight)</script></body>")


@app.route("/api/build-status")
def api_build_status():
    b = cad.build_status()
    info = cad.plugin_info()
    b.update(appStarted=STARTED["at"], appPid=STARTED["pid"], plugin=os.path.basename(info["dll"]), command=info.get("command"))
    return jsonify(b)


@app.route("/api/wblocks")
def api_wblocks():
    with _jobs_lock:
        items = [{"id": j["id"], "created": j.get("created"), "drawing": (j.get("params") or {}).get("drawing", ""), **(j.get("report") or {})}
                 for j in reversed(list(_jobs.values())) if j.get("kind") == "wblock"]
    return jsonify(items[:200])


@app.route("/api/exists")
def api_exists():
    p = request.args.get("path", "")
    return jsonify({"exists": os.path.exists(p), "folderExists": os.path.isdir(os.path.dirname(p))})


@app.route("/api/env")
def api_env():
    dll = cad.plugin_dll()
    return jsonify({
        "dll": dll, "dllBuilt": os.path.isfile(dll),
        "dllTime": time.strftime("%Y-%m-%d %H:%M", time.localtime(os.path.getmtime(dll))) if os.path.isfile(dll) else "",
        "acadExe": cad.acad_exe(), "consoleExe": cad.console_exe(), "busy": cad.LOCK.op, "defaultTestFolder": os.path.join(ROOT, "testdata"),
    })


@app.route("/api/native-dialog")
def api_native_dialog():
    """Show Windows' own Open / Save As dialog. Only returns the chosen path."""
    kind = request.args.get("kind") if request.args.get("kind") in ("save", "multi") else "open"
    folder = request.args.get("dir", "").strip().strip('"')
    if folder and os.path.isfile(folder):
        folder = os.path.dirname(folder)
    while folder and not os.path.isdir(folder):
        up = os.path.dirname(folder.rstrip("\\/"))
        folder = "" if up == folder else up
    title = {"save": "Save the WBLOCK as", "multi": "Choose the WBLOCK files to import"}.get(kind, "Open the copy of the equipment drawing")
    try:
        path = native_dialog.ask(kind, title, folder, request.args.get("name", ""))
    except Exception as exc:
        applog.log("DIALOG", "FAILED", str(exc))
        return jsonify({"error": str(exc)}), 500
    if path and kind == "save" and not path.lower().endswith(".dwg"):
        path += ".dwg"
    paths = [x.strip() for x in path.splitlines() if x.strip()] if kind == "multi" else ([path] if path else [])
    return jsonify({"path": paths[0] if paths else "", "paths": paths, "cancelled": not paths})


@app.route("/api/layer-standard")
def api_layer_standard():
    """The block-level layer standard (read from the Excel file next to run.bat)."""
    return jsonify(layer_standard.load(ROOT))


@app.route("/api/browse")
def api_browse():
    path = request.args.get("path", "").strip().strip('"')
    if not path:
        drives = ["%s:\\" % d for d in string.ascii_uppercase if os.path.exists("%s:\\" % d)]
        return jsonify({"path": "", "parent": "", "dirs": drives, "files": []})
    if os.path.isfile(path):
        path = os.path.dirname(path)
    if not os.path.isdir(path):
        return jsonify({"error": "Not a folder: " + path}), 400
    dirs, files = [], []
    try:
        for name in sorted(os.listdir(path), key=str.lower):
            full = os.path.join(path, name)
            if os.path.isdir(full):
                dirs.append(name)
            elif name.lower().endswith(".dwg"):
                try:
                    st = os.stat(full)
                    files.append({"name": name, "size": st.st_size,
                                  "modified": time.strftime("%Y-%m-%d %H:%M", time.localtime(st.st_mtime))})
                except OSError:
                    pass
    except PermissionError:
        return jsonify({"error": "No permission to read " + path}), 400
    parent = os.path.dirname(path.rstrip("\\/"))
    if parent == path.rstrip("\\/"):
        parent = ""
    return jsonify({"path": path, "parent": parent, "dirs": dirs, "files": files})


def _summary(j):
    return {k: j.get(k) for k in ("id", "kind", "label", "state", "created", "elapsed")}


@app.route("/api/jobs")
def api_jobs():
    with _jobs_lock:
        items = [_summary(j) for j in reversed(list(_jobs.values()))][:100]
    return jsonify(items)


@app.route("/api/jobs/<jid>")
def api_job(jid):
    j = _jobs.get(jid)
    if not j:
        abort(404)
    d = dict(j)
    if j["state"] == "running":
        d["progress"] = cad.read_progress()
    d["queuePosition"] = (_queue.index(jid) + 1) if jid in _queue else 0
    return jsonify(d)


@app.route("/api/jobs/<jid>/cancel", methods=["POST"])
def api_cancel(jid):
    j = _jobs.get(jid)
    if not j:
        abort(404)
    if j["state"] == "queued":
        with _queue_cv:
            if jid in _queue:
                _queue.remove(jid)
        j["state"] = "cancelled"
        _save_history()
        return jsonify({"ok": True, "msg": "Removed from the queue."})
    if j["state"] == "running" and cad.cancel_console():
        return jsonify({"ok": True, "msg": "Background job stopped; nothing was published."})
    return jsonify({"ok": False, "msg": "This job runs in your AutoCAD session: press Esc in AutoCAD to cancel it (nothing is published)."})


@app.route("/api/report")
def api_report():
    path = request.args.get("path", "")
    if not path.lower().endswith(".json") or not os.path.isfile(path):
        abort(404)
    return send_file(path, mimetype="application/json")


@app.route("/api/open-folder", methods=["POST"])
def api_open_folder():
    path = str((request.get_json(force=True) or {}).get("path", ""))
    folder = path if os.path.isdir(path) else os.path.dirname(path)
    if not os.path.isdir(folder):
        return jsonify({"ok": False, "msg": "Folder not found"}), 400
    try:
        if os.path.isfile(path):
            import subprocess
            subprocess.Popen(["explorer", "/select,", path])
        else:
            os.startfile(folder)
        return jsonify({"ok": True})
    except Exception as exc:
        return jsonify({"ok": False, "msg": str(exc)}), 500


@app.route("/api/open-in-cad", methods=["POST"])
def api_open_in_cad():
    path = str((request.get_json(force=True) or {}).get("path", ""))
    return jsonify(cad.open_read_only(path))


if __name__ == "__main__":
    _load_history()
    STARTED["at"] = time.strftime("%Y-%m-%d %H:%M:%S")
    STARTED["pid"] = os.getpid()
    _b = cad.build_status()
    applog.log("APP", "START", python=sys.version.split()[0], plugin=os.path.basename(cad.plugin_dll()),
               command=cad.plugin_info().get("command"), build="FAILED" if _b["failed"] else "ok",
               unbuilt_changes=_b["unbuiltChanges"] or None, detail=_b["errors"])
    threading.Thread(target=_worker, daemon=True).start()
    port = int(cad.settings().get("port", 5014))
    # Only ONE copy may run: Windows lets two programs listen on the same port,
    # and the browser then talks to whichever answers -- possibly an old copy.
    import socket as _sk
    _probe = _sk.socket()
    _probe.settimeout(1.0)
    _busy = _probe.connect_ex(("127.0.0.1", port)) == 0
    _probe.close()
    if _busy:
        applog.log("APP", "FAILED", reason="another copy is already running on port %d" % port)
        print("")
        print("  *** DWG Tools is ALREADY RUNNING in another window (port %d). ***" % port)
        print("  Close the other DWG Tools / run.bat window(s) first -- an old copy keeps")
        print("  answering the browser with old code. Then run run.bat again.")
        print("")
        sys.exit(1)
    shared = ("--share" in sys.argv[1:] or os.environ.get("DWGTOOLS_SHARE") == "1"
              or cad.settings().get("share") is True)
    host = "0.0.0.0" if shared else "127.0.0.1"
    print("")
    print("  This PC:          http://127.0.0.1:%d" % port)
    if shared:
        for ip in _lan_ips():
            print("  On the network:   http://%s:%d" % (ip, port))
        print("  SHARED MODE: anyone on the network who opens that address can browse")
        print("  drives this PC can see and start jobs in THIS PC's AutoCAD. Close the window to stop.")
    print("")
    app.run(host=host, port=port, debug=False, threaded=True)
