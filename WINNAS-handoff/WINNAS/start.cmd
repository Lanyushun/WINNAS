@echo off
rem WINNAS launcher - starts the console without a visible window
cd /d "%~dp0"

set PY=C:\Users\Shawn\AppData\Local\Programs\Python\Python310\pythonw.exe
if not exist "%PY%" set PY=C:\Users\Shawn\AppData\Local\Programs\Python\Python310\python.exe
if not exist "%PY%" (
  echo [ERROR] Python 3.10 not found.
  pause
  exit /b 1
)

for /f "tokens=2 delims=:," %%p in ('findstr /c:"\"port\"" config.json') do set PORT=%%p
set PORT=%PORT: =%

echo Starting WINNAS...
start "" "%PY%" "%~dp0app.py"

timeout /t 3 >nul
echo.
echo   URL   : http://localhost:%PORT%
echo   Config: %~dp0config.json
echo.
start "" "http://localhost:%PORT%"
