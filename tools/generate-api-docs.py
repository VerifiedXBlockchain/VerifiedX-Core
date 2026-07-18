#!/usr/bin/env python3
"""Generate per-controller markdown docs from an OpenAPI swagger.json spec."""

import json
import os
import re
import sys


def load_spec(path):
    with open(path) as f:
        return json.load(f)


def resolve_ref(ref, spec):
    """Resolve a $ref like '#/components/schemas/Foo' to its schema dict."""
    if not ref or not ref.startswith("#/"):
        return None
    parts = ref.lstrip("#/").split("/")
    node = spec
    for part in parts:
        node = node.get(part, {})
    return node


def schema_name_from_ref(ref):
    """Extract a short display name from a $ref string."""
    if not ref:
        return "object"
    name = ref.rsplit("/", 1)[-1]
    # Strip namespace prefix (e.g., 'ReserveBlockCore.Api.Rest.Models.Foo' -> 'Foo')
    if "." in name:
        name = name.rsplit(".", 1)[-1]
    return name


def get_type_str(schema, spec):
    """Convert a schema to a human-readable type string."""
    if not schema:
        return "object"

    if "$ref" in schema:
        return schema_name_from_ref(schema["$ref"])

    typ = schema.get("type", "object")
    fmt = schema.get("format")

    if typ == "array":
        items = schema.get("items", {})
        inner = get_type_str(items, spec)
        return f"{inner}[]"
    if typ == "integer":
        return "integer" if not fmt else fmt
    if typ == "number":
        return "number" if not fmt else fmt
    if typ == "string":
        return "string" if not fmt else f"string ({fmt})"
    if typ == "boolean":
        return "boolean"

    return typ


def get_constraints(prop_name, schema, required_fields):
    """Build a constraints string from schema validation metadata."""
    parts = []
    if schema.get("minimum") is not None:
        parts.append(f"min: {schema['minimum']}")
    if schema.get("maximum") is not None:
        parts.append(f"max: {schema['maximum']}")
    if schema.get("exclusiveMinimum") is not None:
        parts.append(f"> {schema['exclusiveMinimum']}")
    if schema.get("exclusiveMaximum") is not None:
        parts.append(f"< {schema['exclusiveMaximum']}")
    if schema.get("minLength") is not None:
        parts.append(f"minLength: {schema['minLength']}")
    if schema.get("maxLength") is not None:
        parts.append(f"maxLength: {schema['maxLength']}")
    if schema.get("pattern"):
        parts.append(f"pattern: `{schema['pattern']}`")
    if schema.get("enum"):
        parts.append(f"enum: {', '.join(str(e) for e in schema['enum'])}")
    return ", ".join(parts) if parts else "\u2014"


def flatten_properties(schema, spec):
    """Extract property rows from a schema, resolving $ref if needed."""
    if "$ref" in schema:
        schema = resolve_ref(schema["$ref"], spec) or {}

    # Handle allOf
    if "allOf" in schema:
        merged = {}
        req = []
        for sub in schema["allOf"]:
            resolved = sub
            if "$ref" in sub:
                resolved = resolve_ref(sub["$ref"], spec) or {}
            merged.update(resolved.get("properties", {}))
            req.extend(resolved.get("required", []))
        return merged, req

    props = schema.get("properties", {})
    required = schema.get("required", [])
    return props, required


def render_request_body_table(request_body, spec):
    """Render a markdown table for a request body schema."""
    if not request_body:
        return ""

    content = request_body.get("content", {})
    json_content = content.get("application/json", content.get("text/json", {}))
    schema = json_content.get("schema", {})

    if not schema:
        return ""

    # Check if it's a generic object (no defined properties)
    resolved = schema
    if "$ref" in schema:
        resolved = resolve_ref(schema["$ref"], spec) or {}

    props, required_fields = flatten_properties(resolved, spec)

    if not props:
        name = schema_name_from_ref(schema.get("$ref", ""))
        if name and name != "object":
            return f"\n**Body:** `{name}` (JSON)\n"
        return "\n**Body:** JSON object\n"

    lines = [
        "",
        "| Field | Type | Required | Constraints |",
        "|-------|------|----------|-------------|",
    ]

    for prop_name, prop_schema in sorted(props.items()):
        typ = get_type_str(prop_schema, spec)
        req = "Yes" if prop_name in required_fields else "No"
        constraints = get_constraints(prop_name, prop_schema, required_fields)
        lines.append(f"| `{prop_name}` | {typ} | {req} | {constraints} |")

    lines.append("")
    return "\n".join(lines)


def render_parameters(params, spec):
    """Render path/query parameters as a markdown table."""
    if not params:
        return "\n_None_\n"

    # Filter out header params (like apitoken)
    filtered = [p for p in params if p.get("in") != "header"]
    if not filtered:
        return "\n_None_\n"

    lines = [
        "",
        "| Name | In | Type | Required | Description |",
        "|------|----|------|----------|-------------|",
    ]

    for param in filtered:
        name = param.get("name", "")
        location = param.get("in", "")
        schema = param.get("schema", {})
        typ = get_type_str(schema, spec)
        req = "Yes" if param.get("required") else "No"
        desc = param.get("description", "\u2014")
        lines.append(f"| `{name}` | {location} | {typ} | {req} | {desc} |")

    lines.append("")
    return "\n".join(lines)


def render_responses(responses, spec):
    """Render response status codes."""
    if not responses:
        return "\n`200 OK`\n"

    lines = []
    for status, detail in sorted(responses.items()):
        desc = detail.get("description", "")
        lines.append(f"`{status}` {desc}")

    return "\n" + " | ".join(lines) + "\n"


def tag_to_filename(tag):
    """Convert a controller tag to a kebab-case filename."""
    # Insert hyphens before capitals: SmartContracts -> Smart-Contracts
    name = re.sub(r"(?<=[a-z])(?=[A-Z])", "-", tag)
    return name.lower() + ".md"


def strip_base_path(path):
    """Strip /api/rest prefix for display, keeping the controller-relative path."""
    return re.sub(r"^/api/rest", "", path)


def group_endpoints(spec):
    """Group endpoints by controller tag, filtering to /api/rest/ only."""
    controllers = {}
    for path, methods in spec.get("paths", {}).items():
        if not path.startswith("/api/rest/"):
            continue
        for method, details in methods.items():
            if method not in ("get", "post", "put", "delete", "patch"):
                continue
            tag = details.get("tags", ["Other"])[0]
            controllers.setdefault(tag, []).append(
                {
                    "method": method.upper(),
                    "path": path,
                    "display_path": strip_base_path(path),
                    "summary": details.get("summary", ""),
                    "description": details.get("description", ""),
                    "parameters": details.get("parameters", []),
                    "requestBody": details.get("requestBody"),
                    "responses": details.get("responses", {}),
                }
            )
    return controllers


def section_key(path):
    """Extract a section grouping from a path for large controllers."""
    parts = path.strip("/").split("/")
    if len(parts) >= 2:
        return parts[1]
    return ""


def write_controller_md(out_dir, tag, endpoints, spec):
    """Write a markdown file for one controller."""
    filename = tag_to_filename(tag)
    filepath = os.path.join(out_dir, filename)

    # Determine base path
    if endpoints:
        sample_path = endpoints[0]["path"]
        # e.g., /api/rest/wallets/status -> /api/rest/wallets
        parts = sample_path.split("/")
        base = "/".join(parts[:4]) if len(parts) >= 4 else sample_path
    else:
        base = f"/api/rest/{tag.lower()}"

    lines = [
        f"# {tag}",
        "",
        f"> Base path: `{base}`",
        "",
    ]

    # Sort endpoints: by path then by method order
    method_order = {"GET": 0, "POST": 1, "PUT": 2, "PATCH": 3, "DELETE": 4}
    endpoints.sort(key=lambda e: (e["path"], method_order.get(e["method"], 5)))

    for i, ep in enumerate(endpoints):
        if i > 0:
            lines.append("---")
            lines.append("")

        lines.append(f"## {ep['method']} {ep['display_path']}")
        lines.append("")

        if ep["summary"]:
            lines.append(ep["summary"])
            lines.append("")

        # Parameters
        path_and_query_params = [
            p for p in ep["parameters"] if p.get("in") in ("path", "query")
        ]
        if path_and_query_params:
            lines.append("### Parameters")
            lines.append(render_parameters(ep["parameters"], spec))

        # Request body
        if ep["requestBody"]:
            body_table = render_request_body_table(ep["requestBody"], spec)
            if body_table.strip():
                lines.append("### Request Body")
                lines.append(body_table)

        # Response
        lines.append("### Response")
        lines.append(render_responses(ep["responses"], spec))

    with open(filepath, "w") as f:
        f.write("\n".join(lines))

    return filename


def write_index(out_dir, spec, file_links):
    """Write the index README.md."""
    info = spec.get("info", {})
    title = info.get("title", "API Documentation")
    desc = info.get("description", "")
    version = info.get("version", "")

    lines = [
        f"# {title}",
        "",
    ]
    if desc:
        lines.append(desc)
        lines.append("")
    if version:
        lines.append(f"**Version:** {version}")
        lines.append("")

    lines.extend(
        [
            "## Authentication",
            "",
            "Include your API token in the `apitoken` request header. Most endpoints require a valid token when one is configured.",
            "",
            "The `GET /api/rest/wallets/status` endpoint is accessible without authentication.",
            "",
            "## Response Format",
            "",
            "All endpoints return a standard JSON envelope:",
            "",
            "```json",
            "{",
            '  "success": true,',
            '  "data": { ... },',
            '  "error": null,',
            '  "meta": null',
            "}",
            "```",
            "",
            "Error responses:",
            "",
            "```json",
            "{",
            '  "success": false,',
            '  "data": null,',
            '  "error": {',
            '    "code": "ERROR_CODE",',
            '    "message": "Human-readable description"',
            "  }",
            "}",
            "```",
            "",
            "Paginated endpoints include `meta` with `page`, `pageSize`, `totalCount`, `totalPages`.",
            "",
            "## Controllers",
            "",
            "| Controller | Endpoints | Description |",
            "|------------|-----------|-------------|",
        ]
    )

    controller_descriptions = {
        "Wallets": "Wallet lifecycle, encryption, HD wallets",
        "Accounts": "Address management, balances, NFTs",
        "Transactions": "Send, query, and manage transactions",
        "Blocks": "Block data and chain history",
        "Network": "Network status, peers, masternodes",
        "Signatures": "Create and verify signatures",
        "Adnr": "Domain name registration and resolution",
        "SmartContracts": "NFT lifecycle, sales, ownership",
        "Tokens": "Fungible token operations and governance",
        "Voting": "Topic creation and vote casting",
        "Validators": "Validator registration and management",
        "ReserveAccounts": "Reserve (xRBX) account operations",
        "Beacons": "Beacon node management",
        "Shops": "Decentralized shop protocol (DST)",
    }

    for tag, filename, count in sorted(file_links, key=lambda x: x[0]):
        desc = controller_descriptions.get(tag, "")
        lines.append(f"| [{tag}]({filename}) | {count} | {desc} |")

    lines.extend(
        [
            "",
            "---",
            "",
            "*Generated from [swagger.json](swagger.json) using `tools/generate-api-docs.sh`*",
            "",
        ]
    )

    filepath = os.path.join(out_dir, "README.md")
    with open(filepath, "w") as f:
        f.write("\n".join(lines))


def main():
    if len(sys.argv) < 3:
        print(f"Usage: {sys.argv[0]} <swagger.json> <output_dir>")
        sys.exit(1)

    spec_path = sys.argv[1]
    out_dir = sys.argv[2]

    spec = load_spec(spec_path)
    os.makedirs(out_dir, exist_ok=True)

    controllers = group_endpoints(spec)
    file_links = []

    for tag, endpoints in controllers.items():
        filename = write_controller_md(out_dir, tag, endpoints, spec)
        file_links.append((tag, filename, len(endpoints)))
        print(f"  {tag}: {len(endpoints)} endpoints -> {filename}")

    write_index(out_dir, spec, file_links)
    print(f"  Index -> README.md")

    total = sum(count for _, _, count in file_links)
    print(f"\nGenerated {len(file_links)} controller docs ({total} endpoints total)")


if __name__ == "__main__":
    main()
