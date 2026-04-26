-- 建立壓力測試用的大量資料
-- DB: EtlPerf
-- 表：dbo.Orders_SRC (10,000+ 筆) / dbo.Orders_TGT (空)
-- 用法:
--   docker cp tests/scripts/seed-mssql-large.sql etltool-mssql:/tmp/seed-large.sql
--   docker exec etltool-mssql /opt/mssql-tools18/bin/sqlcmd \
--     -S localhost -U sa -P "Dev_Password1!" -No -i /tmp/seed-large.sql

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF DB_ID('EtlPerf') IS NULL
BEGIN
    PRINT '建立 EtlPerf 資料庫…';
    CREATE DATABASE EtlPerf;
END
GO
USE EtlPerf;
GO

IF OBJECT_ID('dbo.Orders_SRC') IS NOT NULL DROP TABLE dbo.Orders_SRC;
IF OBJECT_ID('dbo.Orders_TGT') IS NOT NULL DROP TABLE dbo.Orders_TGT;
GO

-- 訂單表：8 個典型欄位（PK + 客戶 + 部門 + 金額 + 狀態 + 時間 + 兩個自由文字）
CREATE TABLE dbo.Orders_SRC (
    OrderId        BIGINT       NOT NULL PRIMARY KEY,
    CustomerName   NVARCHAR(100) NOT NULL,
    DepartmentId   INT           NOT NULL,
    ProductCode    VARCHAR(20)   NOT NULL,
    Quantity       INT           NOT NULL,
    UnitPrice      DECIMAL(12,2) NOT NULL,
    TotalAmount    DECIMAL(14,2) NOT NULL,
    Status         VARCHAR(16)   NOT NULL,
    CreatedAt      DATETIME2(3)  NOT NULL,
    Notes          NVARCHAR(500) NULL
);

CREATE TABLE dbo.Orders_TGT (
    OrderId        BIGINT       NOT NULL PRIMARY KEY,
    CustomerName   NVARCHAR(100) NOT NULL,
    DepartmentId   INT           NOT NULL,
    ProductCode    VARCHAR(20)   NOT NULL,
    Quantity       INT           NOT NULL,
    UnitPrice      DECIMAL(12,2) NOT NULL,
    TotalAmount    DECIMAL(12,2) NULL,
    Status         VARCHAR(16)   NOT NULL,
    CreatedAt      DATETIME2(3)  NOT NULL,
    Notes          NVARCHAR(500) NULL
);
GO

DECLARE @rows INT = 10000;

;WITH n AS (
    SELECT TOP (@rows) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT INTO dbo.Orders_SRC
    (OrderId, CustomerName, DepartmentId, ProductCode, Quantity, UnitPrice, TotalAmount, Status, CreatedAt, Notes)
SELECT
    n                                                         AS OrderId,
    CONCAT(N'Customer-',
           CHAR(65 + (n % 26)),
           CHAR(65 + ((n / 26) % 26)),
           '-',
           RIGHT('00000' + CAST(n AS VARCHAR), 5))            AS CustomerName,
    1 + (n % 50)                                              AS DepartmentId,
    CONCAT('SKU-', RIGHT('00000' + CAST((n * 31) % 99999 AS VARCHAR), 5)) AS ProductCode,
    1 + (n % 100)                                             AS Quantity,
    CAST(10 + (n % 9990) + (n * 0.01) AS DECIMAL(12,2))       AS UnitPrice,
    CAST((1 + (n % 100)) * (10 + (n % 9990) + (n * 0.01)) AS DECIMAL(14,2)) AS TotalAmount,
    CASE n % 5
        WHEN 0 THEN 'pending'
        WHEN 1 THEN 'processing'
        WHEN 2 THEN 'shipped'
        WHEN 3 THEN 'completed'
        ELSE        'cancelled'
    END                                                       AS Status,
    DATEADD(MINUTE, -n, SYSUTCDATETIME())                     AS CreatedAt,
    CASE WHEN n % 7 = 0 THEN N'urgent — see notes' ELSE NULL END AS Notes
FROM n;
GO

-- 確認與索引
CREATE INDEX IX_Orders_SRC_DepartmentId_Status ON dbo.Orders_SRC (DepartmentId, Status) INCLUDE (CreatedAt);
CREATE INDEX IX_Orders_SRC_CreatedAt ON dbo.Orders_SRC (CreatedAt);
GO

SELECT
    'Orders_SRC'           AS [table],
    COUNT(*)               AS rows_inserted,
    MIN(OrderId)           AS min_id,
    MAX(OrderId)           AS max_id,
    COUNT(DISTINCT DepartmentId) AS distinct_depts,
    COUNT(DISTINCT Status) AS distinct_status,
    MIN(CreatedAt)         AS oldest,
    MAX(CreatedAt)         AS newest
FROM dbo.Orders_SRC;

SELECT TOP 5 * FROM dbo.Orders_SRC ORDER BY OrderId;
GO
