"""
Reads presidio_config.json and writes a .env.presidio file with .NET-compatible
environment variables (double underscore = nesting, index = array position,
key = dictionary key). This is the official ASP.NET Core configuration format for
environment variables - see
https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/#environment-variables

It works REGARDLESS of what appsettings.json inside the PrivacyProxy image looks
like, because in ASP.NET Core's configuration priority environment variables ALWAYS
win over JSON files.

LIMITATION: empty arrays/dictionaries (e.g. "AllowList": []) produce NO env
variables, because .NET cannot express "explicitly empty" via env vars. If the
image already had a non-empty default for AllowList/Context, that default would
remain. For PrivacyProxy.Api the code default for both is "[]" anyway (see
PresidioOptions.cs), so this is harmless here.

Usage: python3 generate_presidio_env.py
"""
import json
from pathlib import Path

CONFIG_PATH = Path("presidio_config.json")
OUT_PATH    = Path(".env.presidio")


def flatten(prefix: str, value, out: dict[str, str]) -> None:
    if isinstance(value, dict):
        for key, sub_value in value.items():
            flatten(f"{prefix}__{key}", sub_value, out)
    elif isinstance(value, list):
        for i, sub_value in enumerate(value):
            flatten(f"{prefix}__{i}", sub_value, out)
    else:
        out[prefix] = str(value)


def main() -> None:
    config = json.loads(CONFIG_PATH.read_text())

    flat: dict[str, str] = {}
    flatten("Presidio", config["Presidio"], flat)

    lines = [f"{key}={value}" for key, value in sorted(flat.items())]
    OUT_PATH.write_text("\n".join(lines) + "\n")
    print(f"{OUT_PATH} written ({len(flat)} variables).")


if __name__ == "__main__":
    main()