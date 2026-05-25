@echo off
cd /d "D:\BookReaderTranslationServer\TranslationServer"
docker compose up -d --build
echo.
echo Proxy: http://localhost:8080
echo On phone use: http://YOUR_PC_LAN_IP:8080
pause
