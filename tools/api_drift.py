#!/usr/bin/env python3
"""Endpoint inventory + drift gate for the VFX v1 (RPC) and v2 (REST) API layers.

Why this exists
---------------
v1 (the RPC-style controllers under ReserveBlockCore/Controllers and
ReserveBlockCore/Bitcoin/Controllers) and v2 (the REST layer under
ReserveBlockCore/Api/Rest/Controllers) are maintained independently. v2 is a
*parallel reimplementation* over the same data/service layer, not a wrapper over
v1. The standing risk is that someone extends a v1 controller and the v2 layer
silently falls behind. This tool makes that drift visible and gate-able.

What it does
------------
  1. `inventory` -- extract a normalized endpoint inventory from either layer,
     resolving both routing conventions used in this repo:
       - v1: class [Route("api/[controller]")] + action [HttpGet]/[Route("Method/{p}")]
       - v2: RestBaseController [Route("api/rest/[controller]")] + [HttpGet("template")]
  2. `check` -- load the checked-in coverage map (docs/api/v2-coverage-map.txt),
     re-extract the current v1 inventory, and require every v1 endpoint to be
     classified. Any unclassified endpoint is drift: `check` prints it and exits
     non-zero. It also prints the standing `gap` backlog (integrator-facing v1
     endpoints v2 does not yet implement) on every run.
  3. `genmap` -- (re)generate the coverage map from the current v1 inventory
     using per-controller default classifications. Run once to seed the map, or
     after a deliberate scope change; review the diff before committing.

Endpoint classification (one status per v1 endpoint in the map):
    covered  v2 exposes an equivalent endpoint
    gap      integrator-facing, v2 SHOULD cover it but does not yet  (actionable)
    omit     in-scope but deliberately not exposed by v2 (GUI-only/debug/deprecated)
    oos      out of scope: node-to-node P2P / consensus / internal; never REST

stdlib only -- no restore or pip step required to run in CI.

Usage:
  api_drift.py inventory --layer {v1,v2} [--json] [--root DIR]
  api_drift.py check [--map FILE] [--root DIR] [--strict]
  api_drift.py genmap [--out FILE] [--root DIR]
"""

import argparse
import json
import os
import re
import sys


REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

V1_DIRS = [
    "ReserveBlockCore/Controllers",
    "ReserveBlockCore/Bitcoin/Controllers",
]
V2_DIRS = [
    "ReserveBlockCore/Api/Rest/Controllers",
]

DEFAULT_MAP = "docs/api/v2-coverage-map.txt"

HTTP_VERBS = ("HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch")

VALID_STATUSES = ("covered", "gap", "omit", "oos")

# Per-controller default classification, applied by genmap. Endpoint-level
# overrides can be hand-edited into the map afterward; genmap preserves any
# existing per-endpoint status it finds in the current map (see cmd_genmap).
CONTROLLER_DEFAULT = {
    # node-to-node P2P / consensus / internal -- never part of a REST surface
    "ADJV1Controller": "oos",
    "BCV1Controller": "oos",
    "ValidatorController": "oos",
    "IntegrationsV1Controller": "oos",
    # integrator-facing domains v2 has not implemented yet -- standing gaps
    "VBTCController": "gap",
    "BTCV2Controller": "gap",
    "PrivacyV1Controller": "gap",
    "WalletController": "gap",        # [Route("wallet")] GUI/browser wallet API
    "ExplorerController": "gap",
    # controllers whose resource v2 mirrors as a curated subset. Default to
    # 'omit' (v2 exposes a selected subset, not 1:1) and promote the specific
    # endpoints v2 actually implements to 'covered' by hand / heuristic.
    "V1Controller": "omit",
    "V2Controller": "omit",
    "TXV1Controller": "omit",
    "SCV1Controller": "omit",
    "TKV2Controller": "omit",
    "DSTV1Controller": "omit",
    "WebShopV1Controller": "omit",
    "VOV1Controller": "omit",
    "RSV1Controller": "omit",
}


# ---------------------------------------------------------------------------
# Endpoint extraction
# ---------------------------------------------------------------------------

class Endpoint:
    __slots__ = ("verb", "route", "controller", "action", "source")

    def __init__(self, verb, route, controller, action, source):
        self.verb = verb
        self.route = route
        self.controller = controller
        self.action = action
        self.source = source

    @property
    def key(self):
        return f"{self.verb} /{self.route}"

    def as_dict(self):
        return {
            "verb": self.verb, "route": self.route, "controller": self.controller,
            "action": self.action, "source": self.source,
        }


def _kebab(name):
    return re.sub(r"(?<!^)(?=[A-Z])", "-", name).lower()


def _normalize_route(route):
    route = route.strip().strip("/")
    route = re.sub(r"\{([^}:=]+)[^}]*\}", r"{\1}", route)          # {id:int} -> {id}
    route = re.sub(r"/?\{somepassword\?\}", "", route, flags=re.IGNORECASE)
    route = re.sub(r"/{2,}", "/", route)
    return route.lower().strip("/")


def _resolve_tokens(template, stem, action):
    return template.replace("[controller]", stem).replace("[action]", action)


def _class_base_route(class_attrs, controller_stem, is_v2):
    routes = []
    for attr in class_attrs:
        m = re.search(r'Route\(\s*"([^"]*)"', attr)
        if m:
            routes.append(m.group(1))
    routes.sort(key=lambda r: ("somepassword" in r.lower(), len(r)))
    if routes:
        base = routes[0]
    elif is_v2:
        base = "api/rest/[controller]"
    else:
        base = ""
    stem = _kebab(controller_stem) if is_v2 else controller_stem
    return _resolve_tokens(base, stem, "")


_METHOD_RE = re.compile(r"public\s+(?:async\s+)?[\w<>\[\],\.\?\s]+?\s+(\w+)\s*\(")
_ATTR_LINE_RE = re.compile(r"^\s*\[(.+)\]\s*$")


_CLASS_RE = re.compile(r"^\s*public\s+(?:abstract\s+|sealed\s+|partial\s+)*class\s+(\w+Controller)\b")


def extract_from_file(path, is_v2):
    with open(path, encoding="utf-8", errors="replace") as f:
        text = f.read()

    lines = text.splitlines()
    # Single pass: _ATTR_LINE_RE uses a greedy body so it captures attributes with
    # nested brackets like [Route("api/[controller]")] correctly. Accumulate the
    # attribute lines immediately above the controller class as the class attrs,
    # then keep accumulating per-method attrs below it.
    controller_name = None
    controller_stem = None
    base_route = None
    stem_for_token = None
    endpoints = []
    pending_attrs = []

    for line in lines:
        if controller_name is None:
            cm = _CLASS_RE.match(line)
            am0 = _ATTR_LINE_RE.match(line)
            if am0:
                pending_attrs.append(am0.group(1))
                continue
            if cm:
                controller_name = cm.group(1)
                controller_stem = controller_name[:-len("Controller")]
                base_route = _class_base_route(pending_attrs, controller_stem, is_v2)
                stem_for_token = _kebab(controller_stem) if is_v2 else controller_stem
                pending_attrs = []
                continue
            # a non-attribute, non-class line before the class resets pending attrs
            if line.strip() and not line.strip().startswith(("//", "*", "/*", "using", "namespace")):
                pending_attrs = []
            continue

        stripped = line.strip()
        am = _ATTR_LINE_RE.match(line)
        if am:
            pending_attrs.append(am.group(1))
            continue
        mm = _METHOD_RE.search(line)
        if mm and "public" in line:
            action = mm.group(1)
            verbs, templates = [], []
            for attr in pending_attrs:
                a = attr.strip()
                for verb in HTTP_VERBS:
                    vm = re.match(rf'{verb}\b(?:\(\s*(?:Name\s*=\s*"[^"]*"\s*,\s*)?"([^"]*)")?', a)
                    if vm:
                        verbs.append(verb.replace("Http", "").upper())
                        if vm.group(1):
                            templates.append(vm.group(1))
                rm = re.match(r'Route\(\s*"([^"]*)"', a)
                if rm:
                    templates.append(rm.group(1))
            if verbs:
                if not templates:
                    templates = [""]
                for verb in dict.fromkeys(verbs):
                    for tmpl in dict.fromkeys(templates):
                        t = _resolve_tokens(tmpl, stem_for_token, action)
                        full = f"{base_route}/{t}" if (base_route and t) else (t or base_route)
                        endpoints.append(Endpoint(
                            verb, _normalize_route(full), controller_name, action,
                            os.path.relpath(path, REPO_ROOT)))
            pending_attrs = []
        elif stripped and not stripped.startswith(("//", "*", "/*")):
            pending_attrs = []
    return endpoints


def collect_layer(root, dirs, is_v2):
    endpoints = []
    for d in dirs:
        full_dir = os.path.join(root, d)
        if not os.path.isdir(full_dir):
            continue
        for name in sorted(os.listdir(full_dir)):
            if not name.endswith("Controller.cs") or name == "ActionFilterController.cs":
                continue
            endpoints.extend(extract_from_file(os.path.join(full_dir, name), is_v2))
    seen, unique = set(), []
    for ep in endpoints:
        k = (ep.key, ep.controller)
        if k in seen:
            continue
        seen.add(k)
        unique.append(ep)
    return unique


# ---------------------------------------------------------------------------
# Coverage map: flat, per-endpoint, diff-friendly.
#   STATUS  VERB  /route              # Controller.Action
# ---------------------------------------------------------------------------

def load_map(path):
    """Return {endpoint_key: status}. endpoint_key == 'VERB /normalized-route'."""
    mapping = {}
    if not os.path.exists(path):
        return mapping
    with open(path, encoding="utf-8") as f:
        for raw in f:
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            body = line.split("#", 1)[0].strip()
            parts = body.split(None, 2)
            if len(parts) < 3:
                continue
            status, verb, route = parts[0], parts[1], parts[2]
            if status not in VALID_STATUSES:
                continue
            key = f"{verb.upper()} /{_normalize_route(route)}"
            mapping[key] = status
    return mapping


def load_map_reasons(path):
    """Return {endpoint_key: reason} for map lines carrying a '-- reason' comment
    suffix (used on omit lines to document why an endpoint is deliberately not
    exposed by v2). genmap re-emits these so hand-written reasons survive regen."""
    reasons = {}
    if not os.path.exists(path):
        return reasons
    with open(path, encoding="utf-8") as f:
        for raw in f:
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            body, _, comment = line.partition("#")
            if " -- " not in comment:
                continue
            parts = body.split(None, 2)
            if len(parts) < 3 or parts[0] not in VALID_STATUSES:
                continue
            key = f"{parts[1].upper()} /{_normalize_route(parts[2])}"
            reasons[key] = comment.split(" -- ", 1)[1].strip()
    return reasons


def _map_line(status, ep, reason=None):
    line = f"{status:8}{ep.verb:7}/{ep.route:60} # {ep.controller}.{ep.action}"
    if reason:
        line += f" -- {reason}"
    return line


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

def cmd_inventory(args):
    root = args.root or REPO_ROOT
    dirs, is_v2 = (V1_DIRS, False) if args.layer == "v1" else (V2_DIRS, True)
    eps = collect_layer(root, dirs, is_v2)
    if args.json:
        print(json.dumps([e.as_dict() for e in eps], indent=2))
    else:
        for e in sorted(eps, key=lambda x: (x.controller, x.route, x.verb)):
            print(f"{e.verb:6} /{e.route:60} {e.controller}.{e.action}")
        print(f"\n# {len(eps)} endpoints in {args.layer}", file=sys.stderr)
    return 0


def cmd_genmap(args):
    root = args.root or REPO_ROOT
    out = args.out or os.path.join(root, DEFAULT_MAP)
    existing = load_map(out)  # preserve any hand-tuned per-endpoint statuses
    reasons = load_map_reasons(out)  # preserve hand-written '-- reason' comments
    v1 = collect_layer(root, V1_DIRS, is_v2=False)

    lines = [
        "# VFX v1 -> v2 API coverage map. One line per v1 endpoint.",
        "#   STATUS  VERB  /route   # Controller.Action",
        "# STATUS: covered | gap | omit | oos   (see tools/api_drift.py header)",
        "# Regenerate with: python3 tools/api_drift.py genmap",
        "# The drift gate (`check`) fails if any live v1 endpoint is missing here.",
        "",
    ]
    for ep in sorted(v1, key=lambda x: (x.controller, x.route, x.verb)):
        status = existing.get(ep.key) or CONTROLLER_DEFAULT.get(ep.controller, "gap")
        lines.append(_map_line(status, ep, reasons.get(ep.key)))
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"wrote {out} ({len(v1)} endpoints)")
    return 0


def cmd_check(args):
    root = args.root or REPO_ROOT
    map_path = args.map or os.path.join(root, DEFAULT_MAP)
    mapping = load_map(map_path)
    v1 = collect_layer(root, V1_DIRS, is_v2=False)

    counts = {s: 0 for s in VALID_STATUSES}
    unclassified, gaps = [], []
    for ep in v1:
        status = mapping.get(ep.key)
        if status is None:
            unclassified.append(ep)
        else:
            counts[status] += 1
            if status == "gap":
                gaps.append(ep)

    live_keys = {ep.key for ep in v1}
    stale = sorted(k for k in mapping if k not in live_keys)

    print(f"v1 endpoints:      {len(v1)}")
    print(f"  covered:         {counts['covered']}")
    print(f"  gap (uncovered): {counts['gap']}")
    print(f"  omit:            {counts['omit']}")
    print(f"  oos:             {counts['oos']}")
    print(f"  UNCLASSIFIED:    {len(unclassified)}")
    print(f"  stale map lines: {len(stale)}")

    # Standing backlog, grouped by controller, printed every run.
    if gaps:
        by_ctrl = {}
        for e in gaps:
            by_ctrl.setdefault(e.controller, 0)
            by_ctrl[e.controller] += 1
        print("\nStanding v2 coverage gaps (integrator-facing v1 endpoints, not in v2):")
        for c in sorted(by_ctrl, key=lambda c: -by_ctrl[c]):
            print(f"  {by_ctrl[c]:3}  {c}")

    rc = 0
    if unclassified:
        print("\nDRIFT: v1 endpoints not present in the coverage map.")
        print("Classify each (covered/gap/omit/oos) -- run `genmap` then review, or add by hand:")
        for e in sorted(unclassified, key=lambda x: (x.controller, x.route)):
            print(f"  {e.verb:6} /{e.route:55} {e.controller}.{e.action}")
        rc = 1
    if stale:
        print(f"\nStale map lines (endpoint no longer in v1) -- {'FAIL (--strict)' if args.strict else 'warning'}:")
        for s in stale:
            print(f"  {s}")
        if args.strict:
            rc = 1
    if rc == 0:
        print("\nOK: every live v1 endpoint is classified.")
    return rc


def main():
    p = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    pi = sub.add_parser("inventory")
    pi.add_argument("--layer", choices=["v1", "v2"], required=True)
    pi.add_argument("--json", action="store_true")
    pi.add_argument("--root")
    pi.set_defaults(func=cmd_inventory)

    pc = sub.add_parser("check")
    pc.add_argument("--map")
    pc.add_argument("--root")
    pc.add_argument("--strict", action="store_true")
    pc.set_defaults(func=cmd_check)

    pg = sub.add_parser("genmap")
    pg.add_argument("--out")
    pg.add_argument("--root")
    pg.set_defaults(func=cmd_genmap)

    args = p.parse_args()
    sys.exit(args.func(args) or 0)


if __name__ == "__main__":
    main()
