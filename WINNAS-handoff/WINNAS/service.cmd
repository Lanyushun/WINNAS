@echo off
rem WINNAS background service - no console, no browser, no prompt.
rem Registered as scheduled task "WINNAS Console" (trigger: at logon).

cd /d "%~dp0"

set PY=C:\Users\Shawn\AppData\Local\Programs\Python\Python310\pythonw.exe
if not exist "%PY%" set PY=C:\Users\Shawn\AppData\Local\Programs\Python\Python310\python.exe

rem Avoid a second instance if the port is already taken.
netstat -ano | findstr /r /c:":8081 .*LISTENING" >nul
if not errorlevel 1 exit /b 0

start "" "%PY%" "%~dp0app.py"
