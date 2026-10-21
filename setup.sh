#!/usr/bin/env bash
# Creates the solution file and adds all projects. Run once after cloning.
set -euo pipefail
dotnet new sln -n VeilGate --force
dotnet sln VeilGate.sln add src/*/*.csproj tests/*/*.csproj
echo "Solution ready. Next: cp .env.example .env, then see README.md"
