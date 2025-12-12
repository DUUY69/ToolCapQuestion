@echo off
setlocal
cd /d "%~dp0"

echo ========================================
echo Building CaptureRegionApp...
echo ========================================
cd CaptureRegionApp
dotnet build
if %ERRORLEVEL% NEQ 0 (
    echo Build failed!
    pause
    exit /b 1
)

echo.
echo ========================================
echo Running CaptureRegionApp...
echo ========================================
dotnet run

endlocal

