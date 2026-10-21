# Windows version of setup.sh
dotnet new sln -n VeilGate --force
Get-ChildItem -Recurse -Filter *.csproj src,tests | ForEach-Object { dotnet sln VeilGate.sln add $_.FullName }
Write-Host "Solution ready. Next: copy .env.example to .env, then see README.md"
