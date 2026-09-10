#!/bin/zsh
set -eu
PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$PROJECT_ROOT"
exec /usr/local/share/dotnet/dotnet src/WeeklyReports/bin/Release/net10.0/WeeklyReports.dll run --root "$PROJECT_ROOT"
