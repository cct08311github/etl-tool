# ETL Tool — Oracle ↔ MS SQL 雙向定時 ETL

可在 Web UI 上設定資料庫連線、欄位映射、篩選條件、排程，把資料定時從來源 A 表搬到目標 B 表。
支援兩種寫入模式：

- **Delete-Insert**：依條件刪除目標表的資料，再批次插入
- **Upsert**：依主鍵欄位，存在更新、不存在新增

每次執行整段以目標 DB 的單一 transaction 包覆，失敗整個 rollback。

## 技術棧

- .NET 10 (LTS) + ASP.NET Core + Blazor Server
- Quartz.NET 排程器（內建於同一個 process）
- EF Core + SQLite（任務、連線、執行歷史落地）
- ADO.NET providers：`Oracle.ManagedDataAccess.Core`、`Microsoft.Data.SqlClient`
- DynamicExpresso（欄位轉換表達式）
- Serilog（檔案 + console）
- ASP.NET Core Data Protection（連線字串加密）

## 專案結構

```
src/
  EtlTool.App/                Blazor Server UI + 主機進入點 + Quartz 註冊
  EtlTool.Core/               領域模型、ETL 引擎、排程 Job、抽象介面
  EtlTool.Connectors/         Oracle / MSSQL 的 IDbConnector 實作 + ConnectionStringHelper
  EtlTool.Data/               EF Core SQLite + repositories + Data Protection
tests/
  EtlTool.Tests/              單元測試（FilterCompiler, TransformEvaluator, FilterTreeJson — 13 cases）
  EtlTool.IntegrationTests/   E2E 整合測試（依賴 docker-compose 起的 Oracle + MSSQL — 9 cases）
  scripts/                    seed-oracle.sql / seed-mssql.sql 種子資料
```

`EtlTool.Core` 不依賴 `EtlTool.Connectors` 或 `EtlTool.Data` —— 透過 DI 注入抽象介面，連線字串加解密、連線查找、執行歷史落地都由 Data 層實作。

## 本機開發

### 1. 起兩家測試 DB

```bash
docker compose -f docker-compose.dev.yml up -d
```

- Oracle：`localhost:1521/XEPDB1`，預設 `system/oracle`（HR schema 自動建好）
- SQL Server：`localhost:1433`，預設 `sa/Dev_Password1!`

### 1.1 種測試資料（給 E2E）

```bash
docker cp tests/scripts/seed-oracle.sql etltool-oracle:/tmp/seed.sql
docker exec etltool-oracle bash -c 'sqlplus -S system/oracle@//localhost:1521/XEPDB1 @/tmp/seed.sql'

docker cp tests/scripts/seed-mssql.sql etltool-mssql:/tmp/seed.sql
docker exec etltool-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Dev_Password1!" -No -i /tmp/seed.sql
```

兩邊各會建立 `EMPLOYEES_SRC`（5 列種子）+ `EMPLOYEES_TGT`（空表）。

### 1.2 跑測試

```bash
dotnet test tests/EtlTool.Tests             # 13 個單元測試（不需 DB）
dotnet test tests/EtlTool.IntegrationTests  # 9 個 E2E 測試（需 docker-compose 起好）
```

E2E 測試涵蓋：

- Test connection 對 Oracle / MSSQL 雙方
- List schemas / tables / columns / PK 偵測
- Oracle → MSSQL DeleteInsert（form 篩選）
- DeleteInsert 重跑會替換同條件的舊資料（不污染其他條件的資料）
- Oracle → MSSQL Upsert（驗證更新 + 新增同時生效）
- MSSQL → Oracle DeleteInsert（反方向）
- 欄位轉換表達式（DynamicExpresso 把 `alice` 大寫成 `ALICE`）
- Raw SQL 篩選模式
- 失敗 rollback：故意產生 PK = NULL 觸發 INSERT 失敗，驗證目標表不被部分修改

### 2. 跑 App

```bash
dotnet run --project src/EtlTool.App
```

預設網址 http://localhost:5247

第一次跑會自動：

- 建立 SQLite 資料庫於 `src/EtlTool.App/data/etltool.db`
- 建立 Data Protection 金鑰於 `src/EtlTool.App/data/keys/`
- 建立 log 於 `src/EtlTool.App/data/logs/etltool-YYYYMMDD.log`

可用環境變數 `ETLTOOL_DATA_DIR` 或 `appsettings.json` 的 `DataDirectory` 覆寫資料目錄。

### 3. 操作流程

1. **連線管理**：建立 Oracle / MSSQL 連線，按「測試連線」確認
2. **新增任務**：選來源/目標連線、schema、table
3. **欄位映射**：按「同名自動配對」，需要時手動微調 / 加轉換表達式
4. **寫入模式**：Delete-Insert 或 Upsert（後者必勾主鍵欄位）
5. **篩選條件**：表單模式或進階 SQL
6. **排程**：選預設 cron 或自訂；儲存後自動向 Quartz 註冊
7. **預覽前 5 筆**（Dry Run）：儲存前可按此按鈕，依目前篩選對來源跑一次 `SELECT TOP 5`，在 modal 中看實際拿到的 raw 資料 — 不寫入目標、不開 transaction
8. **儲存驗證**：Save 前會檢查名稱、來源/目標完整性、映射重複、Upsert 主鍵、Cron 表達式、批次大小範圍，問題會列在表單上方 alert
9. **立即執行 / 歷史**：任務列表上點「▶」手動觸發；點「歷史」看每次的 SQL、筆數、錯誤、sample payload

## 部署

完整部署指南、備份策略、反向代理設定、故障排除請見 **[DEPLOYMENT.md](DEPLOYMENT.md)**。

簡述三種模式：

- **Windows Service**（最常見）：`dotnet publish -r win-x64 --self-contained` 後用 [`deploy/install-windows-service.ps1`](deploy/install-windows-service.ps1) 一鍵安裝
- **Linux systemd**：`dotnet publish -r linux-x64 --self-contained` 後套用 [`deploy/etltool.service`](deploy/etltool.service)
- **Docker**：根目錄 [`Dockerfile`](Dockerfile)，`docker build -t etltool . && docker run -p 5247:5247 -v etltool-data:/data etltool`

健康檢查端點：`GET /healthz`（含 SQLite 連線檢查，503 表示 DB 異常）。

> ⚠ **目前無內建身分驗證**，請部署在內網信任區段，並用反向代理 / VPN / IP 白名單做存取控制。詳見 [DEPLOYMENT.md §12 安全檢查清單](DEPLOYMENT.md)。

## 安全性

- 連線字串以 ASP.NET Core Data Protection 加密後落地 SQLite
- 金鑰落地至 `<DataDir>/keys`，目錄 ACL 應限縮為服務帳戶
- 沒有使用者帳號 / 認證 —— 預期跑在內網信任環境，UI port 別暴露到公網
- 篩選條件「進階 SQL」模式直接拼進 SQL，請信任輸入來源；表單模式為參數化，無注入風險

## 常見問題

**Q：可以把 SQLite 設定檔搬到 MS SQL 嗎？**
目前固定 SQLite，要切換需改 `Program.cs` 的 `AddDbContext` + 跑對應 provider 的 migration。

**Q：Cron 表達式格式？**
Quartz.NET 風格，6~7 欄位（秒 分 時 日 月 週 [年]）。範例：

- 每 5 分鐘：`0 0/5 * * * ?`
- 每天 02:00：`0 0 2 * * ?`

**Q：Upsert 沒勾主鍵會怎樣？**
任務執行時會直接拋 `Upsert 模式需至少勾選一個主鍵欄位`，整段 rollback。
