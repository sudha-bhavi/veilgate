# Windows version of setup.sh
Remove-Item VeilGate.sln, VeilGate.slnx -ErrorAction SilentlyContinue
dotnet new sln -n VeilGate
$sln = if (Test-Path VeilGate.slnx) { "VeilGate.slnx" } else { "VeilGate.sln" }
Get-ChildItem -Recurse -Filter *.csproj src,tests | ForEach-Object { dotnet sln $sln add $_.FullName }
Write-Host "Solution ready: $sln. Next: copy .env.example to .env, then see README.md"
