@echo off
chcp 65001 >nul
echo ==========================================
echo   TU DONG CAP NHAT PHIEN BAN MOI
echo ==========================================

git add .
set /p msg=Nhập nội dung ghi chú (ví dụ: Sua loi v109): 
if "%msg%"=="" set msg=Update code

git commit -m "%msg%"
git push origin main

echo.
echo ------------------------------------------
echo [THÔNG BÁO] Code đã đẩy lên GitHub thành công!
echo Đường dẫn chứa file MSI của bạn hiện tại là:
echo E:\New folder\My Desktop Widget\MyCalendarWidgetSetup\Release
echo ------------------------------------------
echo.
pause