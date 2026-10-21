@echo off
rem QuotaChecker portable launcher - keep this file ASCII-only (CMD uses the OEM codepage).
rem App files live in the "app" subfolder; this launcher keeps the package root tidy.
start "" "%~dp0app\QuotaChecker.exe"
exit /b 0
