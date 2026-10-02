$ErrorActionPreference = "Stop"
dotnet restore "$PSScriptRoot\..\desktop\DroneDash_x64.Desktop\DroneDash_x64.Desktop.csproj"
dotnet build "$PSScriptRoot\..\desktop\DroneDash_x64.Desktop\DroneDash_x64.Desktop.csproj" -c Release
dotnet build "$PSScriptRoot\..\desktop\DroneDash_x64.MockRc\DroneDash_x64.MockRc.csproj" -c Release
