#!/usr/bin/env bash
# Moi truong .NET cho project nay.
#
#   source ./env.sh
#
# .NET 9 SDK duoc cai tren o ngoai (/Volumes/240GB/dotnet) de khong chiem cho o chinh,
# cache NuGet cung nam tren o ngoai. Go cai dat = xoa 2 folder do, khong de lai gi.
#
# Muon khoi phai source moi lan, tu them 4 dong export duoi day vao ~/.zshrc.

export DOTNET_ROOT="/Volumes/240GB/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export NUGET_PACKAGES="/Volumes/240GB/dotnet-nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "dotnet $(dotnet --version)  |  DOTNET_ROOT=$DOTNET_ROOT  |  NUGET_PACKAGES=$NUGET_PACKAGES"
