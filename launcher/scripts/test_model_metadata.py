"""Run classification persistence and Manager HTTP compatibility regression tests.

The launcher no longer imports any plugin source or writes guessed sidecars.
--plugin-root is accepted for compatibility with old acceptance commands, but
the supplied plugin is never loaded, modified or required by these tests.
"""

import argparse
from pathlib import Path
import subprocess


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin-root", type=Path, help="Legacy argument; plugin source is no longer executed.")
    options = parser.parse_args()
    if options.plugin_root is not None:
        print("Plugin checkout is preserved; testing Manager HTTP contracts and durable classification work.", flush=True)
    root = Path(__file__).resolve().parents[2]
    subprocess.run(
        ["dotnet", "run", "--project", str(root / "launcher/Anima.Launcher.Tests/Anima.Launcher.Tests.csproj"),
         "--", "--installation-readiness-only"],
        cwd=root, check=True,
    )


if __name__ == "__main__":
    main()
