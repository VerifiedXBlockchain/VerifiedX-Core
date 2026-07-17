# v1 <-> v2 API Drift Policy

## The problem

v1 (the RPC-style controllers under `ReserveBlockCore/Controllers/` and
`ReserveBlockCore/Bitcoin/Controllers/`) and v2 (the REST layer under
`ReserveBlockCore/Api/Rest/Controllers/`) will both be supported for a while.
New integrators use v2; existing tooling uses v1. v2 is a **parallel
reimplementation** over the same data/service layer, not a wrapper over v1.

The risk: someone extends a v1 controller — a new endpoint, or new behavior on an
existing one — and v2 is not given the same treatment, so the two APIs silently
diverge. This is most likely on the actively-developed controllers (vBTC/bridge,
validator/consensus), whose authors do not work in the v2 layer.

## The tool: `tools/api_drift.py`

A dependency-free (stdlib-only) Python script that extracts an endpoint inventory
from each layer and gates on a checked-in coverage map. It handles both routing
conventions in this repo (v1 `[Route("api/[controller]")]` + `[HttpGet]`/`[Route("Method/{p}")]`;
v2 `RestBaseController` base route + `[HttpGet("template")]`).

```bash
# Inventory (human-readable or --json)
python3 tools/api_drift.py inventory --layer v1
python3 tools/api_drift.py inventory --layer v2

# Drift gate: fail (exit 1) if any live v1 endpoint is not classified in the map
python3 tools/api_drift.py check

# Regenerate the map from the current v1 surface, preserving existing
# per-endpoint statuses (new endpoints get their controller's default).
python3 tools/api_drift.py genmap
```

### The coverage map: `docs/api/v2-coverage-map.txt`

One line per v1 endpoint, diff-friendly so a reviewer sees exactly which line a
PR adds or changes:

```
STATUS   VERB   /route                         # Controller.Action
covered  POST   /api/v1/sendtransaction/...     # V1Controller.SendTransaction
gap      POST   /vbtcapi/vbtc/bridgetobase      # VBTCController.BridgeToBase
oos      POST   /valapi/validator/submitattestation  # ValidatorController.SubmitAttestation
```

Status values:

| Status | Meaning | Action implied |
|---|---|---|
| `covered` | v2 exposes an equivalent | keep v2 in sync when v1 changes |
| `gap` | integrator-facing, v2 does not cover it yet | backlog item; build in v2 when prioritized |
| `omit` | tracked controller, deliberately not in v2's curated subset | none |
| `oos` | node-to-node P2P / consensus / internal — never REST | none |

### What `check` does

1. Re-extracts the current v1 inventory.
2. Every v1 endpoint must appear in the map. Any that doesn't is **unclassified
   drift** — printed, and the command exits non-zero.
3. Prints the standing `gap` backlog (counts per controller) on every run, so the
   uncovered domains (vBTC bridge, privacy) stay visible.
4. `--strict` additionally fails on **stale** map lines (an endpoint removed from
   v1 but still in the map), so renames/removals get cleaned up too.

The gate is green today (all 535 endpoints classified). It goes red the moment a
new v1 endpoint appears without a decision about v2.

## The policy

**When you add or rename an endpoint on a v1 controller:**

1. Run `python3 tools/api_drift.py check`. If it fails, it names your endpoint(s).
2. Decide the v2 treatment and record it in `docs/api/v2-coverage-map.txt`:
   - Built the v2 equivalent too? → `covered`.
   - Integrator-facing but deferring v2? → `gap` (it enters the backlog).
   - P2P/consensus/internal, or intentionally not in v2's REST surface? →
     `oos` / `omit`.
   - Fastest path: `python3 tools/api_drift.py genmap` (new endpoints get their
     controller's default status), then review the diff and fix any that guessed
     wrong.
3. If you **renamed or removed** an endpoint, run `check --strict` and delete the
   stale line.
4. Commit the map change **in the same PR** as the controller change. The map
   diff is the reviewable record of the v2 decision.

**When you change the _behavior_ of a `covered` endpoint** (not just its route —
e.g. a new field, a changed validation, a different calculation): update the v2
reimplementation to match, or downgrade it to `gap` with a note. Route tooling
**cannot** detect this; it relies on author discipline. See the "Semantic drift"
section of [../verification/drift-audit-2026-07.md](../verification/drift-audit-2026-07.md).

## CI integration (suggested)

`check` is a plain script with a non-zero exit on drift — drop it into the
existing `.github/workflows/` as a fast, restore-free step:

```yaml
  api-drift:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: python3 tools/api_drift.py check
```

Run it as a required check on PRs that touch `ReserveBlockCore/**/Controllers/`.
Because it needs no `dotnet restore`, it finishes in seconds. Start non-blocking
(annotate only) if you want a grace period, then make it required.

## Limitations (by design)

- **Route-level only.** Detects added/removed/renamed endpoints; does not detect
  behavioral drift on endpoints whose route is unchanged.
- **`covered` vs `omit` is a human judgment**, seeded by a name/route heuristic in
  `genmap`. The gate does not depend on that split being perfect — only on every
  endpoint being classified.
- **Static parse, not runtime.** It reads controller source, so an endpoint
  registered by an unusual mechanism (dynamic routing, a base-class action not
  matching the `Http*`/`Route` attribute patterns) could be missed. All current
  controllers follow the two documented conventions.
