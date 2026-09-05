"""Azure service credentials loader (plan §4 Stage 2, §7 ASR, §9–11 index).

Secrets live in the gitignored ``.env`` at the repo root and nowhere else:
not in code, not in tests, not in artifacts, not in chat. This module reads
that file and validates its shape, and is deliberate about *never echoing
values*: error messages name the offending variable, and ``repr(AzureConfig)``
shows endpoints/deployments but masks keys.

Deliberately not routed through ``io_utils`` — that choke point exists for
corpus I/O determinism; ``.env`` is machine-local configuration, read with
``utf-8-sig`` so Windows editors may add a BOM.

Parsing rules: ``KEY=VALUE``, optional ``export `` prefix, optional matching
surrounding quotes on the value, full-line ``#`` comments, and unquoted
inline comments (whitespace before ``#``) stripped — dotenv convention. A
``#`` inside quotes, or without preceding whitespace, is a value character.
Malformed
lines and duplicate keys raise rather than silently dropping a secret the
operator believed was set.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path

ENV_FILENAME = ".env"
EXAMPLE_FILENAME = ".env.example"

_VAR_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
_REGION_RE = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*")


class AzureConfigError(RuntimeError):
    """``.env`` missing, malformed, or incomplete. Carries variable names only."""


def repo_root() -> Path:
    """Repo root derived from this file's location, never from the cwd."""
    return Path(__file__).resolve().parents[3]


def parse_env(text: str) -> dict[str, str]:
    """Parse ``KEY=VALUE`` lines. Strict: bad lines and duplicates raise."""
    values: dict[str, str] = {}
    for lineno, raw in enumerate(text.splitlines(), start=1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("export "):
            line = line[len("export "):].lstrip()
        key, sep, value = line.partition("=")
        key = key.strip()
        if not sep or not _VAR_RE.fullmatch(key):
            raise AzureConfigError(f"{ENV_FILENAME} line {lineno}: expected KEY=VALUE")
        if key in values:
            raise AzureConfigError(f"{ENV_FILENAME} line {lineno}: duplicate key {key}")
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in ("'", '"'):
            value = value[1:-1]
        elif value.startswith("#"):
            value = ""
        elif (m := re.search(r"\s#", value)):
            value = value[: m.start()].rstrip()
        values[key] = value
    return values


def read_env_file(path: Path) -> dict[str, str]:
    """Read and parse *path* (``utf-8-sig`` tolerates a Windows BOM)."""
    return parse_env(path.read_text(encoding="utf-8-sig"))


@dataclass(frozen=True, repr=False)
class AzureConfig:
    search_endpoint: str
    search_admin_key: str
    openai_endpoint: str
    openai_api_key: str
    chat_deployment: str
    small_chat_deployment: str
    embedding_deployment: str
    speech_key: str
    speech_region: str
    speech_endpoint: str
    search_query_key: str | None = None

    def __repr__(self) -> str:  # keys masked — safe for logs and chat
        q = "set" if self.search_query_key else "unset"
        return (
            f"AzureConfig(search={self.search_endpoint}, openai={self.openai_endpoint}, "
            f"chat={self.chat_deployment!r}, small={self.small_chat_deployment!r}, "
            f"embed={self.embedding_deployment!r}, speech_region={self.speech_region!r}, "
            f"speech={self.speech_endpoint}, "
            f"admin_key=***, api_key=***, speech_key=***, query_key={q})"
        )


def _required(values: dict[str, str], name: str) -> str:
    v = values.get(name, "").strip()
    if not v:
        raise AzureConfigError(
            f"{ENV_FILENAME}: required variable {name} is missing or empty — "
            f"see {EXAMPLE_FILENAME}"
        )
    if "<" in v or ">" in v:
        raise AzureConfigError(
            f"{ENV_FILENAME}: {name} still contains a <placeholder> — fill in the real value"
        )
    return v


def _endpoint(values: dict[str, str], name: str) -> str:
    v = _required(values, name)
    if not v.startswith("https://"):
        raise AzureConfigError(f"{ENV_FILENAME}: {name} must be an https:// endpoint")
    return v


def load_azure_config(env_path: Path | None = None) -> AzureConfig:
    """Load and validate the Azure section of ``.env``.

    *env_path* defaults to the repo-root ``.env`` regardless of cwd. Raises
    ``AzureConfigError`` naming the problem variable; never echoes values.
    """
    path = Path(env_path) if env_path is not None else repo_root() / ENV_FILENAME
    if not path.is_file():
        raise AzureConfigError(
            f"{ENV_FILENAME} not found at {path} — copy {EXAMPLE_FILENAME} to "
            f"{ENV_FILENAME} and fill it in"
        )
    values = read_env_file(path)

    region = _required(values, "AZURE_SPEECH_REGION").lower()
    if not _REGION_RE.fullmatch(region):
        raise AzureConfigError(
            f"{ENV_FILENAME}: AZURE_SPEECH_REGION must be a region identifier "
            f"like 'centralus', got {region!r}"
        )

    query = values.get("AZURE_SEARCH_QUERY_KEY", "").strip() or None
    if query is not None and ("<" in query or ">" in query):
        raise AzureConfigError(
            f"{ENV_FILENAME}: AZURE_SEARCH_QUERY_KEY still contains a <placeholder>"
        )

    return AzureConfig(
        search_endpoint=_endpoint(values, "AZURE_SEARCH_ENDPOINT"),
        search_admin_key=_required(values, "AZURE_SEARCH_ADMIN_KEY"),
        openai_endpoint=_endpoint(values, "AZURE_OPENAI_ENDPOINT"),
        openai_api_key=_required(values, "AZURE_OPENAI_API_KEY"),
        chat_deployment=_required(values, "AZURE_OPENAI_CHAT_DEPLOYMENT"),
        small_chat_deployment=_required(values, "AZURE_OPENAI_SMALL_CHAT_DEPLOYMENT"),
        embedding_deployment=_required(values, "AZURE_OPENAI_EMBEDDING_DEPLOYMENT"),
        speech_key=_required(values, "AZURE_SPEECH_KEY"),
        speech_region=region,
        speech_endpoint=_endpoint(values, "AZURE_SPEECH_ENDPOINT"),
        search_query_key=query,
    )
