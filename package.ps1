Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Đang đóng gói Dynamic Island for Windows" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

$root = $PSScriptRoot
$publishDir = Join-Path $root "publish"
$zipPath = Join-Path $root "DynamicIsland-win-x64-standalone.zip"

# Xóa thư mục publish cũ nếu có
if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}
if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}

Write-Host "`n[1/3] Biên dịch Self-Contained Single-File (Tự chứa .NET 8, không cần cài gì thêm)..." -ForegroundColor Yellow
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[LỖI] Đóng gói thất bại!" -ForegroundColor Red
    exit 1
}

Write-Host "`n[2/3] Nén file zip để gửi cho bạn bè..." -ForegroundColor Yellow
$exePath = Join-Path $publishDir "DynamicIsland.exe"
Compress-Archive -Path $exePath -DestinationPath $zipPath -Force

$zipInfo = Get-Item $zipPath
$sizeMB = [Math]::Round($zipInfo.Length / 1MB, 2)

Write-Host "`n[3/3] Hoàn tất thành công!" -ForegroundColor Green
Write-Host "--------------------------------------------------"
Write-Host "File đã đóng gói sẵn sàng:" -ForegroundColor White
Write-Host " - Đường dẫn: $zipPath" -ForegroundColor Green
Write-Host " - Dung lượng: $sizeMB MB" -ForegroundColor Green
Write-Host "--------------------------------------------------"
Write-Host "Bạn chỉ cần gửi file .zip này cho bạn bè hoặc upload lên GitHub Releases." -ForegroundColor Cyan
Write-Host "Người nhận chỉ cần giải nén và nhấp đúp vào DynamicIsland.exe để sử dụng ngay!`n" -ForegroundColor Cyan
