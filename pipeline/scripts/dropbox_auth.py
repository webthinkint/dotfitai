"""Authorize the Q&A fetch against Dropbox, once, and save its refresh token.

Dropbox shows the signed-in person an approval page with a one-time code; this
exchanges the code for a refresh token and appends it to the root ``.env`` as
``DROPBOX_REFRESH_TOKEN``, so the token is never printed or pasted anywhere.

The fetch only reads: a token granted any write scope is refused rather than
saved. Scopes are fixed at approval, so a change on the app's Permissions tab
needs this run again (delete the ``.env`` line first). Never echoes key material.

Usage (from pipeline/, in a terminal, signed in to the work Dropbox account):
    uv run scripts/dropbox_auth.py
"""

from __future__ import annotations

import base64
import json
import sys
import urllib.error
import urllib.parse
import urllib.request

from qa_pipeline.azure_config import ENV_FILENAME, read_env_file, repo_root

AUTHORIZE_URL = "https://www.dropbox.com/oauth2/authorize"
TOKEN_URL = "https://api.dropboxapi.com/oauth2/token"
REQUIRED_SCOPES = {"files.metadata.read", "files.content.read", "sharing.read"}
TOKEN_VAR = "DROPBOX_REFRESH_TOKEN"


def exchange(code: str, app_key: str, app_secret: str) -> dict:
    """Trade the one-time approval code for the token response."""
    body = urllib.parse.urlencode(
        {"code": code, "grant_type": "authorization_code"}).encode("ascii")
    basic = base64.b64encode(f"{app_key}:{app_secret}".encode()).decode("ascii")
    request = urllib.request.Request(
        TOKEN_URL, data=body, headers={"Authorization": f"Basic {basic}"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def main() -> int:
    env_path = repo_root() / ENV_FILENAME
    env = read_env_file(env_path)
    app_key, app_secret = env.get("DROPBOX_APP_KEY"), env.get("DROPBOX_APP_SECRET")
    if not app_key or not app_secret:
        print("FAIL: set DROPBOX_APP_KEY and DROPBOX_APP_SECRET in .env first",
              file=sys.stderr)
        return 1
    if env.get(TOKEN_VAR):
        print(f"FAIL: {TOKEN_VAR} is already set; delete its .env line to "
              "authorize again", file=sys.stderr)
        return 1

    query = urllib.parse.urlencode({"client_id": app_key, "response_type": "code",
                                    "token_access_type": "offline"})
    print(f"Open this, approve the app, and copy the code it shows:\n\n"
          f"  {AUTHORIZE_URL}?{query}\n")
    code = input("Code: ").strip()
    if not code:
        print("FAIL: no code entered", file=sys.stderr)
        return 1

    try:
        token = exchange(code, app_key, app_secret)
    except urllib.error.HTTPError as e:
        print(f"FAIL: Dropbox refused the code (HTTP {e.code}); codes work once "
              "and expire quickly, so approve again", file=sys.stderr)
        return 1
    except urllib.error.URLError as e:
        print(f"FAIL: could not reach Dropbox: {e.reason}", file=sys.stderr)
        return 1

    refresh_token = token.get("refresh_token")
    if not refresh_token:
        print("FAIL: Dropbox returned no refresh token", file=sys.stderr)
        return 1

    granted = set((token.get("scope") or "").split())
    if not granted:
        print("note: Dropbox did not list the granted scopes; the listing will "
              "show whether they are enough")
    else:
        writes = sorted(s for s in granted if s.endswith(".write"))
        missing = sorted(REQUIRED_SCOPES - granted)
        if writes or missing:
            print("FAIL: nothing saved. On the app's Permissions tab, "
                  + (f"untick {', '.join(writes)}" if writes else "")
                  + (" and " if writes and missing else "")
                  + (f"tick {', '.join(missing)}" if missing else "")
                  + ", submit, and run this again.", file=sys.stderr)
            return 1

    existing = env_path.read_text(encoding="utf-8-sig")
    separator = "" if existing.endswith("\n") else "\n"
    with env_path.open("a", encoding="utf-8", newline="\n") as f:
        f.write(f"{separator}{TOKEN_VAR}={refresh_token}\n")
    print(f"PASS: {TOKEN_VAR} saved to {ENV_FILENAME}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
