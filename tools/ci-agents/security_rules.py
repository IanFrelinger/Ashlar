"""Bounded, parse-only security review candidates. No source or secret text is reported.

These deliberately narrow rules are advisory, not a vulnerability scanner or proof
of safety. C# matching has no semantic model, ignores strings/comments (including
interpolations), and cannot follow aliases, helpers, dataflow or preprocessor state.
Workflow review requires PyYAML; ambiguous/unsupported YAML raises ValueError so
the caller can report incomplete coverage rather than a clean scan.
"""
from __future__ import annotations

import re


MAX_BYTES = 1024 * 1024
MAX_YAML_NODES = 20000
MAX_YAML_DEPTH = 40


def _finding(rule, line, message, next_action, confidence="high"):
    return {"rule": rule, "line": line, "message": message,
            "next_action": next_action, "confidence": confidence}


def _csharp_code(text):
    """Mask comments/literals without changing line numbers or character positions."""
    chars = list(text)

    def literal_end(start, depth=0):
        if depth > 40:
            raise ValueError("C# literal nesting limit exceeded")
        quote = text[start]
        prefix = text[max(0, start - 2):start]
        verbatim = quote == '"' and (prefix.endswith('@') or prefix == '@$')
        interpolated = quote == '"' and (prefix.endswith('$') or prefix == '$@')
        count = 1
        while quote == '"' and start + count < len(text) and text[start + count] == quote:
            count += 1
        if not verbatim and quote == '"' and count >= 3:
            end = text.find('"' * count, start + count)
            if end < 0:
                raise ValueError("Unterminated C# raw string; security coverage is incomplete")
            return end + count
        cursor = start + 1
        while cursor < len(text):
            if text[cursor] == quote:
                if verbatim and cursor + 1 < len(text) and text[cursor + 1] == quote:
                    cursor += 2
                    continue
                return cursor + 1
            if interpolated and text[cursor] == '{':
                if cursor + 1 < len(text) and text[cursor + 1] == '{':
                    cursor += 2
                    continue
                cursor = interpolation_end(cursor + 1, depth + 1)
            elif not verbatim and text[cursor] == "\\":
                cursor += 2
            else:
                cursor += 1
        raise ValueError("Unterminated C# literal; security coverage is incomplete")

    def interpolation_end(cursor, depth):
        braces = 1
        while cursor < len(text):
            if text.startswith("//", cursor):
                end = text.find("\n", cursor)
                cursor = len(text) if end < 0 else end
            elif text.startswith("/*", cursor):
                end = text.find("*/", cursor + 2)
                if end < 0:
                    raise ValueError("Unterminated C# interpolation comment")
                cursor = end + 2
            elif text[cursor] in "\"'":
                cursor = literal_end(cursor, depth)
            elif text[cursor] == '{':
                braces += 1
                cursor += 1
            elif text[cursor] == '}':
                braces -= 1
                cursor += 1
                if braces == 0:
                    return cursor
            else:
                cursor += 1
        raise ValueError("Unterminated C# interpolation; security coverage is incomplete")

    index = 0
    while index < len(text):
        start = index
        if text.startswith("//", index):
            end = text.find("\n", index)
            index = len(text) if end < 0 else end
        elif text.startswith("/*", index):
            end = text.find("*/", index + 2)
            if end < 0:
                raise ValueError("Unterminated C# comment; security coverage is incomplete")
            index = end + 2
        elif text[index] in "\"'":
            index = literal_end(index)
        else:
            index += 1
            continue
        for offset in range(start, min(index, len(text))):
            if chars[offset] not in "\r\n":
                chars[offset] = " "
    return "".join(chars)


def _csharp_findings(text):
    code = _csharp_code(text)
    rules = [
        ("security/tls-accept-any", r"\bDangerousAcceptAnyServerCertificateValidator\b",
         "A certificate validator that accepts every server certificate is referenced.",
         "Keep normal certificate validation in production; verify any test-only scope and add a rejected-certificate test."),
        ("security/tls-callback-true",
         r"\b(?:ServerCertificateCustomValidationCallback|ServerCertificateValidationCallback|RemoteCertificateValidationCallback)\s*\+?=\s*(?:static\s+)?(?:\([^;{}]*?\)|[A-Za-z_]\w*)\s*=>\s*(?:true\b|\{\s*return\s+true\s*;)" ,
         "A certificate-validation callback unconditionally returns true.",
         "Use normal certificate validation or narrowly validated trust; verify rejected certificates cannot connect."),
        ("security/binaryformatter-construction",
         r"\bnew\s+(?:(?:global::)?System\.Runtime\.Serialization\.Formatters\.Binary\.)?BinaryFormatter\s*\(",
         "BinaryFormatter is constructed; this serializer is unsafe for untrusted data.",
         "Check the type and input boundary, then migrate to a supported serializer; a binder does not make BinaryFormatter secure."),
    ]
    output = []
    for rule, pattern, message, action in rules:
        for match in re.finditer(pattern, code):
            output.append(_finding(rule, code.count("\n", 0, match.start()) + 1, message, action))
    return output


def _workflow_root(text):
    try:
        import yaml
    except ImportError:
        raise ValueError("PyYAML is required; workflow security coverage is incomplete") from None
    try:
        for count, token in enumerate(yaml.scan(text)):
            if count > MAX_YAML_NODES * 5:
                raise ValueError("Workflow YAML token limit exceeded")
            if isinstance(token, (yaml.tokens.AliasToken, yaml.tokens.AnchorToken)):
                raise ValueError("Workflow YAML anchors and aliases are unsupported")
        root = yaml.compose(text, Loader=yaml.BaseLoader)
        nodes = 0

        def validate(node, depth=0):
            nonlocal nodes
            nodes += 1
            if nodes > MAX_YAML_NODES or depth > MAX_YAML_DEPTH:
                raise ValueError("Workflow YAML structure limit exceeded")
            if isinstance(node, yaml.ScalarNode):
                if node.tag != "tag:yaml.org,2002:str":
                    raise ValueError("Workflow YAML tags are unsupported")
            elif isinstance(node, yaml.SequenceNode):
                if node.tag != "tag:yaml.org,2002:seq":
                    raise ValueError("Workflow YAML tags are unsupported")
                for child in node.value:
                    validate(child, depth + 1)
            elif isinstance(node, yaml.MappingNode):
                if node.tag != "tag:yaml.org,2002:map":
                    raise ValueError("Workflow YAML tags are unsupported")
                seen = set()
                for key, value in node.value:
                    if not isinstance(key, yaml.ScalarNode) or key.value in seen or key.value == "<<":
                        raise ValueError("Workflow YAML has ambiguous or unsupported mapping keys")
                    seen.add(key.value)
                    validate(key, depth + 1)
                    validate(value, depth + 1)
            else:
                raise ValueError("Workflow YAML node is unsupported")

        if not isinstance(root, yaml.MappingNode):
            raise ValueError("Workflow YAML must be a mapping")
        validate(root)
        return root
    except (yaml.YAMLError, RecursionError):
        # PyYAML diagnostics can embed source and secrets; emit fixed text only.
        raise ValueError("Workflow YAML could not be parsed safely") from None


def _mapping(node):
    if node is None or node.id != "mapping":
        return {}
    return {key.value: value for key, value in node.value}


def _scalar(node):
    return node.value if node is not None and node.id == "scalar" else ""


def _workflow_findings(text):
    root = _workflow_root(text)
    top = _mapping(root)
    event = top.get("on")
    if event is None:
        raise ValueError("Workflow trigger is missing; security coverage is incomplete")
    if event.id == "mapping":
        events = set(_mapping(event))
    elif event.id == "sequence":
        events = {_scalar(child) for child in event.value}
    else:
        events = {_scalar(event)}
    output = []

    def permissions(mapping):
        node = mapping.get("permissions")
        if _scalar(node) == "write-all":
            output.append(_finding("security/workflow-write-all", node.start_mark.line + 1,
                "The workflow grants write access to every available token scope.",
                "Declare the minimum read-only defaults and grant individual write permissions only where required."))

    permissions(top)
    jobs = top.get("jobs")
    if jobs is None or jobs.id != "mapping":
        raise ValueError("Workflow jobs must be a mapping; security coverage is incomplete")
    for job in _mapping(jobs).values():
        if job.id != "mapping":
            raise ValueError("Workflow job is not a mapping")
        properties = _mapping(job)
        permissions(properties)
        steps = properties.get("steps")
        if steps is None:  # Reusable workflow jobs have no steps.
            continue
        if steps.id != "sequence":
            raise ValueError("Workflow steps must be a sequence")
        for step in steps.value:
            if step.id != "mapping":
                raise ValueError("Workflow step is not a mapping")
            fields = _mapping(step)
            uses = fields.get("uses")
            action = _scalar(uses)
            if re.fullmatch(r"[^\s/@]+/[^\s@]+@(?:main|master|develop)", action):
                output.append(_finding("security/action-moving-branch", uses.start_mark.line + 1,
                    "A remote action uses a moving development-branch reference.",
                    "Review and lock the action dependency or pin a verified full commit SHA; this rule does not audit tagged actions.", "medium"))
            inputs = _mapping(fields.get("with"))
            checkout_source = " ".join(_scalar(inputs.get(key)) for key in ("ref", "repository"))
            if ("pull_request_target" in events and action.startswith("actions/checkout@")
                    and (re.search(r"github\s*\.\s*event\s*\.\s*pull_request\s*\.\s*head\b", checkout_source)
                         or re.search(r"refs/pull/.*?/(?:head|merge)", checkout_source))):
                output.append(_finding("security/privileged-pr-checkout", uses.start_mark.line + 1,
                    "A pull_request_target workflow checks out contributor-controlled source.",
                    "Keep that source as data only; do not build or execute it with privileged tokens or secrets. Prefer pull_request for testing."))
            run = fields.get("run")
            script = _scalar(run)
            for expression in re.finditer(r"\$\{\{(.*?)\}\}", script, re.DOTALL):
                value = expression.group(1)
                # Narrow known free-text event fields; env entries are deliberately not scanned.
                if re.search(r"\bgithub\s*\.\s*(?:head_ref\b|event\s*\.\s*(?:(?:pull_request|issue|comment|review|discussion)\s*\.\s*(?:title|body)\b|(?:head_commit|commits\s*\[[^]]*\])\s*\.\s*message\b|pull_request\s*\.\s*head\s*\.\s*(?:ref|label)\b))", value):
                    output.append(_finding("security/untrusted-expression-in-run", run.start_mark.line + 1,
                        "An untrusted event text expression is interpolated directly into a run script.",
                        "Pass the value through an intermediate environment variable or an action argument, and quote it as data in the script."))
                    break
    return output


def findings(text: str, path: str) -> list[dict]:
    """Return fixed-text candidates; raise ValueError when supported input is incomplete."""
    if not isinstance(text, str) or not isinstance(path, str):
        raise ValueError("Security review requires text and a repository path")
    if len(text.encode("utf-8")) > MAX_BYTES or "\0" in text:
        raise ValueError("Security review input exceeds limits or contains NUL")
    if path.endswith(".cs"):
        result = _csharp_findings(text)
    elif path.startswith(".github/workflows/") and path.endswith((".yml", ".yaml")):
        result = _workflow_findings(text)
    else:
        result = []
    return sorted(result, key=lambda item: (item["line"], item["rule"]))
