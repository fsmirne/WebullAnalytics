@echo off
chcp 65001 >nul
set PYTHONUTF8=1
python.exe "C:\dev\WebullAnalytics\scripts\backfill_thetadata.py" %*
