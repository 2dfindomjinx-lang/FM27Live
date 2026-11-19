"""Publish the reviewed local release via the user's existing Git credential manager.
No credential is printed, written to disk, put in a URL, or embedded in the application.
"""
import argparse
import json
from pathlib import Path
import subprocess
import urllib.error
import urllib.request

REPOSITORY = "2dfindomjinx-lang/FM27Live"

def main():
    p = argparse.ArgumentParser()
    p.add_argument("--version", required=True)
    p.add_argument("--release-dir", type=Path, required=True)
    p.add_argument("--notes", type=Path, required=True)
    p.add_argument("--create-repository", action="store_true")
    p.add_argument("--prepare-only", action="store_true")
    args = p.parse_args()
    release_dir = args.release_dir.resolve()
    required = ["FM27Live.exe", "update.json", "SHA256SUMS.txt", "LICENSE", "NOTICE-LIVE.txt"]
    for name in required:
        if not (release_dir / name).is_file():
            raise SystemExit("Missing release asset: " + name)
    env = __import__("os").environ.copy()
    env["GIT_TERMINAL_PROMPT"] = "0"
    env["GCM_INTERACTIVE"] = "never"
    result = subprocess.run(["git", "credential", "fill"], input="protocol=https\nhost=github.com\nusername=2dfindomjinx-lang\n\n", text=True, capture_output=True, env=env)
    credentials = dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)
    token = credentials.get("password")
    if not token:
        raise SystemExit("GitHub credential unavailable; sign in through Git Credential Manager first.")

    def api(path, data=None, method=None, binary=False):
        url = path if path.startswith("https://uploads.github.com/") else "https://api.github.com" + path
        body = data if binary else (json.dumps(data).encode() if data is not None else None)
        req = urllib.request.Request(url, body, method=method, headers={
            "Authorization": "Bearer " + token,
            "Accept": "application/vnd.github+json", "User-Agent": "FM27Live-Publisher",
            "Content-Type": "application/octet-stream" if binary else "application/json"})
        with urllib.request.urlopen(req, timeout=180) as response:
            return json.load(response)

    user = api("/user")
    if user["login"].lower() != REPOSITORY.split("/")[0].lower():
        raise SystemExit("Authenticated GitHub account does not match the publisher.")
    try:
        repo = api("/repos/" + REPOSITORY)
    except urllib.error.HTTPError as e:
        if e.code != 404 or not args.create_repository:
            raise SystemExit("Repository is unavailable; no changes made.")
        repo = api("/user/repos", {"name": "FM27Live", "description": "Windows FM27 real-time editor with overlay and signed automatic updates. GPL-3.0-or-later.", "private": False, "auto_init": False}, "POST")
    if repo["private"]:
        raise SystemExit("Automatic updates require public release downloads; this repository is private.")
    print("Repository ready:", repo["html_url"])
    if args.prepare_only:
        return
    tag = "v" + args.version
    # A release remains a draft until all required assets have uploaded successfully.
    release = api("/repos/" + REPOSITORY + "/releases", {"tag_name": tag, "target_commitish": "main", "name": "FM27 Live " + tag, "body": args.notes.read_text(encoding="utf-8"), "draft": True, "prerelease": False}, "POST")
    base = release["upload_url"].split("{")[0]
    for asset in required + ["README-LIVE.md"]:
        path = release_dir / asset
        uploaded = api(base + "?name=" + asset, path.read_bytes(), "POST", True)
        if uploaded["size"] != path.stat().st_size:
            raise SystemExit("Upload size mismatch; release remains a draft.")
        print("Uploaded:", asset)
    published = api("/repos/" + REPOSITORY + "/releases/" + str(release["id"]), {"draft": False, "make_latest": "true"}, "PATCH")
    print("Published:", published["html_url"])

if __name__ == "__main__":
    main()
