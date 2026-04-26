# 把 EtlTool 部署成既有 IIS 站台底下的子應用程式（共用 port 443）
# 用法（以系統管理員身分執行）:
#   .\install-iis-subapp.ps1 `
#     -PublishPath "C:\Apps\EtlTool" `
#     -ParentSite "Default Web Site" `
#     -VirtualPath "etltool" `
#     -DataPath "C:\ProgramData\EtlTool"
#
# 完成後可由父站台的 https 入口存取：https://your-host/etltool/

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PublishPath,
    [string]$ParentSite = "Default Web Site",
    [string]$VirtualPath = "etltool",
    [string]$DataPath = "C:\ProgramData\EtlTool",
    [string]$AppPoolName = "EtlToolPool",
    [string]$AppPoolIdentity = "ApplicationPoolIdentity"   # 或 "DOMAIN\svc-etltool"
)

$ErrorActionPreference = "Stop"
Import-Module WebAdministration

if (-not (Test-Path "$PublishPath\EtlTool.App.dll")) {
    Write-Error "找不到 $PublishPath\EtlTool.App.dll，請先 dotnet publish"
    exit 1
}

# 0) 必備：ASP.NET Core Hosting Bundle (含 ANCM v2)
#    https://dotnet.microsoft.com/download/dotnet/10.0 → ASP.NET Core Runtime 10.0.x → Hosting Bundle
Write-Host "==> 確認 ANCM v2 已安裝（請先安裝 .NET 10 Hosting Bundle）"

# 1) 建立資料目錄並設權限
Write-Host "==> 建立並授權資料目錄: $DataPath"
New-Item -ItemType Directory -Path "$DataPath\keys" -Force | Out-Null
New-Item -ItemType Directory -Path "$DataPath\logs" -Force | Out-Null
$identityToGrant = if ($AppPoolIdentity -eq "ApplicationPoolIdentity") {
    "IIS AppPool\$AppPoolName"
} else { $AppPoolIdentity }
icacls "$DataPath" /grant "${identityToGrant}:(OI)(CI)F" /T | Out-Null

# 2) Application Pool — AlwaysRunning + 不 idle (Quartz 排程必要)
if (Test-Path "IIS:\AppPools\$AppPoolName") {
    Write-Host "==> AppPool $AppPoolName 已存在"
} else {
    Write-Host "==> 建立 AppPool $AppPoolName"
    New-WebAppPool -Name $AppPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "managedRuntimeVersion" -Value ""           # No managed code (純 ASP.NET Core)
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "startMode" -Value "AlwaysRunning"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "processModel.idleTimeout" -Value "00:00:00"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "recycling.periodicRestart.time" -Value "00:00:00"
if ($AppPoolIdentity -ne "ApplicationPoolIdentity") {
    # 用網域帳戶
    $cred = Get-Credential -Message "請輸入 $AppPoolIdentity 的密碼" -UserName $AppPoolIdentity
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "processModel.identityType" -Value "SpecificUser"
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "processModel.userName" -Value $cred.UserName
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "processModel.password" -Value $cred.GetNetworkCredential().Password
}

# 3) 建立 Web Application（子應用程式）
$appPath = "IIS:\Sites\$ParentSite\$VirtualPath"
if (Test-Path $appPath) {
    Write-Host "==> Web Application 已存在，更新指向"
    Set-ItemProperty $appPath -Name "physicalPath" -Value $PublishPath
    Set-ItemProperty $appPath -Name "applicationPool" -Value $AppPoolName
} else {
    Write-Host "==> 建立 Web Application '$VirtualPath' 於 '$ParentSite'"
    New-WebApplication -Name $VirtualPath -Site $ParentSite -PhysicalPath $PublishPath -ApplicationPool $AppPoolName | Out-Null
}

# 4) 寫/合併 web.config 的環境變數，讓 app 知道自己在 /VirtualPath 下
$webConfigPath = Join-Path $PublishPath "web.config"
if (-not (Test-Path $webConfigPath)) {
    Write-Warning "$webConfigPath 不存在 — dotnet publish 應該會產生此檔。請改用 deploy/iis/web.config 為範本。"
} else {
    Write-Host "==> 寫入 ETLTOOL_PATH_BASE=/$VirtualPath 與 ETLTOOL_DATA_DIR 至 web.config"
    [xml]$cfg = Get-Content $webConfigPath
    $aspNetCore = $cfg.SelectSingleNode("//aspNetCore")
    if ($aspNetCore -ne $null) {
        $envVars = $aspNetCore.SelectSingleNode("environmentVariables")
        if ($envVars -eq $null) {
            $envVars = $cfg.CreateElement("environmentVariables")
            $aspNetCore.AppendChild($envVars) | Out-Null
        }

        function Set-EnvVar($name, $value) {
            $node = $envVars.SelectSingleNode("environmentVariable[@name='$name']")
            if ($node -eq $null) {
                $node = $cfg.CreateElement("environmentVariable")
                $node.SetAttribute("name", $name)
                $envVars.AppendChild($node) | Out-Null
            }
            $node.SetAttribute("value", $value)
        }
        Set-EnvVar "ASPNETCORE_ENVIRONMENT" "Production"
        Set-EnvVar "ETLTOOL_PATH_BASE" "/$VirtualPath"
        Set-EnvVar "ETLTOOL_DATA_DIR" $DataPath

        $cfg.Save($webConfigPath)
    }
}

# 5) 啟動 / 預熱
Write-Host "==> 啟動 AppPool 與 Application"
Start-WebAppPool -Name $AppPoolName
# 觸發初次預熱（會被 applicationInitialization + healthz 接住）
try {
    $url = "http://localhost/$VirtualPath/healthz"
    Write-Host "==> 預熱: $url"
    Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30 | Out-Null
    Write-Host "    ✓ Healthy" -ForegroundColor Green
} catch {
    Write-Warning "  預熱呼叫失敗（這不一定是錯誤，可能是父站台只綁 https）。請手動瀏覽 https://<parent>/$VirtualPath/healthz"
}

Write-Host ""
Write-Host "完成！由父站台訪問："
Write-Host "  https://<your-host>/$VirtualPath/"
Write-Host ""
Write-Host "管理:"
Write-Host "  Restart-WebAppPool $AppPoolName    # 重啟"
Write-Host "  Remove-WebApplication -Name $VirtualPath -Site '$ParentSite'   # 解除安裝"
