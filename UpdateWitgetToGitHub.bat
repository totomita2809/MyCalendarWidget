@echo off
chcp 65001 >nul

:START
cls
echo ==========================================
echo   QUY TRÌNH PHÁT HÀNH TỰ ĐỘNG THÔNG MINH
echo ==========================================

:: ------------------------------------------
:: BƯỚC 1: KIỂM TRA ĐĂNG NHẬP GITHUB CLI
:: ------------------------------------------
echo [1/5] Đang kiểm tra trạng thái đăng nhập GitHub...
gh auth status >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo [CẢNH BÁO] Bạn chưa đăng nhập tài khoản GitHub!
    echo Đang chuyển hướng sang trang đăng nhập...
    gh auth login
    if %errorlevel% neq 0 (
        echo [LỖI] Đăng nhập thất bại.
        goto RETRY_OR_EXIT
    )
)
echo [THÀNH CÔNG] Đã xác thực tài khoản GitHub.

:: ------------------------------------------
:: BƯỚC 2: NHẬP VÀ KIỂM TRA TỆP TIN CỤ THỂ
:: ------------------------------------------
echo.
echo [2/5] Kiểm tra thư mục chứa file cài đặt...
cd /d "E:\New folder\My Desktop Widget\MyCalendarWidgetSetup\Release"

set /p VER=Nhập số phiên bản cần phát hành (VD: 111): 
if "%VER%"=="" (
    echo [LỖI] Bạn chưa nhập số phiên bản!
    goto RETRY_OR_EXIT
)

set MSI_FILE=MyCalendarWidgetSetup%VER%.msi
set JSON_FILE=MyCalendarWidgetSetup%VER%.json

if not exist "%MSI_FILE%" (
    echo.
    echo ==========================================
    echo [LỖI KHÔNG ĐỦ ĐIỀU KIỆN] Thiếu file cài đặt!
    echo Không tìm thấy: %MSI_FILE%
    echo Vui lòng kiểm tra lại thư mục Release.
    echo ==========================================
    goto RETRY_OR_EXIT
)

if not exist "%JSON_FILE%" (
    echo.
    echo ==========================================
    echo [LỖI KHÔNG ĐỦ ĐIỀU KIỆN] Thiếu file JSON mô tả!
    echo Không tìm thấy: %JSON_FILE%
    echo Vui lòng tạo file JSON mô tả cập nhật trước.
    echo ==========================================
    goto RETRY_OR_EXIT
)

echo [THÀNH CÔNG] Đã tìm thấy đủ cặp file: %MSI_FILE% và %JSON_FILE%.

:: ------------------------------------------
:: BƯỚC 3: QUÉT TRÊN GITHUB XEM PHIÊN BẢN ĐÃ TỒN TẠI CHƯA
:: ------------------------------------------
echo.
echo [3/5] Quét trạng thái phiên bản v%VER% trên GitHub Releases...
gh release view "%VER%" >nul 2>&1
if %errorlevel% == 0 (
    echo.
    echo ==========================================
    echo [DỪNG TIẾN TRÌNH] Phiên bản v%VER% đã tồn tại trên GitHub!
    echo Không thể tải lên đè lên phiên bản cũ.
    echo ==========================================
    goto RETRY_OR_EXIT
) else (
    echo [THÀNH CÔNG] Phiên bản v%VER% chưa có trên GitHub. Có thể tiến hành đẩy lên.
)

:: ------------------------------------------
:: BƯỚC 4: COPY VÀ ĐỔI TÊN THÀNH UPDATE.JSON
:: ------------------------------------------
echo.
echo [4/5] Đang tạo và đổi tên file update.json vào thư mục gốc...
cd /d "E:\New folder\My Desktop Widget"
copy /y "MyCalendarWidgetSetup\Release\%JSON_FILE%" "update.json" >nul

:: ------------------------------------------
:: BƯỚC 5: ĐẨY UPDATE.JSON, MÃ NGUỒN VÀ TẠO RELEASE
:: ------------------------------------------
echo.
echo [5/5] Đang đẩy mã nguồn, update.json và tạo Release lên GitHub...

git add update.json
git add "MyCalendarWidget/*.cs"
git add "MyCalendarWidget/*.xaml"
git add "*.slnx"
git add "*.bat"

set /p msg=Nhập nội dung ghi chú phiên bản v%VER%: 
if "%msg%"=="" set msg=Release version %VER%

git commit -m "%msg%"
if %errorlevel% neq 0 goto GIT_ERROR

git push origin main
if %errorlevel% neq 0 goto GIT_ERROR

echo Đang tạo Release v%VER% và tải tệp lên GitHub tự động...
cd /d "E:\New folder\My Desktop Widget\MyCalendarWidgetSetup\Release"

gh release create "%VER%" "%MSI_FILE%" "%JSON_FILE%" --title "Version %VER%" --notes "%msg%"
if %errorlevel% neq 0 goto GIT_ERROR

echo.
echo ==========================================
echo HOÀN TẤT 100%! 
echo - Code và update.json đã lên nhánh main.
echo - File %MSI_FILE% và %JSON_FILE% đã lên Releases!
echo ==========================================
goto END

:GIT_ERROR
echo.
echo ==========================================
echo [LỖI HỆ THỐNG / GIT / GITHUB CLI]
echo Tiến trình gặp sự cố.
echo ==========================================

:RETRY_OR_EXIT
echo.
echo ------------------------------------------
echo Bạn muốn làm gì tiếp theo?
echo [1] Quay lại từ đầu (Nhập lại phiên bản)
echo [2] Thoát chương trình
echo ------------------------------------------
set /p choice=Nhập lựa chọn của bạn (1 hoặc 2): 
if "%choice%"=="1" goto START
if "%choice%"=="2" goto END

goto START

:END
echo.
echo Đã thoát chương trình. Tạm biệt!
timeout /t 3 >nul
exit