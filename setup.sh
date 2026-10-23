#!/usr/bin/env bash
# Creates the solution file and adds all projects. Run once after cloning.
# .NET 10 creates a .slnx file by default; older SDKs create .sln.
set -euo pipefail
rm -f VeilGate.sln VeilGate.slnx
dotnet new sln -n VeilGate
SLN=$(ls VeilGate.slnx 2>/dev/null || ls VeilGate.sln)
dotnet sln "$SLN" add src/*/*.csproj tests/*/*.csproj
echo "Solution ready: $SLN. Next: cp .env.example .env, then see README.md"
