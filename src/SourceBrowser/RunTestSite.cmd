@echo off
rem Serves the site GenerateTestSite.cmd wrote to src\SourceIndexServer\TestSite (also the
rem location the HtmlGenerator F5 launch profile targets) by pointing SourceIndexServer's
rem --contentRoot directly at it. Must be run from the repo root. --contentRoot is resolved
rem against the generic host's base directory (the dll's own folder), not the caller's CWD,
rem so this passes an absolute path built from this script's own location (%~dp0).
dotnet src\SourceIndexServer\bin\Debug\net10.0\Microsoft.SourceBrowser.SourceIndexServer.dll --contentRoot "%~dp0src\SourceIndexServer\TestSite"