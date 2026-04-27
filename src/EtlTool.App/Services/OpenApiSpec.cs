namespace EtlTool.App.Services;

/// <summary>
/// 手寫 OpenAPI 3.0 spec for /api/* endpoints。
///
/// 為什麼不用 Swashbuckle / NSwag 自動生？
///   1. 我們的 endpoint 是 minimal API + 動態 anonymous-typed JSON，自動產出的 schema 很弱
///   2. 銀行客戶喜歡看「人寫的、可被 review 的」契約，不愛看 generator 的 noise
///   3. 手寫的好處是：增刪 endpoint 必須同步改這個檔案 → 文件不會 drift
///
/// 增改 endpoint 時務必同步維護此 YAML，並在 review 時要求 reviewer 確認。
/// 對 servers 的 URL 我們留空 placeholder（{baseUrl}），讓使用者在自己環境填。
/// </summary>
internal static class OpenApiSpec
{
    public const string Yaml = """
openapi: 3.0.3
info:
  title: EtlTool Read-only Monitoring API
  version: "1.0"
  description: |
    Read-only JSON API for external monitoring, dashboards, and audit drill-down.
    All endpoints require:
      - source IP in `Auth:AdminIpAllowlist` (if configured), AND
      - valid `X-Api-Key` header or `Authorization: Bearer <key>` (if `Api:Keys` configured), AND
      - per-IP rate limit (default 60 req/min; tunable via `Api:RateLimitPerMinute`).
    No write operations are exposed via this surface — task CRUD is UI-only by design.
  contact:
    name: EtlTool Operations
servers:
  - url: "{baseUrl}"
    description: Self-hosted instance (substitute your scheme://host[:port])
    variables:
      baseUrl:
        default: http://localhost:5247
security:
  - ApiKeyHeader: []
  - BearerAuth: []
paths:
  /api/health:
    get:
      summary: Liveness + component health detail
      description: |
        Same payload as `/healthz/detail`. Returns a JSON document with
        component-level status (db, quartz scheduler, connection monitor,
        audit write latency, backup freshness).
      tags: [health]
      responses:
        "200":
          description: Health snapshot (status field per component)
          content:
            application/json:
              schema:
                type: object
                properties:
                  status: { type: string, example: healthy }
                  components: { type: object }
        "401": { $ref: "#/components/responses/Unauthorized" }
        "429": { $ref: "#/components/responses/RateLimited" }

  /api/tasks/last-run:
    get:
      summary: Snapshot of all tasks with last-run summary
      description: |
        Returns one record per ETL task with last-success / last-failure
        timestamps and 30-day SLA percentage. Sorted by task name.
      tags: [tasks]
      responses:
        "200":
          description: Task snapshot list
          content:
            application/json:
              schema:
                type: object
                properties:
                  generatedAt: { type: string, format: date-time }
                  count: { type: integer }
                  tasks:
                    type: array
                    items:
                      $ref: "#/components/schemas/TaskSummary"
        "401": { $ref: "#/components/responses/Unauthorized" }
        "429": { $ref: "#/components/responses/RateLimited" }

  /api/tasks/{taskId}:
    get:
      summary: Single task detail with last 10 runs
      tags: [tasks]
      parameters:
        - name: taskId
          in: path
          required: true
          schema: { type: string, format: uuid }
      responses:
        "200":
          description: Task detail
          content:
            application/json:
              schema:
                $ref: "#/components/schemas/TaskDetail"
        "401": { $ref: "#/components/responses/Unauthorized" }
        "404": { $ref: "#/components/responses/NotFound" }
        "429": { $ref: "#/components/responses/RateLimited" }

  /api/tasks/{taskId}/runs:
    get:
      summary: Paginated run history for one task
      description: |
        Returns RunHistory rows for the given task in reverse chronological
        order. Use this for audit drill-down or downstream warehousing.
        `size` is capped at 200 to prevent DOS.
      tags: [tasks]
      parameters:
        - name: taskId
          in: path
          required: true
          schema: { type: string, format: uuid }
        - name: page
          in: query
          required: false
          schema: { type: integer, minimum: 1, default: 1 }
        - name: size
          in: query
          required: false
          schema: { type: integer, minimum: 1, maximum: 200, default: 20 }
      responses:
        "200":
          description: Paginated runs
          content:
            application/json:
              schema:
                type: object
                properties:
                  taskId: { type: string, format: uuid }
                  taskName: { type: string }
                  page: { type: integer }
                  size: { type: integer }
                  total: { type: integer }
                  totalPages: { type: integer }
                  runs:
                    type: array
                    items:
                      $ref: "#/components/schemas/RunHistoryItem"
        "401": { $ref: "#/components/responses/Unauthorized" }
        "404": { $ref: "#/components/responses/NotFound" }
        "429": { $ref: "#/components/responses/RateLimited" }

  /api/openapi.yaml:
    get:
      summary: This OpenAPI specification
      tags: [meta]
      responses:
        "200":
          description: OpenAPI 3.0 YAML
          content:
            application/yaml: {}

components:
  securitySchemes:
    ApiKeyHeader:
      type: apiKey
      in: header
      name: X-Api-Key
      description: |
        Configured via `Api:Keys[]` in appsettings.json or environment.
        Comparison is constant-time (FixedTimeEquals) over SHA-256 hashes.
    BearerAuth:
      type: http
      scheme: bearer
      bearerFormat: api-key
      description: |
        Same key value as ApiKeyHeader, just sent via standard Authorization header.
        Useful for tooling that natively supports OAuth-style auth.

  responses:
    Unauthorized:
      description: Missing or invalid API key
      content:
        application/json:
          schema:
            type: object
            properties:
              error: { type: string, example: missing or invalid X-Api-Key header }
    NotFound:
      description: Task not found
      content:
        application/json:
          schema:
            type: object
            properties:
              error: { type: string, example: task not found }
    RateLimited:
      description: Rate limit exceeded (default 60 req/min/IP)

  schemas:
    TaskSummary:
      type: object
      properties:
        id: { type: string, format: uuid }
        name: { type: string }
        enabled: { type: boolean }
        autoDisabledAt: { type: string, format: date-time, nullable: true }
        autoDisabledReason: { type: string, nullable: true }
        cron: { type: string }
        tags: { type: string, nullable: true, description: comma-separated }
        lastSuccess: { type: string, format: date-time, nullable: true }
        lastFailure: { type: string, format: date-time, nullable: true }
        sla30d:
          type: object
          nullable: true
          properties:
            successRate: { type: number, format: double }
            success: { type: integer }
            total: { type: integer }

    TaskDetail:
      allOf:
        - $ref: "#/components/schemas/TaskSummary"
        - type: object
          properties:
            cronDescription: { type: string }
            notes: { type: string, nullable: true }
            source:
              type: object
              properties:
                connectionId: { type: string, format: uuid }
                schema: { type: string, nullable: true }
                table: { type: string }
            target:
              type: object
              properties:
                connectionId: { type: string, format: uuid }
                schema: { type: string, nullable: true }
                table: { type: string }
            writeMode: { type: string, enum: [DeleteInsert, Upsert] }
            recentRuns:
              type: array
              items:
                $ref: "#/components/schemas/RunHistoryItem"

    RunHistoryItem:
      type: object
      properties:
        id: { type: string, format: uuid }
        startedAt: { type: string, format: date-time }
        finishedAt: { type: string, format: date-time, nullable: true }
        durationSec: { type: number, format: double }
        status: { type: string, enum: [Running, Success, Failed] }
        triggerType: { type: string, enum: [Scheduled, Manual] }
        rowsRead: { type: integer }
        rowsWritten: { type: integer }
        errorMessage: { type: string, nullable: true }
""";
}
