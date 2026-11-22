@echo off
rem ================================================================
rem  Publication autonome de LloydScan : le runtime .NET 10 est inclus,
rem  rien a installer sur les postes (hors pilote du scanner).
rem
rem    publier.cmd        version finale 32 bits (recommandee) : a copier
rem                       dans C:\Program Files\LloydScan par la DSI
rem    publier.cmd x64    version finale 64 bits (pilotes TWAIN 64 bits)
rem    publier.cmd test   version de TEST portable : se lance depuis
rem                       n importe quel dossier, sans droits administrateur
rem ================================================================
set ARCH=%1
set CONF=Release
set OUT=
if "%ARCH%"=="" set ARCH=x86
if /I "%ARCH%"=="test" (
  set ARCH=x86
  set CONF=Debug
  set OUT=publish\test-x86
)
if "%OUT%"=="" set OUT=publish\%ARCH%
dotnet publish src\LloydScan.App\LloydScan.App.csproj -c %CONF% -r win-%ARCH% --self-contained true -o %OUT%
if errorlevel 1 (
  echo ECHEC de la publication.
  exit /b 1
)
echo.
echo Application publiee dans %OUT%
if "%CONF%"=="Debug" (echo Version de test : copier le dossier ou l on veut et lancer LloydScan.exe) else (echo A copier dans C:\Program Files\LloydScan - compte administrateur)
