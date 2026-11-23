@echo off
setlocal
cd /d "%~dp0"
title FFXIII-2 Save Editor - build BLUS30776

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo  Le SDK .NET 8 n'est pas installe.
  echo  Installe-le depuis : https://dotnet.microsoft.com/download/dotnet/8.0
  echo  ^(colonne "SDK", Windows x64^), puis relance ce fichier.
  echo.
  start "" "https://dotnet.microsoft.com/download/dotnet/8.0"
  pause
  exit /b 1
)

echo Compilation en cours...
dotnet publish FFXIII2SaveEditor\FFXIII2SaveEditor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -o "%~dp0build-BLUS30776"
if errorlevel 1 (
  echo.
  echo  La compilation a echoue. Copie le texte ci-dessus et envoie-le.
  pause
  exit /b 1
)

echo.
echo  OK. L'editeur modifie est dans : %~dp0build-BLUS30776
echo  Lance FFXIII2SaveEditor.exe depuis ce dossier.
explorer "%~dp0build-BLUS30776"
pause
