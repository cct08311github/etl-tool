# syntax=docker/dockerfile:1.7
# Multi-stage build for EtlTool.App on .NET 10
# 用法: docker build -t etltool:latest .

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
RUN dotnet restore src/EtlTool.App/EtlTool.App.csproj

# 完整原始碼
COPY . .
RUN dotnet publish src/EtlTool.App/EtlTool.App.csproj \
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
