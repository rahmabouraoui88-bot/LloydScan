# Compilation de LloydScan dans le cloud GitHub (aucune installation sur les postes LLOYD).
# Le code est déposé dans le dépôt sous forme d'un seul fichier : LloydScan_GitHub.zip (moins de 25 Mo).
# Les modèles OCR français et arabe (trop lourds pour le zip) sont téléchargés ici, depuis le dépôt officiel Tesseract.
name: Compilation LloydScan

on:
  push:
  workflow_dispatch:

jobs:
  build:
    runs-on: windows-latest
    steps:
      - name: Recuperer le depot
        uses: actions/checkout@v4

      - name: Decompresser le code LloydScan
        shell: pwsh
        run: Expand-Archive -Path LloydScan_GitHub.zip -DestinationPath build -Force

      - name: Telecharger les modeles OCR (francais, arabe)
        shell: pwsh
        working-directory: build/LloydScan/tools/tesseract/tessdata
        run: |
          foreach ($l in 'fra','ara') {
            Invoke-WebRequest -Uri "https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/$l.traineddata" -OutFile "$l.traineddata"
          }
          Get-ChildItem *.traineddata | Format-Table Name, Length

      - name: Installer .NET 10
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Compiler version TEST (portable, sans droits administrateur)
        working-directory: build/LloydScan
        run: dotnet publish src/LloydScan.App/LloydScan.App.csproj -c Debug -r win-x86 --self-contained true -o ../../publish/test-x86

      - name: Compiler version PRODUCTION (a deployer par la DSI dans C:\Program Files\LloydScan)
        working-directory: build/LloydScan
        run: dotnet publish src/LloydScan.App/LloydScan.App.csproj -c Release -r win-x86 --self-contained true -o ../../publish/x86

      - name: Publier version TEST
        uses: actions/upload-artifact@v4
        with:
          name: LloydScan-TEST-portable-x86
          path: publish/test-x86

      - name: Publier version PRODUCTION
        uses: actions/upload-artifact@v4
        with:
          name: LloydScan-PRODUCTION-x86
          path: publish/x86
