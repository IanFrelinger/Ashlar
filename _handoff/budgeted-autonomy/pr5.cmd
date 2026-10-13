@echo off
rem pr5.cmd: run one budgeted autonomy controller command against the PR 5 live-run ledger (the folder this
rem file is in). It shows the command's output, appends the output, the time and the exit code to
rem pr5-console.log beside it, and exits with the same code. Run it from the checkout root:
rem   C:\Users\icfre\Documents\Codex\2026-09-12\f-2\work\ashlar-record-correction
rem Example:  "%L%\pr5.cmd" --json tick --adapter codex --gh cli
setlocal
set "L=%~dp0"
if "%L:~-1%"=="\" set "L=%L:~0,-1%"
set "ASHLAR_CONTROLLER_STATE=%L%\state\controller.db"
set "ASHLAR_CONTROLLER_POLICY=%L%\policy.json"
set "PYTHONUTF8=1"
if not exist "%L%\out" mkdir "%L%\out"
set "T=%TIME: =0%"
set "OUT=%L%\out\%T::=%-%RANDOM%.txt"
python tools\autonomy-controller\controller.py --holder pr5-live %* > "%OUT%" 2>&1
set "RC=%ERRORLEVEL%"
type "%OUT%"
>>"%L%\pr5-console.log" echo ==== %DATE% %TIME% exit %RC% :: pr5 %*
type "%OUT%" >> "%L%\pr5-console.log"
echo [pr5: exit %RC%]
endlocal & exit /b %RC%
