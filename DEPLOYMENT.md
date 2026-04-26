# 部署文件

本文件涵蓋 ETL Tool 在實際環境中的部署、設定、備份、升級與排錯。

> **重要安全提醒**：目前版本**沒有內建身分驗證**。**禁止把 Web UI 暴露至公網**，請部署在內網信任區段，並配合反向代理（IP 白名單／VPN／LDAP 前置）。後續版本會內建登入。

---

## 0. 認證 (Authentication)

從 v0.2 起內建 cookie-based 登入，所有 UI 與 API（除 `/healthz`、`/Account/Login`、靜態資源）都需登入才可存取。

預設帳號為 `admin`、預設密碼為 `etladmin`，**僅供初次啟動**。生產環境必須立即在 `appsettings.Production.json` 設好 `Auth:PasswordHash`。

```jsonc
{
  "Auth": {
    "Username": "admin",
    "PasswordHash": "$2a$12$...." ,   // BCrypt hash
    "SessionHours": 8                  // cookie 有效時間
  }
}
```

產生 BCrypt 雜湊（從本機開發機執行一次）：

```bash
dotnet run --project src/EtlTool.App -- --hash-password
# 互動輸入密碼後印出雜湊；複製到 appsettings.Production.json
```

或用 Python / Node 等工具離線產生（cost factor 12）：

```bash
# Python
python -c "import bcrypt; print(bcrypt.hashpw(b'YourPassword', bcrypt.gensalt(12)).decode())"
```

完整版本（多人 + RBAC + 外部 SSO）為下版規劃。

---

## 1. 系統需求

| 項目 | 最低 | 建議 |
|---|---|---|
| OS | Windows Server 2019 / Ubuntu 22.04 / 任何支援 .NET 10 的 OS | Windows Server 2022 / Ubuntu 24.04 |
| 架構 | x64 | x64 或 arm64（self-contained 發佈時可選） |
| RAM | 256 MB | 1 GB+（大批次資料時） |
| 磁碟 | 200 MB（程式）+ 視 log/SQLite 成長 | 預留 5 GB |
| Runtime | .NET 10 Runtime / ASP.NET Core 10 Runtime | 或用 self-contained 發佈無需安裝 runtime |
| 網路 | 能連到來源/目標 DB 的 port (Oracle 1521、MSSQL 1433) | — |

驗證 runtime 是否安裝：
```bash
dotnet --list-runtimes | grep -E "Microsoft\.AspNetCore\.App 10"
```

---

## 2. 取得發佈檔

### 方案 A：Self-contained（推薦給沒裝 runtime 的目標機）

把 .NET runtime 包進來，目標機**不需要**先裝 .NET：

```bash
# Windows x64
dotnet publish src/EtlTool.App -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=false \
  -o publish/win-x64

# Linux x64
dotnet publish src/EtlTool.App -c Release -r linux-x64 \
  --self-contained true \
  -o publish/linux-x64

# Linux arm64 (如 Raspberry Pi、Apple Silicon container)
dotnet publish src/EtlTool.App -c Release -r linux-arm64 \
  --self-contained true \
  -o publish/linux-arm64
```

產物約 80–120 MB（含 runtime）。

### 方案 B：Framework-dependent

目標機需先裝 .NET 10 ASP.NET Core Runtime：

```bash
dotnet publish src/EtlTool.App -c Release -o publish/portable
```

產物約 5–10 MB。

---

## 3. Windows Service 部署（最常見）

### 3.1 安裝

1. 把 `publish/win-x64/` 整個複製到目標機，例如 `C:\Apps\EtlTool\`
2. 建立資料目錄並指派權限：
   ```powershell
   New-Item -ItemType Directory -Path C:\ProgramData\EtlTool\keys -Force
   New-Item -ItemType Directory -Path C:\ProgramData\EtlTool\logs -Force

   # 將服務帳戶（例如 NT SERVICE\EtlTool 或網域帳戶）加上完整控制
   icacls C:\ProgramData\EtlTool /grant "NT SERVICE\EtlTool:(OI)(CI)F"
   ```
3. 註冊服務：
   ```powershell
   sc.exe create EtlTool binPath= "C:\Apps\EtlTool\EtlTool.App.exe" start= auto
   sc.exe description EtlTool "Oracle/MSSQL ETL Scheduler"
   sc.exe failure EtlTool reset= 86400 actions= restart/30000/restart/60000/restart/120000

   # 設定環境變數（讓 app 知道資料目錄）
   [System.Environment]::SetEnvironmentVariable(
     "ETLTOOL_DATA_DIR", "C:\ProgramData\EtlTool", "Machine")

   sc.exe start EtlTool
   ```
4. 確認運作：
   ```powershell
   Invoke-WebRequest http://localhost:5247/healthz
   ```

### 3.2 啟停與移除

```powershell
sc.exe stop EtlTool
sc.exe start EtlTool
sc.exe delete EtlTool         # 移除註冊（不會刪檔案）
```

### 3.3 改用網域帳戶執行

預設 `LocalSystem` 權限過大。建議：
1. 建一個專用帳戶 `DOMAIN\svc-etltool`（或本機使用者）
2. 給該帳戶「以服務方式登入」權限
3. 給該帳戶 `C:\Apps\EtlTool` 讀取權、`C:\ProgramData\EtlTool` 完整控制
4. `sc.exe config EtlTool obj= "DOMAIN\svc-etltool" password= "<password>"`

---

## 4. Linux systemd 部署

### 4.1 安裝

```bash
sudo useradd -r -s /usr/sbin/nologin etltool
sudo mkdir -p /opt/etltool /var/lib/etltool/{keys,logs}
sudo cp -r publish/linux-x64/* /opt/etltool/
sudo chown -R etltool:etltool /opt/etltool /var/lib/etltool
sudo chmod +x /opt/etltool/EtlTool.App
```

### 4.2 systemd unit

建立 `/etc/systemd/system/etltool.service`：

```ini
[Unit]
Description=ETL Tool (Oracle <-> MSSQL scheduler)
After=network.target

[Service]
Type=notify
User=etltool
Group=etltool
WorkingDirectory=/opt/etltool
ExecStart=/opt/etltool/EtlTool.App
Restart=on-failure
RestartSec=10
KillSignal=SIGINT
Environment=ASPNETCORE_URLS=http://0.0.0.0:5247
Environment=ETLTOOL_DATA_DIR=/var/lib/etltool
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

# Hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ReadWritePaths=/var/lib/etltool
ProtectHome=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true

[Install]
WantedBy=multi-user.target
```

啟用：

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now etltool
sudo systemctl status etltool
sudo journalctl -u etltool -f
```

---

## 5. Docker 部署

### 5.1 Dockerfile（多階段建置）

倉庫根目錄已附 `Dockerfile`，使用：

```bash
docker build -t etltool:latest .
docker run -d --name etltool \
  -p 5247:5247 \
  -e ETLTOOL_DATA_DIR=/data \
  -v etltool-data:/data \
  --restart unless-stopped \
  etltool:latest
```

### 5.2 docker-compose（含 ETL 服務 + 範例 DB）

正式環境通常不會把 DB 一起塞進 compose。範例僅作參考：

```yaml
services:
  etltool:
    image: etltool:latest
    ports: ["5247:5247"]
    environment:
      ASPNETCORE_URLS: "http://0.0.0.0:5247"
      ETLTOOL_DATA_DIR: /data
    volumes:
      - etltool-data:/data
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://localhost:5247/healthz"]
      interval: 30s
      timeout: 5s
      retries: 3

volumes:
  etltool-data:
```

> **絕對重要**：`/data` 必須掛 volume。Container 重建時，**內部資料會消失**，包含 Data Protection 金鑰，所有既存連線字串會無法解密。

---

## 6. 設定 (Configuration)

### 6.1 設定來源優先序

由低到高（後者覆蓋前者）：

1. `appsettings.json`
2. `appsettings.{Environment}.json` (`Development` / `Production`)
3. 環境變數
4. 命令列引數 (`--Foo:Bar=value`)

### 6.2 重要設定鍵

| 鍵 | 預設 | 說明 |
|---|---|---|
| `DataDirectory` | `<ContentRoot>/data` | SQLite、keys、logs 的根目錄 |
| `ETLTOOL_DATA_DIR` (env) | — | 同上，env 優先序高於 appsettings |
| `ASPNETCORE_URLS` (env) | `http://localhost:5247` | 監聽位址。生產綁 `0.0.0.0` 並用反向代理 |
| `ASPNETCORE_ENVIRONMENT` (env) | `Production` | 控制 Development 額外資訊 |
| `Logging:LogLevel:Default` | `Information` | 改 `Warning` 可降量 |

### 6.3 設定範例（生產 `appsettings.Production.json`）

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  },
  "DataDirectory": "/var/lib/etltool"
}
```

### 6.4 Cron 表達式語法

Quartz.NET 6/7 欄位：`秒 分 時 日 月 週 [年]`。常用：

| Cron | 含義 |
|---|---|
| `0 */5 * * * ?` | 每 5 分鐘 |
| `0 0 * * * ?` | 每小時整點 |
| `0 0 2 * * ?` | 每天 02:00 |
| `0 0 3 ? * MON` | 每週一 03:00 |
| `0 0 0 1 * ?` | 每月 1 日 00:00 |

UI 內建驗證 + 顯示下次觸發時間。

---

## 7. 資料目錄結構

```
<DataDirectory>/
├── etltool.db          ← SQLite：連線、任務、映射、執行歷史
├── etltool.db-wal      ← WAL（執行中可能存在）
├── etltool.db-shm
├── keys/               ← Data Protection 金鑰（連線字串解密用）
│   └── key-<guid>.xml
└── logs/
    └── etltool-YYYYMMDD.log
```

---

## 8. ⚠ 備份策略（最重要的章節）

### 8.1 SQLite 備份

**冷備份**（停服務）— 最簡單最可靠：

```bash
sudo systemctl stop etltool
cp -p /var/lib/etltool/etltool.db /backup/etltool-$(date +%Y%m%d).db
sudo systemctl start etltool
```

**熱備份**（不停服務）— 用 SQLite 內建命令：

```bash
sqlite3 /var/lib/etltool/etltool.db ".backup /backup/etltool-$(date +%Y%m%d).db"
```

排程建議：cron 每天備份，保留 14 天。

### 8.2 ⚠ Data Protection 金鑰備份（同等重要）

連線字串以 Data Protection API 加密，金鑰落在 `<DataDirectory>/keys/`。

**如果只備了 SQLite 而沒備金鑰，還原後所有連線字串將無法解密、必須全部重建。**

```bash
# 備份金鑰
tar czf /backup/etltool-keys-$(date +%Y%m%d).tar.gz \
    -C /var/lib/etltool keys/

# 還原時：先還原 keys，再還原 db
```

### 8.3 還原演練

每季至少做一次：

1. 把備份檔搬到測試機
2. 還原 keys + db 到同樣的 `DataDirectory`
3. 啟動 app，到「連線」頁按「測試連線」
4. 觸發一個任務驗證

### 8.4 自動化備份 cron 範例

```bash
# /etc/cron.d/etltool-backup
0 2 * * * etltool sqlite3 /var/lib/etltool/etltool.db ".backup /backup/etltool-$(date +\%Y\%m\%d).db" && tar czf /backup/etltool-keys-$(date +\%Y\%m\%d).tar.gz -C /var/lib/etltool keys/ && find /backup -name "etltool-*" -mtime +14 -delete
```

---

## 9. 反向代理 / HTTPS

App 預設只起 HTTP。生產應放在反向代理後做 TLS 終止 + 存取控制。

### 9.1 nginx 範例

```nginx
upstream etltool {
    server 127.0.0.1:5247;
}

server {
    listen 443 ssl http2;
    server_name etl.example.com;

    ssl_certificate /etc/letsencrypt/live/etl.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/etl.example.com/privkey.pem;

    # IP 白名單（公司辦公網）
    allow 203.0.113.0/24;
    deny all;

    location / {
        proxy_pass http://etltool;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;

        # Blazor Server 用 SignalR (WebSocket)
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 1d;
        proxy_send_timeout 1d;
    }
}
```

### 9.2 IIS（Windows）— 子應用程式 / 共用 port 443

最常見的企業內網部署：把 EtlTool 掛在既有 IIS 站台底下當 **Web Application（子應用程式）**，共用該站台的 HTTPS 憑證與 port 443。

```
https://intranet.example.com/         ← 既有 Default Web Site
https://intranet.example.com/etltool/ ← 本 App，指向 C:\Apps\EtlTool
```

#### 9.2.1 前置條件

1. **裝 .NET 10 Hosting Bundle** —— 含 ANCM v2 (`AspNetCoreModuleV2`)
   <https://dotnet.microsoft.com/download/dotnet/10.0> → ASP.NET Core Runtime → Hosting Bundle
2. **啟用 IIS Application Initialization 功能**（Quartz 排程必要 — 沒它 idle 後排程會停）
   ```
   伺服器管理員 → 新增角色及功能 → 網頁伺服器 (IIS) → 應用程式開發 → 應用程式初始化
   ```
3. 確認父站台已綁好 https + 憑證

#### 9.2.2 一鍵部署腳本

`deploy/iis/install-iis-subapp.ps1` 已備好，**以系統管理員身分**執行：

```powershell
# 1. 先發佈
dotnet publish src/EtlTool.App -c Release -r win-x64 --self-contained true `
  -o C:\Apps\EtlTool

# 2. 安裝為子應用程式
.\deploy\iis\install-iis-subapp.ps1 `
  -PublishPath  "C:\Apps\EtlTool" `
  -ParentSite   "Default Web Site" `
  -VirtualPath  "etltool" `
  -DataPath     "C:\ProgramData\EtlTool"
  # 預設用 ApplicationPoolIdentity；若要網域帳戶加 -AppPoolIdentity "DOMAIN\svc-etltool"
```

腳本會：
- 建立 `EtlToolPool` 應用程式池（`startMode=AlwaysRunning`、`idleTimeout=0`，**不 recycle**）
- 建立子應用程式並指向發佈目錄
- 在 `web.config` 寫入 `ETLTOOL_PATH_BASE=/etltool`、`ETLTOOL_DATA_DIR=C:\ProgramData\EtlTool`
- 授權應用程式池帳戶寫入資料目錄
- 觸發預熱呼叫 `/etltool/healthz`

部署後直接訪問 `https://intranet.example.com/etltool/`。

#### 9.2.3 手動部署（不想用腳本時）

若無法執行 PowerShell 腳本，手動步驟：

1. **發佈**：`dotnet publish src/EtlTool.App -c Release -o C:\Apps\EtlTool`
2. **覆蓋 web.config**：把 `deploy/iis/web.config` 內的 `<applicationInitialization>` 與 `<environmentVariables>` 段合併到發佈產生的 `web.config`，並改：
   ```xml
   <environmentVariable name="ETLTOOL_PATH_BASE" value="/etltool" />
   <environmentVariable name="ETLTOOL_DATA_DIR"  value="C:\ProgramData\EtlTool" />
   ```
3. **建立 App Pool**（IIS 管理員 → 應用程式集區 → 新增）：
   - .NET CLR 版本：**沒有 Managed Code**
   - 進階設定：
     - **啟動模式** = `AlwaysRunning`
     - **Idle Time-out** = `0`
     - **Regular Time Interval (minutes)** = `0`（停用 recycle）
4. **新增 Web Application**（在父站台上右鍵 → 新增應用程式）：
   - 別名：`etltool`
   - 應用程式集區：上面建的
   - 實體路徑：`C:\Apps\EtlTool`
5. **授權資料目錄**給應用程式池：
   ```powershell
   icacls C:\ProgramData\EtlTool /grant "IIS AppPool\EtlToolPool:(OI)(CI)F" /T
   ```
6. **預熱**：訪問 `https://intranet.example.com/etltool/healthz` 一次

#### 9.2.4 為何要 AlwaysRunning + Idle Time-out=0

EtlTool 在進程內跑 Quartz scheduler。IIS 預設會在 idle 20 分鐘後回收 worker process，**這會殺掉所有排程**。配置上述設定才會：

- 應用程式池一啟動就建立 worker process（`AlwaysRunning`）
- 沒人請求也不會回收（`idleTimeout=0`）
- 不定期重啟（`periodicRestart=0`）

這樣 Quartz 才能持續監看 cron 觸發。

#### 9.2.5 IIS 模式 vs Windows Service 模式

兩種模式互斥，**只選一個**：

| 模式 | 場景 | 啟動者 | 共用 port 443 | 排程穩定度 |
|---|---|---|---|---|
| Windows Service | 獨立服務 | sc.exe | ❌ 自己佔 5247 | ★★★ 最穩定 |
| IIS 子應用程式 | 共用既有站台 | IIS w3wp.exe | ✅ | ★★ 需正確配置 App Pool |

#### 9.2.6 升級

```powershell
# 1. 停 App Pool
Stop-WebAppPool -Name EtlToolPool

# 2. 備份資料
Copy-Item -Path C:\ProgramData\EtlTool -Destination "C:\ProgramData\EtlTool.bak.$(Get-Date -Format yyyyMMdd-HHmm)" -Recurse

# 3. 覆蓋發佈檔
dotnet publish src\EtlTool.App -c Release -o C:\Apps\EtlTool

# 4. 重啟（會自動套 migration）
Start-WebAppPool -Name EtlToolPool
Invoke-WebRequest -Uri https://intranet.example.com/etltool/healthz
```

---

## 10. 健康檢查 / 監控

### 10.1 Health 端點

```
GET /healthz
```

回應：

```
HTTP/1.1 200 OK
Healthy
```

含 `AddDbContextCheck<AppDbContext>` 會驗 SQLite 可開可查。失敗時回 503。

### 10.2 Log

- 主檔案：`<DataDirectory>/logs/etltool-YYYYMMDD.log`，每日 rolling，保留 14 天
- Console 同步輸出（systemd journal / Event Log 都看得到）

### 10.3 監控建議

| 指標 | 來源 | 告警條件 |
|---|---|---|
| App 存活 | `/healthz` HTTP 200 | 連續 3 次非 200 |
| RunHistory.Failed | SQLite query | 5 分鐘內 ≥ 1 筆 |
| 磁碟使用 | OS 級監控 | data 目錄 > 80% |
| log 中 ERROR | 日誌收集 | 任何 ERROR 行 |

可用 Prometheus blackbox_exporter 或 UptimeKuma 監 `/healthz`。

---

## 11. 升級流程

### 11.1 In-place 升級（停機升級）

```bash
sudo systemctl stop etltool
cp -r publish/linux-x64/* /opt/etltool/   # 覆寫程式
sudo systemctl start etltool
```

EF Core migration 會在 `StartupBootstrapper` 自動套用，無需手動。

### 11.2 滾動驗證

升級後立刻：
1. `curl /healthz` 應 200
2. 進 UI 看 Dashboard 任務數正常
3. 等 1 個排程週期，看 RunHistory 有新成功紀錄

### 11.3 Rollback

最快方式：
```bash
sudo systemctl stop etltool
# 把 /opt/etltool 換回前一版產物
sudo systemctl start etltool
```

如果有跑過新版的 EF migration，rollback 程式時 SQLite schema 仍是新版 — 這版只新增欄位（不破壞），舊程式仍可正常運作；但**避免從新版直接降兩版以上**。

---

## 12. 安全檢查清單

部署到生產前逐條確認：

- [ ] `ASPNETCORE_URLS` 沒綁到 `0.0.0.0` 直接出公網（除非反向代理在前）
- [ ] 反向代理或防火牆限制了存取來源 IP
- [ ] 服務帳戶**不是** root / Administrator / LocalSystem
- [ ] DataDirectory 權限只開給服務帳戶（chmod 700 / icacls 限縮）
- [ ] 連線字串中的 DB 帳戶為**最小權限**（僅來源 SELECT、目標必要的 DML）
- [ ] SQLite + keys 同時納入備份排程
- [ ] 還原備份的演練過至少一次
- [ ] 監控 `/healthz` 與磁碟用量
- [ ] log 沒被外部讀取（含路徑保護）
- [ ] **理解目前無內建身分驗證**，已用其他機制（VPN / SSO 反代）控管

---

## 13. 常見排錯

### 13.1 啟動失敗：找不到 SQLite

```
Microsoft.Data.Sqlite.SqliteException: SQLite Error 14: 'unable to open database file'
```

→ DataDirectory 不存在或服務帳戶無寫入權。確認 `mkdir` + `chown`。

### 13.2 連線字串無法解密

```
System.Security.Cryptography.CryptographicException: The key {GUID} was not found in the key ring.
```

→ 還原備份時忘了還原 `keys/` 目錄；或服務帳戶換過、新帳戶沒 keys 目錄讀權。
唯一解：**重新建立連線**（既有的 EncryptedConnectionString 已無法救回），或從備份還原 keys。

### 13.3 Quartz job 不觸發

排序檢查：
1. `/healthz` 有 200 嗎？沒 → app 沒起來
2. 任務 `Enabled=true` 嗎？任務列表上「啟用」開關
3. 任務 cron 表達式有效嗎？任務編輯頁 Cron 區塊會即時顯示「下次觸發」
4. 看 log：`Scheduler initialized with N active tasks`，N 對嗎？

注意 Quartz 用 RAMJobStore，**App 重啟後從 SQLite 重建 job 表**。如果 task 在 SQLite 裡 `Enabled=false`，它根本不會被註冊。

### 13.4 來源/目標 schema 抓不到

- Oracle：服務帳戶需 `SELECT ANY DICTIONARY` 或對 `ALL_TABLES / ALL_TAB_COLUMNS` 有讀權
- MSSQL：服務帳戶在目標 DB 是 `db_datareader` + `VIEW DEFINITION`

### 13.5 Upsert 失敗 `CommandText property has not been initialized` / 參數不符

通常是 mapping 與目標表 schema 漂移（目標表新增了 NOT NULL 欄位但 mapping 沒提供）。
解：到任務編輯頁「同名自動配對」按一下重新對齊，或補手動映射。

---

## 14. 升級到下一版要注意

當倉庫加入內建身分驗證後（roadmap 中），升級程式版本時會額外要：
1. 設 `Auth:Username` / `Auth:PasswordHash` 於 appsettings.Production.json
2. 第一次登入後修改密碼
3. 反向代理層的存取控制可保留作為縱深防禦

屆時本文件會更新。

---

## 附錄：快速指令彙整

```bash
# 發佈 self-contained Linux x64
dotnet publish src/EtlTool.App -c Release -r linux-x64 --self-contained -o publish/linux

# 健康檢查
curl http://localhost:5247/healthz

# 看即時 log（systemd）
journalctl -u etltool -f

# 看即時 log（Windows Service）
Get-Content C:\ProgramData\EtlTool\logs\etltool-$(Get-Date -Format yyyyMMdd).log -Wait

# 備份（一行）
sqlite3 /var/lib/etltool/etltool.db ".backup /tmp/db.bak" && \
  tar czf /tmp/etltool-backup-$(date +%F).tgz \
    -C /var/lib/etltool keys/ -C /tmp db.bak

# 還原
sudo systemctl stop etltool
tar xzf /tmp/etltool-backup-2026-04-26.tgz -C /var/lib/etltool/
mv /var/lib/etltool/db.bak /var/lib/etltool/etltool.db
sudo chown -R etltool:etltool /var/lib/etltool
sudo systemctl start etltool
```
