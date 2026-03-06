param(
    [string]$SqlitePath = "bin/Debug/net9.0/data/bot.db",
    [string]$SqlServerConnectionString,
    [string]$Server,
    [string]$Database,
    [string]$User,
    [string]$Password
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

if (-not [string]::IsNullOrWhiteSpace($SqlServerConnectionString)) {
    dotnet run --project tools/ConfigMigrator/ConfigMigrator.csproj -- --sqlite "$SqlitePath" --sqlserver "$SqlServerConnectionString"
    exit $LASTEXITCODE
}

if ([string]::IsNullOrWhiteSpace($Server) -or
    [string]::IsNullOrWhiteSpace($Database) -or
    [string]::IsNullOrWhiteSpace($User) -or
    [string]::IsNullOrWhiteSpace($Password)) {
    throw "Provide either -SqlServerConnectionString OR (-Server -Database -User -Password)."
}

dotnet run --project tools/ConfigMigrator/ConfigMigrator.csproj -- --sqlite "$SqlitePath" --server "$Server" --database "$Database" --user "$User" --password "$Password"
