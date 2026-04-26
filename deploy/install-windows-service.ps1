# EtlTool Windows Service 安裝腳本
# 用法 (以系統管理員身分執行 PowerShell):
#   .\install-windows-service.ps1 -InstallPath "C:\Apps\EtlTool" -DataPath "C:\ProgramData\EtlTool"
#
# 流程:
#   1) 建立資料目錄 + 設定 ACL
#   2) 註冊服務並設定環境變數
#   3) 設定失敗自動重啟
#   4) 啟動服務

[CmdletBinding()]
param(
    [string]$InstallPath = "C:\Apps\EtlTool",
    [string]$DataPath    = "C:\ProgramData\EtlTool",
    [string]$ServiceName = "EtlTool",
    [string]$DisplayName = "ETL Tool",
    [string]$Description = "Oracle <-> MSSQL ETL Scheduler",
    [int]   $Port        = 5247,
    [string]$ServiceAccount = "NT AUTHORITY\NetworkService"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path "$InstallPath\EtlTool.App.exe")) {
    Write-Error "在 $InstallPath 找不到 EtlTool.App.exe，請先把 publish 內容複製過去"
    exit 1
}

Write-Host "==> 建立資料目錄: $DataPath"
New-Item -ItemType Directory -Path "$DataPath\keys" -Force | Out-Null
New-Item -ItemType Directory -Path "$DataPath\logs" -Force | Out-Null

Write-Host "==> 設定 ACL: 給 $ServiceAccount 完整控制權限"
icacls "$DataPath" /grant "${ServiceAccount}:(OI)(CI)F" | Out-Null

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "==> 服務已存在，先停止並刪除"
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "==> 註冊服務"
sc.exe create $ServiceName binPath= "$InstallPath\EtlTool.App.exe" start= auto obj= $ServiceAccount DisplayName= $DisplayName | Out-Null
sc.exe description $ServiceName $Description | Out-Null

Write-Host "==> 失敗自動重啟 (30s / 60s / 120s)"
sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/60000/restart/120000 | Out-Null

Write-Host "==> 設定機器層級環境變數"
[System.Environment]::SetEnvironmentVariable("ETLTOOL_DATA_DIR", $DataPath, "Machine")
[System.Environment]::SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:$Port", "Machine")
[System.Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Machine")

Write-Host "==> 啟動服務"
Start-Service $ServiceName

Start-Sleep -Seconds 3

Write-Host ""
Write-Host "==> 健康檢查: http://localhost:$Port/healthz"
try {
    $r = Invoke-WebRequest "http://localhost:$Port/healthz" -UseBasicParsing -TimeoutSec 5
    if ($r.StatusCode -eq 200) {
        Write-Host "    ✓ Healthy" -ForegroundColor Green
    } else {
        Write-Warning "    回應 $($r.StatusCode)，請檢查日誌"
    }
} catch {
    Write-Warning "    無法連線 — 請檢查 $DataPath\logs\ 的 log"
}

Write-Host ""
Write-Host "完成！"
Write-Host "  Web UI : http://localhost:$Port/"
Write-Host "  資料夾 : $DataPath"
Write-Host "  日誌   : $DataPath\logs\"
Write-Host ""
Write-Host "管理指令:"
Write-Host "  Get-Service $ServiceName"
Write-Host "  Restart-Service $ServiceName"
Write-Host "  Stop-Service $ServiceName ; sc.exe delete $ServiceName    # 移除"
