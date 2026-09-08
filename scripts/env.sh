# Execute: source scripts/env.sh
export DOTNET_ROOT="$PWD/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME="$PWD/.dotnet/cli-home"
export NUGET_PACKAGES="$PWD/.dotnet/packages"
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Trading='Host=localhost;Port=55432;Database=etf;Username=etf;Password=etf_demo_only'
