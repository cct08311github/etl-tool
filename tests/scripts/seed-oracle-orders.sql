-- 在 Oracle (HR schema) 建立對應 MSSQL.EtlPerf.dbo.Orders_SRC / Orders_TGT 的雙生表
-- 欄位名故意保留 MSSQL 的 CamelCase（用引號保大小寫）以便兩邊「同名自動配對」直接命中
-- 用法：
--   docker cp tests/scripts/seed-oracle-orders.sql etltool-oracle:/tmp/seed-orders.sql
--   docker exec etltool-oracle bash -c \
--     'sqlplus -S system/oracle@//localhost:1521/XEPDB1 @/tmp/seed-orders.sql'

ALTER SESSION SET CURRENT_SCHEMA = HR;

BEGIN EXECUTE IMMEDIATE 'DROP TABLE HR."Orders_SRC" PURGE'; EXCEPTION WHEN OTHERS THEN NULL; END;
/
BEGIN EXECUTE IMMEDIATE 'DROP TABLE HR."Orders_TGT" PURGE'; EXCEPTION WHEN OTHERS THEN NULL; END;
/

-- 對應 dbo.Orders_SRC：
--   BIGINT       → NUMBER(19)
--   INT          → NUMBER(10)
--   DECIMAL(12,2)→ NUMBER(12,2)
--   DECIMAL(14,2)→ NUMBER(14,2)
--   NVARCHAR(N)  → NVARCHAR2(N)
--   VARCHAR(N)   → VARCHAR2(N CHAR)
--   DATETIME2(3) → TIMESTAMP(3)
CREATE TABLE HR."Orders_SRC" (
    "OrderId"        NUMBER(19)        NOT NULL,
    "CustomerName"   NVARCHAR2(100)    NOT NULL,
    "DepartmentId"   NUMBER(10)        NOT NULL,
    "ProductCode"    VARCHAR2(20 CHAR) NOT NULL,
    "Quantity"       NUMBER(10)        NOT NULL,
    "UnitPrice"      NUMBER(12,2)      NOT NULL,
    "TotalAmount"    NUMBER(14,2)      NOT NULL,
    "Status"         VARCHAR2(16 CHAR) NOT NULL,
    "CreatedAt"      TIMESTAMP(3)      NOT NULL,
    "Notes"          NVARCHAR2(500)    NULL,
    CONSTRAINT "PK_Orders_SRC" PRIMARY KEY ("OrderId")
);

CREATE TABLE HR."Orders_TGT" (
    "OrderId"        NUMBER(19)        NOT NULL,
    "CustomerName"   NVARCHAR2(100)    NOT NULL,
    "DepartmentId"   NUMBER(10)        NOT NULL,
    "ProductCode"    VARCHAR2(20 CHAR) NOT NULL,
    "Quantity"       NUMBER(10)        NOT NULL,
    "UnitPrice"      NUMBER(12,2)      NOT NULL,
    "TotalAmount"    NUMBER(14,2)      NOT NULL,
    "Status"         VARCHAR2(16 CHAR) NOT NULL,
    "CreatedAt"      TIMESTAMP(3)      NOT NULL,
    "Notes"          NVARCHAR2(500)    NULL,
    CONSTRAINT "PK_Orders_TGT" PRIMARY KEY ("OrderId")
);

-- 種 10,000 筆到 SRC（Oracle 端的「來源」資料；目標 TGT 留空等 ETL 寫入）
INSERT INTO HR."Orders_SRC"
    ("OrderId", "CustomerName", "DepartmentId", "ProductCode",
     "Quantity", "UnitPrice", "TotalAmount", "Status", "CreatedAt", "Notes")
SELECT
    n,
    'Customer-'
        || CHR(65 + MOD(n, 26))
        || CHR(65 + MOD(TRUNC(n / 26), 26))
        || '-'
        || LPAD(TO_CHAR(n), 5, '0')                                                  AS CustomerName,
    1 + MOD(n, 50)                                                                    AS DepartmentId,
    'SKU-' || LPAD(TO_CHAR(MOD(n * 31, 99999)), 5, '0')                              AS ProductCode,
    1 + MOD(n, 100)                                                                   AS Quantity,
    ROUND(10 + MOD(n, 9990) + n * 0.01, 2)                                           AS UnitPrice,
    ROUND((1 + MOD(n, 100)) * (10 + MOD(n, 9990) + n * 0.01), 2)                     AS TotalAmount,
    CASE MOD(n, 5)
        WHEN 0 THEN 'pending'
        WHEN 1 THEN 'processing'
        WHEN 2 THEN 'shipped'
        WHEN 3 THEN 'completed'
        ELSE        'cancelled'
    END                                                                               AS Status,
    SYSTIMESTAMP - NUMTODSINTERVAL(n, 'MINUTE')                                       AS CreatedAt,
    CASE WHEN MOD(n, 7) = 0 THEN 'urgent — see notes' ELSE NULL END                   AS Notes
FROM (
    SELECT LEVEL AS n FROM DUAL CONNECT BY LEVEL <= 10000
);
COMMIT;

-- 索引
CREATE INDEX HR."IX_Orders_SRC_DeptStatus" ON HR."Orders_SRC" ("DepartmentId", "Status");
CREATE INDEX HR."IX_Orders_SRC_CreatedAt"  ON HR."Orders_SRC" ("CreatedAt");

-- 給 PUBLIC 看（簡化測試；正式應 grant 給特定角色）
GRANT SELECT, INSERT, UPDATE, DELETE ON HR."Orders_SRC" TO PUBLIC;
GRANT SELECT, INSERT, UPDATE, DELETE ON HR."Orders_TGT" TO PUBLIC;

-- 驗證
SET LINESIZE 200
SET PAGESIZE 50
COL t FORMAT A14
COL min_id FORMAT 99999
COL max_id FORMAT 99999
COL depts FORMAT 999
COL stati FORMAT 999

SELECT 'Orders_SRC' AS t, COUNT(*) AS rows_inserted,
       MIN("OrderId") AS min_id, MAX("OrderId") AS max_id,
       COUNT(DISTINCT "DepartmentId") AS depts, COUNT(DISTINCT "Status") AS stati
FROM HR."Orders_SRC"
UNION ALL
SELECT 'Orders_TGT', COUNT(*), MIN("OrderId"), MAX("OrderId"),
       COUNT(DISTINCT "DepartmentId"), COUNT(DISTINCT "Status")
FROM HR."Orders_TGT";

COL "OrderId" FORMAT 99999
COL "CustomerName" FORMAT A20
COL "ProductCode" FORMAT A14
COL "Status" FORMAT A12
SELECT * FROM (
    SELECT "OrderId", "CustomerName", "DepartmentId", "ProductCode",
           "Quantity", "UnitPrice", "TotalAmount", "Status"
    FROM HR."Orders_SRC" ORDER BY "OrderId"
) WHERE ROWNUM <= 5;
