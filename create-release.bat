@echo off
setlocal
cd /d "%~dp0"

set project=W3ModWorkbench\W3ModWorkbench.csproj

::Read the version from the project file, e.g. 1.2.0
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "(Select-Xml -Path '%project%' -XPath '//Version').Node.InnerText"`) do set version=%%V
if "%version%"=="" (
	echo Could not read the version from %project%.
	exit /b 1
)
set releaseName=W3ModWorkbench-v%version%-win-x64
echo Building W3 Mod Workbench %version%...

::Delete 'release' folder and create a new one.
if exist release rmdir release /s /q
mkdir release

::Publish the self-contained single exe (settings live in the Release-win-x64 publish profile).
dotnet publish "%project%" -p:PublishProfile=Release-win-x64 -nologo -v q
if errorlevel 1 (
	echo.
	echo Build failed. If it says the exe is in use, close W3 Mod Workbench and try again.
	exit /b 1
)

::Pack the release: the exe, licence, readme and changelog.
if exist "%ProgramFiles%\7-Zip\7z.exe" (set zip="%ProgramFiles%\7-Zip\7z.exe") else (set zip=7z)
where %zip% >nul 2>nul
if errorlevel 1 (
	powershell -NoProfile -Command "Compress-Archive -Path 'release\W3ModWorkbench\*' -DestinationPath 'release\%releaseName%.zip' -Force"
) else (
	%zip% a "release\%releaseName%.zip" ".\release\W3ModWorkbench\*" >nul
)
if not exist "release\%releaseName%.zip" (
	echo Could not create the zip.
	exit /b 1
)

echo.
echo Done: release\%releaseName%.zip
if not "%1"=="--no-open" explorer release
