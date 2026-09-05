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
from typing import Iterable

ENV_FILENAME = ".env"
EXAMPLE_FILENAME = ".env.example"

_VAR_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
_REGION_RE = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*")

# The variables the .env contract knows about. `require=` subsets let
# feature-scoped callers load less than the full runtime contract — the two
# chat deployments are pending a quota increase (open item 1), so data-plane
# work (§9 index build, §7 ASR) must not demand them.
KNOWN_VARS = (
    "AZURE_SEARCH_ENDPOINT",
    "AZURE_SEARCH_ADMIN_KEY",
    "AZURE_SEARCH_QUERY_KEY",
    "AZURE_OPENAI_ENDPOINT",
    "AZURE_OPENAI_API_KEY",
    "AZURE_OPENAI_CHAT_DEPLOYMENT",
    "AZURE_OPENAI_SMALL_CHAT_DEPLOYMENT",
    "AZURE_OPENAI_EMBEDDING_DEPLOYMENT",
    "AZURE_SPEECH_ENDPOINT",
    "AZURE_SPEECH_KEY",
    "AZURE_SPEECH_REGION",
)
REQUIRE_SEARCH = ("AZURE_SEARCH_ENDPOINT", "AZURE_SEARCH_ADMIN_KEY")
REQUIRE_OPENAI_EMBEDDING = (
    "AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_API_KEY",
    "AZURE_OPENAI_EMBEDDING_DEPLOYMENT",
)
REQUIRE_INDEX = REQUIRE_SEARCH + REQUIRE_OPENAI_EMBEDDING
REQUIRE_SPEECH = ("AZURE_SPEECH_ENDPOINT", "AZURE_SPEECH_KEY", "AZURE_SPEECH_REGION")

# Optional by nature (not part of the full runtime contract unless listed):
# the query-only key belongs to the future answer service.
_ALWAYS_OPTIONAL = {"AZURE_SEARCH_QUERY_KEY"}


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
    search_endpoint: str | None = None
    search_admin_key: str | None = None
    openai_endpoint: str | None = None
    openai_api_key: str | None = None
    chat_deployment: str | None = None
    small_chat_deployment: str | None = None
    embedding_deployment: str | None = None
    speech_key: str | None = None
    speech_region: str | None = None
    speech_endpoint: str | None = None
    search_query_key: str | None = None

    def __repr__(self) -> str:  # keys masked — safe for logs and chat
        def s(v: str | None) -> str:
            return v if v else "unset"
        return (
            f"AzureConfig(search={s(self.search_endpoint)}, "
            f"openai={s(self.openai_endpoint)}, "
            f"chat={s(self.chat_deployment)!r}, "
            f"small={s(self.small_chat_deployment)!r}, "
            f"embed={s(self.embedding_deployment)!r}, "
            f"speech_region={s(self.speech_region)!r}, "
            f"speech={s(self.speech_endpoint)}, admin_key=***, api_key=***, "
            f"speech_key=***, "
            f"query_key={'set' if self.search_query_key else 'unset'})"
        )


def load_azure_config(
    env_path: Path | None = None, *, require: Iterable[str] | None = None
) -> AzureConfig:
    """Load and validate the Azure section of ``.env``.

    *require* selects which variables must be present:

    - ``None`` (default) — the full runtime contract: every known variable
      except the always-optional ones (``AZURE_SEARCH_QUERY_KEY``)
    - a subset of ``KNOWN_VARS`` for feature-scoped callers (see the
      ``REQUIRE_*`` constants); unknown names raise (typo guard)

    Present values are always fully validated (placeholder / scheme / shape),
    even when not required — a broken optional is still a broken ``.env``.
    Error messages name variables, never values.
    """
    path = Path(env_path) if env_path is not None else repo_root() / ENV_FILENAME
    if not path.is_file():
        raise AzureConfigError(
            f"{ENV_FILENAME} not found at {path} — copy {EXAMPLE_FILENAME} to "
            f"{ENV_FILENAME} and fill it in"
        )
    values = read_env_file(path)
    default_req = tuple(v for v in KNOWN_VARS if v not in _ALWAYS_OPTIONAL)
    req = default_req if require is None else tuple(require)
    unknown = sorted(set(req) - set(KNOWN_VARS))
    if unknown:
        raise AzureConfigError(
            f"require= contains unknown variable name(s) (typo?): {unknown}"
        )

    def g(name: str) -> str | None:
        v = values.get(name, "").strip() or None
        if v is None:
            if name in req:
                raise AzureConfigError(
                    f"{ENV_FILENAME}: required variable {name} is missing or "
                    f"empty — see {EXAMPLE_FILENAME}"
                )
            return None
        if "<" in v or ">" in v:
            raise AzureConfigError(
                f"{ENV_FILENAME}: {name} still contains a <placeholder> — fill "
                f"in the real value or delete the line"
            )
        return v

    def ge(name: str) -> str | None:
        v = g(name)
        if v is not None and not v.startswith("https://"):
            raise AzureConfigError(
                f"{ENV_FILENAME}: {name} must be an https:// endpoint"
            )
        return v

    region = g("AZURE_SPEECH_REGION")
    if region is not None:
        region = region.lower()
        if not _REGION_RE.fullmatch(region):
            raise AzureConfigError(
                f"{ENV_FILENAME}: AZURE_SPEECH_REGION must be a region "
                f"identifier like 'centralus', got {region!r}"
            )

    return AzureConfig(
        search_endpoint=ge("AZURE_SEARCH_ENDPOINT"),
        search_admin_key=g("AZURE_SEARCH_ADMIN_KEY"),
        search_query_key=g("AZURE_SEARCH_QUERY_KEY"),
        openai_endpoint=ge("AZURE_OPENAI_ENDPOINT"),
        openai_api_key=g("AZURE_OPENAI_API_KEY"),
        chat_deployment=g("AZURE_OPENAI_CHAT_DEPLOYMENT"),
        small_chat_deployment=g("AZURE_OPENAI_SMALL_CHAT_DEPLOYMENT"),
        embedding_deployment=g("AZURE_OPENAI_EMBEDDING_DEPLOYMENT"),
        speech_key=g("AZURE_SPEECH_KEY"),
        speech_region=region,
        speech_endpoint=ge("AZURE_SPEECH_ENDPOINT"),
    )
