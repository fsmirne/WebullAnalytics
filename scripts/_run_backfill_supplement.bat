@echo off
chcp 65001 >nul
set PYTHONUTF8=1
python.exe "%~dp0backfill_thetadata.py" %*
