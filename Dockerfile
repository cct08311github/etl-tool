# syntax=docker/dockerfile:1.7
# =============================================================================
# EtlTool.App — production container image (.NET 10)
#
# 建置：    docker build -t etltool:latest .
# 跑 dev：  docker run --rm -p 5247:5247 \
#               -v $(pwd)/data:/data \
#               etltool:latest
# 跑 prod： docker run -d --name etltool --restart unless-stopped \
#               -p 5247:5247 \
#               -v /var/lib/etltool:/data \
#               -e ETLTOOL__Security__RequireHttps=true \
#               -e ETLTOOL__Api__Keys__0="<32+ char secret>" \
#               -e ETLTOOL__Webhooks__SigningSecret="<random hex>" \
#               -e ETLTOOL__Webhooks__OnFailure="https://hooks.slack.com/..." \
#               etltool:latest
#
# 銀行 ops 注意事項：
#   1. /data 必須是持久卷 — 內含 SQLite DB（任務 / 連線 / RunHistory）
#      + DataProtection keys（解密儲存的連線字串）+ 排程備份。
#      消失 = 所有設定遺失，加密的連線字串永遠無法救回。
#   2. ETLTOOL__ 前綴的 env var 透過 ASP.NET Core 設定系統覆寫 appsettings；
#      巢狀 key 用雙底線 __（如 Security:RequireHttps → Security__RequireHttps）。
#   3. Container 跑非 root user (etltool, uid auto)；bind mount 權限要對。
#   4. 前置反向代理 (IIS / nginx / Traefik) 終端 TLS 並轉送 X-Forwarded-Proto。
#   5. 建議搭配 readiness probe = /healthz，liveness 留空（讓服務自己重連）。
#
# 健康檢查：curl http://localhost:5247/healthz   (anonymous, 純 200/503)
# 詳細檢查：curl http://localhost:5247/healthz/detail  (component-level JSON)
# =============================================================================

ARG TARGETARCH

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先複製 csproj 觸發 NuGet cache 命中（修改原始碼時不重抓套件）
COPY EtlTool.slnx ./
COPY src/EtlTool.App/EtlTool.App.csproj         src/EtlTool.App/
COPY src/EtlTool.Core/EtlTool.Core.csproj       src/EtlTool.Core/
COPY src/EtlTool.Connectors/EtlTool.Connectors.csproj src/EtlTool.Connectors/
COPY src/EtlTool.Data/EtlTool.Data.csproj       src/EtlTool.Data/

# BuildKit cache mount — NuGet 套件快取在 build agent 上跨 build 重用
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/EtlTool.App/EtlTool.App.csproj

# 完整原始碼
COPY . .
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/EtlTool.App/EtlTool.App.csproj \
    -c Release -o /app/publish --no-restore

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# 非 root 使用者
RUN groupadd -r etltool && useradd -r -g etltool -d /data -s /usr/sbin/nologin etltool \
    && mkdir -p /data/keys /data/logs \
    && chown -R etltool:etltool /data

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS="http://0.0.0.0:5247" \
    ETLTOOL_DATA_DIR="/data" \
    DOTNET_PRINT_TELEMETRY_MESSAGE=false \
    DOTNET_RUNNING_IN_CONTAINER=true

USER etltool
VOLUME ["/data"]
EXPOSE 5247

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD wget -q --spider http://localhost:5247/healthz || exit 1

ENTRYPOINT ["./EtlTool.App"]
