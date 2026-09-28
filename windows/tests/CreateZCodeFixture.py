"""Create synthetic ZCode usage in an explicitly supplied isolated fixture home."""
import argparse
from datetime import datetime, timedelta, timezone
from pathlib import Path
import sqlite3

parser = argparse.ArgumentParser()
parser.add_argument("home", type=Path)
args = parser.parse_args()
home = args.home.resolve()
if home == Path.home().resolve():
    raise SystemExit("Refusing to write fixtures into the real user home")
path = home / ".zcode" / "cli" / "db" / "db.sqlite"
path.parent.mkdir(parents=True, exist_ok=True)
if path.exists():
    raise SystemExit("Fixture database already exists")
with sqlite3.connect(path) as connection:
    connection.execute("""CREATE TABLE model_usage (
        id TEXT PRIMARY KEY, model_id TEXT, status TEXT, started_at INTEGER, completed_at INTEGER,
        input_tokens INTEGER, output_tokens INTEGER, reasoning_tokens INTEGER,
        cache_read_input_tokens INTEGER, cache_creation_input_tokens INTEGER)""")
    now = datetime.now(timezone.utc)
    for day in range(3):
        for call in range(3):
            at = int((now - timedelta(days=day, minutes=10 + call)).timestamp() * 1000)
            connection.execute("INSERT INTO model_usage VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                               (f"zcode-fixture-{day}-{call}", "zcode-demo-model", "completed",
                                at, at, 88000, 1600, 700, 82000, 0))
print(path)
