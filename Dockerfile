# syntax=docker/dockerfile:1
#
# 本アプリのコンテナイメージ（Questionnaire の Dockerfile に倣った）。
# **開発環境（ホスト）に .NET SDK も node も要求しない**ようにするためのもの。
# 実行は compose.yaml から行う。

# ---- 管理画面（Svelte） -----------------------------------------------------
# **ホストに node を要求しないため、ここでビルドする**
FROM node:24-alpine AS frontend
WORKDIR /frontend

COPY src/VehicleVision.PleasanterTools.CalendarFeed.Frontend/package.json ./
COPY src/VehicleVision.PleasanterTools.CalendarFeed.Frontend/package-lock.json ./
RUN npm ci

COPY src/VehicleVision.PleasanterTools.CalendarFeed.Frontend/ ./
# vite.config.ts の outDir は ../{Web}/wwwroot/admin を指すので、その位置を用意しておく
RUN mkdir -p /VehicleVision.PleasanterTools.CalendarFeed.Web && npm run build

# ---- サーバ -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 依存の解決だけ先に済ませてレイヤをキャッシュする
COPY global.json Directory.Build.props ./
COPY VehicleVision.PleasanterTools.CalendarFeed.slnx ./
COPY src/ src/
RUN dotnet restore src/VehicleVision.PleasanterTools.CalendarFeed.Web

RUN dotnet publish src/VehicleVision.PleasanterTools.CalendarFeed.Web \
        -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# 設定は Pleasanter と同じく App_Data/Parameters/*.json 方式（publish に含まれる）。
# **資格情報の実値は入れない。** 環境変数か Key Vault から読む
COPY --from=build /app/publish .
# 管理画面のビルド成果物
COPY --from=frontend /VehicleVision.PleasanterTools.CalendarFeed.Web/wwwroot/admin ./wwwroot/admin

# SQLite の既定の置き場（compose.yaml の app-data ボリューム）。**非 root の実行ユーザーが書けるようにしておく。**
# 名前付きボリュームは、初めて使うときにイメージ側の所有者を引き継ぐ（これが無いと root 所有になり、
# 「unable to open database file」で起動しない。compose で実際に踏んだ）
RUN mkdir -p /data && chown "$APP_UID" /data

# **root で動かさない。**
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "VehicleVision.PleasanterTools.CalendarFeed.Web.dll"]
