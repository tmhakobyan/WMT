@echo off
rem Developer use only (needs .NET 8 SDK). Normal users get results from GitHub Actions.
dotnet test "%~dp0..\tests\WMT.Tests" -c Release
pause
