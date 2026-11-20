@echo off
REM Open Island (Windows 11) — Claude Code managed statusLine bridge
REM Caches rate_limits JSON from stdin into %TEMP%\open-island-rl.json for the Usage dashboard.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$raw = [Console]::In.ReadToEnd(); if ($raw) { Set-Content -Path (Join-Path $env:TEMP 'open-island-rl.json') -Value $raw -Encoding UTF8 }"
