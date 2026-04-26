-- Oracle 端「完成後呼叫 SP」驗證腳本
-- 建立：
--   HR.ETL_HOOK_LOG     — 記錄每次 SP 被呼叫的 log 表
--   HR.ON_ETL_COMPLETED — 接收標準參數寫進 log
-- 用法：
--   docker cp tests/scripts/oracle-etl-hook-sp.sql etltool-oracle:/tmp/etl-hook.sql
--   docker exec etltool-oracle bash -c \
--     'sqlplus -S system/oracle@//localhost:1521/XEPDB1 @/tmp/etl-hook.sql'

ALTER SESSION SET CURRENT_SCHEMA = HR;

-- 1) Log 表（每次 SP 被觸發插一列）
BEGIN EXECUTE IMMEDIATE 'DROP TABLE HR.ETL_HOOK_LOG PURGE'; EXCEPTION WHEN OTHERS THEN NULL; END;
/

CREATE TABLE HR.ETL_HOOK_LOG (
    LOG_ID         NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    INVOKED_AT     TIMESTAMP DEFAULT SYSTIMESTAMP,
    TASK_ID        VARCHAR2(50),
    TASK_NAME      VARCHAR2(200),
    RUN_ID         VARCHAR2(50),
    STATUS         VARCHAR2(20),
    ROWS_READ      NUMBER,
    ROWS_WRITTEN   NUMBER,
    STARTED_AT     DATE,
    FINISHED_AT    DATE,
    ERROR_MESSAGE  VARCHAR2(4000),
    TRIGGER_TYPE   VARCHAR2(20)
);

-- 2) Stored Procedure（參數名稱對齊 EtlEngine.InvokePostRunSpAsync 送出的 10 個 named param）
--    用 ON_ETL_COMPLETED.<param> 限定避免欄位/參數同名衝突
CREATE OR REPLACE PROCEDURE HR.ON_ETL_COMPLETED (
    task_id        IN VARCHAR2,
    task_name      IN VARCHAR2,
    run_id         IN VARCHAR2,
    status         IN VARCHAR2,
    rows_read      IN NUMBER,
    rows_written   IN NUMBER,
    started_at     IN DATE,
    finished_at    IN DATE,
    error_message  IN VARCHAR2,
    trigger_type   IN VARCHAR2
) AS
BEGIN
    INSERT INTO HR.ETL_HOOK_LOG (
        TASK_ID, TASK_NAME, RUN_ID, STATUS,
        ROWS_READ, ROWS_WRITTEN, STARTED_AT, FINISHED_AT,
        ERROR_MESSAGE, TRIGGER_TYPE
    ) VALUES (
        ON_ETL_COMPLETED.task_id,
        ON_ETL_COMPLETED.task_name,
        ON_ETL_COMPLETED.run_id,
        ON_ETL_COMPLETED.status,
        ON_ETL_COMPLETED.rows_read,
        ON_ETL_COMPLETED.rows_written,
        ON_ETL_COMPLETED.started_at,
        ON_ETL_COMPLETED.finished_at,
        ON_ETL_COMPLETED.error_message,
        ON_ETL_COMPLETED.trigger_type
    );
    COMMIT;
END;
/

-- 給 system 帳戶執行權（內網信任環境用 system 也 OK）
GRANT EXECUTE ON HR.ON_ETL_COMPLETED TO PUBLIC;
GRANT SELECT, INSERT ON HR.ETL_HOOK_LOG TO PUBLIC;

SHOW ERRORS PROCEDURE HR.ON_ETL_COMPLETED;
SELECT 'HR.ON_ETL_COMPLETED 建立完成' AS msg FROM DUAL;
