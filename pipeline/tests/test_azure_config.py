"""Azure env loader tests — synthetic fixtures only, never real credentials."""

from __future__ import annotations

import pytest

from qa_pipeline.azure_config import (
    AzureConfig, AzureConfigError, load_azure_config, parse_env, read_env_file,
    repo_root,
)

FAKE = {
    "AZURE_SEARCH_ENDPOINT": "https://example-search.search.windows.net",
    "AZURE_SEARCH_ADMIN_KEY": "fake-admin-key-0000000000000000000000000000",
    "AZURE_SEARCH_QUERY_KEY": "fake-query-key-00000000000000000000000000000",
    "AZURE_OPENAI_ENDPOINT": "https://example-aoai.openai.azure.com",
    "AZURE_OPENAI_API_KEY": "fake-api-key-000000000000000000000000000000",
    "AZURE_OPENAI_CHAT_DEPLOYMENT": "chat-frontier",
    "AZURE_OPENAI_SMALL_CHAT_DEPLOYMENT": "chat-small",
    "AZURE_OPENAI_EMBEDDING_DEPLOYMENT": "embed-large",
    "AZURE_SPEECH_KEY": "fake-speech-key-00000000000000000000000000000",
    "AZURE_SPEECH_ENDPOINT": "https://example-speech.api.cognitive.microsoft.com",
    "AZURE_SPEECH_REGION": "centralus",
}


def _env_text(**overrides: str) -> str:
    merged = dict(FAKE)
    merged.update({k: v for k, v in overrides.items() if v is not None})
    for k, v in overrides.items():  # None deletes the variable
        if v is None:
            merged.pop(k, None)
    return "".join(f"{k}={v}\n" for k, v in merged.items())


def _env_file(tmp_path, text: str):
    path = tmp_path / ".env"
    path.write_bytes(text.encode("utf-8"))
    return path


# --- parse_env --------------------------------------------------------------


def test_parse_env_comments_blanks_quotes_and_export():
    text = (
        "# full-line comment\n"
        "\n"
        "export AZURE_SPEECH_REGION=centralus\n"
        "AZURE_OPENAI_API_KEY=\"quoted key=\"  \n"
        "AZURE_OPENAI_CHAT_DEPLOYMENT='single'\n"
        "# another comment\n"
    )
    assert parse_env(text) == {
        "AZURE_SPEECH_REGION": "centralus",
        "AZURE_OPENAI_API_KEY": "quoted key=",  # '=' padding survives partition; quotes stripped
        "AZURE_OPENAI_CHAT_DEPLOYMENT": "single",
    }


def test_parse_env_rejects_malformed_line():
    with pytest.raises(AzureConfigError, match="line 2"):
        parse_env("A=1\nthis is not an assignment\n")


def test_parse_env_rejects_duplicate_key():
    with pytest.raises(AzureConfigError, match="duplicate key A"):
        parse_env("A=1\nA=2\n")


def test_parse_env_inline_comments():
    # dotenv convention: whitespace-then-# starts a comment in unquoted values
    text = (
        "AZURE_OPENAI_EMBEDDING_DEPLOYMENT=text-embedding-3-large     # index embeddings\n"
        "AZURE_OPENAI_CHAT_DEPLOYMENT=          # still empty\n"
        "B=nospace#kept\n"
        "C='quoted # not a comment'\n"
    )
    assert parse_env(text) == {
        "AZURE_OPENAI_EMBEDDING_DEPLOYMENT": "text-embedding-3-large",
        "AZURE_OPENAI_CHAT_DEPLOYMENT": "",
        "B": "nospace#kept",
        "C": "quoted # not a comment",
    }


def test_read_env_file_tolerates_bom(tmp_path):
    path = _env_file(tmp_path, "\ufeffA=1\n")
    assert read_env_file(path) == {"A": "1"}


# --- load_azure_config ------------------------------------------------------


def test_load_ok(tmp_path):
    cfg = load_azure_config(_env_file(tmp_path, _env_text()))
    assert isinstance(cfg, AzureConfig)
    assert cfg.search_endpoint == FAKE["AZURE_SEARCH_ENDPOINT"]
    assert cfg.search_query_key == FAKE["AZURE_SEARCH_QUERY_KEY"]
    assert cfg.speech_region == "centralus"
    assert cfg.speech_endpoint == FAKE["AZURE_SPEECH_ENDPOINT"]


def test_load_query_key_optional(tmp_path):
    text = _env_text().replace(
        f"AZURE_SEARCH_QUERY_KEY={FAKE['AZURE_SEARCH_QUERY_KEY']}\n", ""
    )
    cfg = load_azure_config(_env_file(tmp_path, text))
    assert cfg.search_query_key is None


def test_load_missing_file_names_path(tmp_path):
    with pytest.raises(AzureConfigError, match=r"\.env not found"):
        load_azure_config(tmp_path / ".env")


def test_load_missing_variable_is_named(tmp_path):
    with pytest.raises(AzureConfigError, match="AZURE_SPEECH_KEY"):
        load_azure_config(_env_file(tmp_path, _env_text(AZURE_SPEECH_KEY=None)))


def test_load_placeholder_rejected(tmp_path):
    text = _env_text(AZURE_OPENAI_ENDPOINT="https://<your-openai-name>.openai.azure.com")
    with pytest.raises(AzureConfigError, match="AZURE_OPENAI_ENDPOINT.*placeholder"):
        load_azure_config(_env_file(tmp_path, text))


def test_load_non_https_endpoint_rejected(tmp_path):
    with pytest.raises(AzureConfigError, match="https"):
        load_azure_config(
            _env_file(tmp_path, _env_text(AZURE_SEARCH_ENDPOINT="http://insecure.example"))
        )


def test_load_region_with_space_rejected(tmp_path):
    with pytest.raises(AzureConfigError, match="AZURE_SPEECH_REGION"):
        load_azure_config(_env_file(tmp_path, _env_text(AZURE_SPEECH_REGION="Central US")))


def test_repr_masks_all_keys(tmp_path):
    cfg = load_azure_config(_env_file(tmp_path, _env_text()))
    r = repr(cfg)
    assert "fake-admin-key" not in r
    assert "fake-api-key" not in r
    assert "fake-speech-key" not in r
    assert "fake-query-key" not in r
    assert cfg.search_endpoint in r  # endpoints/deployments stay visible for logs


def test_repo_root_holds_the_pipeline_regardless_of_cwd(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    root = repo_root()
    assert (root / "pipeline" / "pyproject.toml").is_file()
    assert root.joinpath(".git").exists()
