@echo off
setlocal enabledelayedexpansion

rem This script does four things, mirroring Mac's build_app_13_plus.sh +
rem build_dmg_13_plus.sh combined into one:
rem   1. dotnet publish a self-contained build
rem   2. copy the zh-cn-to-tw-web frontend into a web\ subfolder
rem   3. rebuild zh-cn-to-tw-ocr-service with PyInstaller, copy it into an
rem      ocr-service\ subfolder
rem   4. call Inno Setup's ISCC.exe to produce a Setup.exe
rem
rem Every step genuinely rebuilds (never "only if missing") -- see
rem known-issue-check item 21: a "package for distribution" script must not
rem silently repackage stale output under a fresh-looking filename.
rem
rem Intentionally plain ASCII, no Chinese comments -- see build_app_exe.bat
rem at the meta-repo root for why (Windows batch files handle non-ASCII
rem source encoding unreliably; hit this directly while writing this file).

rem Using %%~f to fully-qualify/normalize each path -- an un-normalized
rem path with a literal "..\.." segment in it (which is what you get from
rem plain string concatenation like "%SCRIPT_DIR%..\..") caused `if exist`
rem to silently misbehave here (its own then-block never ran, no error
rem shown, the script just stopped) when hit directly while writing this
rem script. Resolving it up front avoids relying on cmd.exe's handling of
rem un-normalized paths anywhere later in the script.
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "WINDOWS_REPO=%%~fI"
for %%I in ("%WINDOWS_REPO%\..") do set "META_REPO=%%~fI"
set "WEB_REPO=%META_REPO%\zh-cn-to-tw-web"
set "OCR_REPO=%META_REPO%\zh-cn-to-tw-ocr-service"
set "STAGING=%SCRIPT_DIR%staging"
set "STAGING_APP=%STAGING%\app"

echo ==^> Cleaning old staging/dist (forcing a fresh build)
if exist "%STAGING%" rmdir /s /q "%STAGING%"
if exist "%SCRIPT_DIR%dist" rmdir /s /q "%SCRIPT_DIR%dist"

echo.
echo ==^> [1/4] dotnet publish (self-contained, win-x64, Release)
where dotnet >nul 2>nul
if errorlevel 1 (
    echo dotnet not found - install the .NET 8 SDK first
    exit /b 1
)
dotnet publish "%WINDOWS_REPO%\src\ZhCnToTw\ZhCnToTw.csproj" ^
    -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%STAGING_APP%"
if errorlevel 1 (
    echo dotnet publish failed
    exit /b 1
)

echo.
echo ==^> [2/4] Copying zh-cn-to-tw-web frontend into web\
if not exist "%WEB_REPO%\index.html" (
    echo Cannot find %WEB_REPO%\index.html - is zh-cn-to-tw-web checked out correctly?
    exit /b 1
)
mkdir "%STAGING_APP%\web" >nul 2>nul
xcopy "%WEB_REPO%\*" "%STAGING_APP%\web\" /e /i /y >nul
if errorlevel 1 (
    echo Copying zh-cn-to-tw-web failed
    exit /b 1
)

echo.
echo ==^> [3/4] Rebuilding zh-cn-to-tw-ocr-service with PyInstaller
set "OCR_VENV_PY=%OCR_REPO%\venv\Scripts\python.exe"
if exist "%OCR_VENV_PY%" goto ocr_venv_found
echo Cannot find %OCR_VENV_PY%
echo Set up the venv first per zh-cn-to-tw-ocr-service's README (Setup Guide, Windows steps)
exit /b 1
:ocr_venv_found
rem PyInstaller occasionally fails here with a FileNotFoundError while
rem trying to re-open the exe it just wrote (winutils.set_exe_build_timestamp
rem / the bootloader copy step) -- real-time antivirus scanning grabbing the
rem freshly-written exe in a race PyInstaller's own retry logic doesn't
rem cover (its allowlist only covers a few specific PermissionError-style
rem winerror codes, not the FileNotFoundError this produces; see
rem PyInstaller\building\api.py's _retry_operation). Confirmed transient by
rem hand: reran the exact same command a few times and it eventually
rem succeeded with zero code changes. So: retry the whole PyInstaller
rem invocation here, not just trust its internal retry. Using goto/labels
rem instead of a for-loop with nested if-blocks -- deeply nested parens
rem combined with pushd/popd inside a for-loop body is exactly the kind of
rem construct the classic (non-PowerShell) cmd.exe batch parser handles
rem unreliably; goto is more primitive but far more predictable.
set "OCR_ATTEMPT=0"

:ocr_build_attempt
set /a OCR_ATTEMPT+=1
echo Attempt %OCR_ATTEMPT%/3...
if exist "%OCR_REPO%\build" rmdir /s /q "%OCR_REPO%\build"
if exist "%OCR_REPO%\dist" rmdir /s /q "%OCR_REPO%\dist"
pushd "%OCR_REPO%"
"%OCR_VENV_PY%" -m PyInstaller "packaging\ocr_service.spec" --noconfirm
set "OCR_BUILD_RC=%errorlevel%"
popd
if "%OCR_BUILD_RC%"=="0" goto ocr_build_done
if %OCR_ATTEMPT% LSS 3 goto ocr_build_attempt

echo PyInstaller build of ocr-service failed after 3 attempts
echo This is usually antivirus real-time scanning locking the freshly-written exe --
echo try excluding %OCR_REPO% from real-time/behavior scanning and rerun.
exit /b 1

:ocr_build_done
mkdir "%STAGING_APP%\ocr-service" >nul 2>nul
xcopy "%OCR_REPO%\dist\zh-cn-to-tw-ocr-service\*" "%STAGING_APP%\ocr-service\" /e /i /y >nul
if errorlevel 1 (
    echo Copying the ocr-service build output failed
    exit /b 1
)

echo.
echo ==^> [4/4] Packaging with Inno Setup into Setup.exe
rem Sequential goto-based checks, not a for-loop over a parenthesized list
rem -- see the note above ocr_venv_found: multi-line parenthesized blocks
rem (for/if with a "( ... )" body) proved unreliable in this environment
rem (a block would silently abort the whole script, even when its own
rem condition should have been false and the block skipped entirely) while
rem single-line "if X goto label" never had that problem. Kept this
rem primitive-but-predictable style for the rest of the script too, even
rem though only this step actually needed the fix, since it is the one
rem place besides the OCR retry above with a genuinely multi-branch check.
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if defined ISCC goto iscc_found
echo Cannot find ISCC.exe - install Inno Setup 6 first (winget install --id JRSoftware.InnoSetup)
exit /b 1
:iscc_found

"%ISCC%" "%SCRIPT_DIR%installer.iss"
set "ISCC_RC=%errorlevel%"
if "%ISCC_RC%"=="0" goto iscc_done
echo Inno Setup packaging failed
exit /b 1
:iscc_done

echo.
echo ==^> Done. The installer is in %SCRIPT_DIR%dist\
