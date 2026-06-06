@echo off
REM Launch tModLoader with .NET tiered compilation DISABLED.
REM Methods then JIT once at the optimized tier and never relocate, so a single
REM one-shot trainer patch sticks forever (no re-patching / no snapshot polling).
REM Steam must be running. Close any running tModLoader first.

set DOTNET_TieredCompilation=0
set DOTNET_TieredPGO=0
set DOTNET_TC_QuickJitForLoops=0

cd /d "C:\Program Files (x86)\Steam\steamapps\common\tModLoader"
echo Launching tModLoader with TieredCompilation=0 ...
start "" "dotnet\dotnet.exe" tModLoader.dll
